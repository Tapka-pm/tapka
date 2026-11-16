using Microsoft.Data.Sqlite;

namespace Tapka;

internal sealed record InstalledPackage(string Id, string Version, string? Selector, string? Architecture,
    string? Repository, string? DownloadUrl, string? Checksum, string? InstalledAt, string? VersionChangedAt,
    string? RelativePath = null)
{
    public string PackagePath => RelativePath ?? Path.Combine("packages", Id);
    public bool IsPinned => Selector == Version;
}

internal readonly record struct PackageVersion(string Id, string Version);

internal sealed class InstalledPackages : IDisposable
{
    private const int SchemaVersion = 2;
    private const string FileName = "installed.db";
    private const string LegacyColumns =
        "id, version, selector, architecture, repository, download_url, checksum, installed_at, version_changed_at";
    private string Columns => LegacyColumns + (legacySchema ? ", NULL" : ", relative_path");
    private readonly SqliteConnection connection;
    private bool legacySchema;

    public static bool Exists(InstallScope scope) => File.Exists(Path.Combine(scope.Root, FileName));

    public static InstalledPackages? OpenSnapshot(InstallScope scope) => Exists(scope)
        ? OpenExisting(Path.Combine(scope.Root, FileName), SqliteOpenMode.ReadOnly) : null;

    private InstalledPackages(string path, SqliteOpenMode mode)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static async Task<InstalledPackages> OpenForReadAsync(InstallScope scope, CancellationToken cancellationToken)
    {
        // Existing registries can be queried while an installation is in progress.
        var path = Path.Combine(scope.Root, FileName);
        if (OpenSnapshot(scope) is { } existing)
        {
            return existing;
        }

        using var installLock = await scope.AcquireInstallLockAsync(cancellationToken);
        EnsureCreated(scope, path, cancellationToken);
        return OpenExisting(path, SqliteOpenMode.ReadOnly);
    }

    // The caller holds the scope's install lock.
    public static InstalledPackages OpenForInstall(InstallScope scope, CancellationToken cancellationToken)
    {
        var path = Path.Combine(scope.Root, FileName);
        EnsureCreated(scope, path, cancellationToken);
        return OpenExisting(path, SqliteOpenMode.ReadWrite);
    }

    private static InstalledPackages OpenExisting(string path, SqliteOpenMode mode)
    {
        var inventory = new InstalledPackages(path, mode);
        try
        {
            using var command = inventory.CreateCommand("PRAGMA user_version");
            var version = (long)command.ExecuteScalar()!;
            if (version == 1 && mode == SqliteOpenMode.ReadWrite)
            {
                using var transaction = inventory.BeginWrite();
                inventory.Execute("""
                    ALTER TABLE packages ADD COLUMN relative_path TEXT;
                    UPDATE packages SET relative_path = 'packages\' || id;
                    """ + VersionsSchema + "PRAGMA user_version = 2;",
                    transaction);
                transaction.Commit();
            }
            else if (version == 1)
            {
                inventory.legacySchema = true;
            }
            else if (version != SchemaVersion)
            {
                throw new InvalidDataException($"Unsupported installed package database schema {version} in '{path}'.");
            }
            return inventory;
        }
        catch
        {
            inventory.Dispose();
            throw;
        }
    }

    // Publish the new registry only after the legacy import has committed.
    private static void EnsureCreated(InstallScope scope, string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        var temporary = Path.Combine(scope.Root, $".installed-{Guid.NewGuid():N}.db");
        try
        {
            using (var inventory = new InstalledPackages(temporary, SqliteOpenMode.ReadWriteCreate))
            {
                inventory.Initialize(scope.PackagesDirectory, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private void Initialize(string packagesDirectory, CancellationToken cancellationToken)
    {
        using var transaction = BeginWrite();
        using var schema = CreateCommand($"""
            CREATE TABLE packages (
                id TEXT PRIMARY KEY NOT NULL,
                version TEXT NOT NULL,
                selector TEXT,
                architecture TEXT,
                repository TEXT,
                download_url TEXT,
                checksum TEXT,
                installed_at TEXT,
                version_changed_at TEXT,
                relative_path TEXT
            );
            CREATE TABLE commands (
                package_id TEXT NOT NULL REFERENCES packages(id) ON DELETE CASCADE,
                name TEXT NOT NULL COLLATE NOCASE,
                target TEXT NOT NULL,
                PRIMARY KEY (package_id, name)
            );
            CREATE TABLE dependencies (
                package_id TEXT NOT NULL REFERENCES packages(id) ON DELETE CASCADE,
                dependency_id TEXT NOT NULL REFERENCES packages(id) DEFERRABLE INITIALLY DEFERRED,
                PRIMARY KEY (package_id, dependency_id)
            );
            CREATE INDEX dependencies_reverse ON dependencies(dependency_id, package_id);
            CREATE TABLE dependency_renames (
                package_id TEXT NOT NULL,
                dependency_id TEXT NOT NULL,
                name TEXT NOT NULL COLLATE NOCASE,
                renamed_to TEXT NOT NULL,
                PRIMARY KEY (package_id, dependency_id, name),
                FOREIGN KEY (package_id, dependency_id)
                    REFERENCES dependencies(package_id, dependency_id) ON DELETE CASCADE
            );
            {VersionsSchema}
            {InstallationToolsSchema}
            PRAGMA user_version = {SchemaVersion};
            """, transaction);
        schema.ExecuteNonQuery();
        var legacy = LegacyPackageMetadata.Read(packagesDirectory, cancellationToken);
        foreach (var (package, manifest) in legacy.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var dependency in manifest.Dependencies)
            {
                if (!legacy.ContainsKey(dependency.Id))
                {
                    var path = Path.Combine(packagesDirectory, package.Id, LegacyPackageMetadata.ManifestFile);
                    throw new InvalidDataException($"Missing dependency '{dependency.Id}' in package metadata '{path}'.");
                }
            }
            Save(package with { RelativePath = package.PackagePath }, manifest, transaction);
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public SqliteTransaction BeginWrite() => connection.BeginTransaction();

    private const string VersionsSchema = """
        CREATE TABLE package_versions (
            id TEXT NOT NULL, version TEXT NOT NULL COLLATE NOCASE, architecture TEXT, repository TEXT,
            download_url TEXT, checksum TEXT, relative_path TEXT,
            PRIMARY KEY (id, version)
        );
        INSERT INTO package_versions SELECT id, version, architecture, repository, download_url, checksum, relative_path FROM packages;
        """;

    // This additive table also works with clients that only know the v2 inventory.
    private const string InstallationToolsSchema = """
        CREATE TABLE IF NOT EXISTS installation_tools (
            package_id TEXT NOT NULL, package_version TEXT NOT NULL COLLATE NOCASE,
            tool_id TEXT NOT NULL, tool_version TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (package_id, package_version, tool_id),
            FOREIGN KEY (package_id, package_version) REFERENCES package_versions(id, version) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS installation_tools_reverse ON installation_tools(tool_id, tool_version, package_id);
        """;

    public IReadOnlyList<InstalledPackage> ListVersions()
    {
        if (legacySchema) return List();
        using var command = CreateCommand("""
            SELECT v.id, v.version, p.selector, v.architecture, v.repository, v.download_url, v.checksum,
                p.installed_at, p.version_changed_at, coalesce(v.relative_path, p.relative_path)
            FROM package_versions v LEFT JOIN packages p ON p.id = v.id AND v.version = p.version COLLATE NOCASE
            ORDER BY v.id, v.version COLLATE NOCASE
            """);
        using var reader = command.ExecuteReader();
        var versions = new List<InstalledPackage>();
        while (reader.Read()) versions.Add(ReadPackage(reader));
        return versions;
    }

    public void CommitCleanup(string batch, IEnumerable<PackageVersion> versions)
    {
        using var transaction = BeginWrite();
        // The tool version is provenance. Only the consumer still needs to exist on disk.
        using var legacyTools = CreateCommand("""
            SELECT 1 FROM pragma_foreign_key_list('installation_tools') WHERE "from" = 'tool_id'
            """, transaction);
        if (legacyTools.ExecuteScalar() is not null)
        {
            Execute("""
                DROP INDEX IF EXISTS installation_tools_reverse;
                ALTER TABLE installation_tools RENAME TO previous_installation_tools;
                """ + InstallationToolsSchema + """
                INSERT INTO installation_tools SELECT * FROM previous_installation_tools;
                DROP TABLE previous_installation_tools;
                """, transaction);
        }
        foreach (var version in versions)
        {
            Execute("""
                DELETE FROM packages WHERE id = $id AND version = $version COLLATE NOCASE;
                DELETE FROM package_versions WHERE id = $id AND version = $version;
                """, transaction, ("$id", version.Id), ("$version", version.Version));
        }
        Execute("""
            CREATE TABLE IF NOT EXISTS cleanup_commits (batch TEXT PRIMARY KEY NOT NULL);
            INSERT INTO cleanup_commits VALUES ($batch);
            """, transaction, ("$batch", batch));
        transaction.Commit();
    }

    public bool WasCleanupCommitted(string batch)
    {
        using var table = CreateCommand("SELECT 1 FROM sqlite_schema WHERE name = 'cleanup_commits' AND type = 'table'");
        if (table.ExecuteScalar() is null) return false;
        using var command = CreateCommand("SELECT 1 FROM cleanup_commits WHERE batch = $batch", null, ("$batch", batch));
        return command.ExecuteScalar() is not null;
    }

    public void ForgetCleanup(string batch)
    {
        using var command = CreateCommand("DELETE FROM cleanup_commits WHERE batch = $batch", null, ("$batch", batch));
        command.ExecuteNonQuery();
    }

    public InstalledPackage? FindVersion(string id, string version)
    {
        using var command = CreateCommand("""
            SELECT id, version, NULL, architecture, repository, download_url, checksum, NULL, NULL, relative_path
            FROM package_versions WHERE id = $id AND version = $version
            """, null, ("$id", id), ("$version", version));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPackage(reader) : null;
    }

    public IReadOnlyList<InstalledPackage> List(string? query = null, bool explicitOnly = false)
    {
        using var command = CreateCommand($"""
            SELECT {Columns} FROM packages
            WHERE ($explicit = 0 OR selector IS NOT NULL) AND instr(id, $query) > 0
            ORDER BY id
            """, null, ("$explicit", explicitOnly), ("$query", query?.ToLowerInvariant() ?? ""));
        using var reader = command.ExecuteReader();
        var result = new List<InstalledPackage>();
        while (reader.Read())
        {
            result.Add(ReadPackage(reader));
        }
        return result;
    }

    public InstalledPackage? Find(string id) => Find(id, null);

    private InstalledPackage? Find(string id, SqliteTransaction? transaction)
    {
        using var command = CreateCommand($"SELECT {Columns} FROM packages WHERE id = $id", transaction, ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPackage(reader) : null;
    }

    public (InstalledPackage Package, PackageManifest Manifest, IReadOnlyList<string> RequiredBy,
        IReadOnlyList<PackageVersion> InstallationTools, IReadOnlyList<PackageVersion> UsedToInstall)? Details(string id)
    {
        using var snapshot = connection.BeginTransaction(deferred: true);
        if (Find(id, snapshot) is not { } package)
        {
            return null;
        }
        var manifest = ReadManifests([id], id, snapshot)[id];
        var requiredBy = new List<string>();
        using (var command = CreateCommand(
            "SELECT package_id FROM dependencies WHERE dependency_id = $id ORDER BY package_id", snapshot, ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                requiredBy.Add(reader.GetString(0));
            }
        }
        var (tools, installedWithTool) = ReadInstallationTools(package, snapshot);
        return (package, manifest, requiredBy, tools, installedWithTool);
    }

    private (List<PackageVersion> Tools, List<PackageVersion> InstalledWithTool) ReadInstallationTools(InstalledPackage package,
        SqliteTransaction transaction)
    {
        using var table = CreateCommand("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'installation_tools'", transaction);
        if (table.ExecuteScalar() is null) return ([], []);

        using var command = CreateCommand("""
            SELECT tool_id AS id, tool_version AS version, 0 AS reverse FROM installation_tools
            WHERE package_id = $id AND package_version = $version
            UNION ALL
            SELECT p.id, p.version, 1 FROM installation_tools i
            JOIN packages p ON p.id = i.package_id AND i.package_version = p.version
            WHERE i.tool_id = $id AND i.tool_version = $version
            ORDER BY reverse, id, version
            """, transaction, ("$id", package.Id), ("$version", package.Version));
        using var reader = command.ExecuteReader();
        var tools = new List<PackageVersion>();
        var installedWithTool = new List<PackageVersion>();
        while (reader.Read())
        {
            var list = reader.GetBoolean(2) ? installedWithTool : tools;
            list.Add(new PackageVersion(reader.GetString(0), reader.GetString(1)));
        }
        return (tools, installedWithTool);
    }

    // Install holds the scope lock, so its package rows and manifests come from the same installed state.
    public Dictionary<string, PackageManifest> ReadManifests(IEnumerable<string> ids) => ReadManifests(ids, null, null);

    private Dictionary<string, PackageManifest> ReadManifests(IEnumerable<string> ids, string? id, SqliteTransaction? transaction)
    {
        var commands = ids.ToDictionary(key => key, _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var dependencies = commands.Keys.ToDictionary(key => key, _ => new List<PackageDependency>());
        var where = id is null ? "" : " WHERE package_id = $id";
        using (var command = CreateCommand("SELECT package_id, name, target FROM commands" + where,
            transaction, ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                commands[reader.GetString(0)].Add(reader.GetString(1), reader.GetString(2));
            }
        }
        using (var command = CreateCommand("""
            SELECT d.package_id, d.dependency_id, r.name, r.renamed_to
            FROM dependencies d LEFT JOIN dependency_renames r
              ON d.package_id = r.package_id AND d.dependency_id = r.dependency_id
            """ + (id is null ? "" : " WHERE d.package_id = $id") +
            " ORDER BY d.package_id, d.dependency_id, r.name", transaction, ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var list = dependencies[reader.GetString(0)];
                var dependency = reader.GetString(1);
                if (list.Count == 0 || list[^1].Id != dependency)
                {
                    list.Add(new PackageDependency(dependency, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                }
                if (!reader.IsDBNull(2))
                {
                    list[^1].Rename.Add(reader.GetString(2), reader.GetString(3));
                }
            }
        }
        return commands.ToDictionary(pair => pair.Key, pair => new PackageManifest(dependencies[pair.Key].ToArray(), pair.Value));
    }

    public void Save(InstalledPackage package, PackageManifest manifest, SqliteTransaction transaction)
    {
        Execute("""
            INSERT INTO packages (id, version, selector, architecture, repository, download_url, checksum,
                installed_at, version_changed_at, relative_path)
            VALUES ($id, $version, $selector, $architecture, $repository, $url, $checksum, $installed, $changed, $path)
            ON CONFLICT (id) DO UPDATE SET version = excluded.version, selector = excluded.selector,
                architecture = excluded.architecture, repository = excluded.repository,
                download_url = excluded.download_url, checksum = excluded.checksum,
                installed_at = excluded.installed_at, version_changed_at = excluded.version_changed_at,
                relative_path = excluded.relative_path;
            INSERT INTO package_versions VALUES ($id, $version, $architecture, $repository, $url, $checksum, $path)
                ON CONFLICT (id, version) DO NOTHING;
            """, transaction, ("$id", package.Id), ("$version", package.Version), ("$selector", package.Selector),
            ("$architecture", package.Architecture), ("$repository", package.Repository),
            ("$url", package.DownloadUrl), ("$checksum", package.Checksum),
            ("$installed", package.InstalledAt), ("$changed", package.VersionChangedAt), ("$path", package.RelativePath));
        Execute("DELETE FROM commands WHERE package_id = $id; DELETE FROM dependencies WHERE package_id = $id",
            transaction, ("$id", package.Id));
        foreach (var (name, target) in manifest.Commands)
        {
            Execute("INSERT INTO commands VALUES ($id, $name, $target)", transaction,
                ("$id", package.Id), ("$name", name), ("$target", target));
        }
        foreach (var dependency in manifest.Dependencies)
        {
            Execute("INSERT INTO dependencies VALUES ($id, $dependency)", transaction,
                ("$id", package.Id), ("$dependency", dependency.Id));
            foreach (var (name, renamed) in dependency.Rename)
            {
                Execute("INSERT INTO dependency_renames VALUES ($id, $dependency, $name, $renamed)", transaction,
                    ("$id", package.Id), ("$dependency", dependency.Id), ("$name", name), ("$renamed", renamed));
            }
        }
    }

    public void SaveInstallationTools(IReadOnlyList<(InstalledPackage Package, InstalledPackage Tool)> tools,
        SqliteTransaction transaction)
    {
        if (tools.Count == 0) return;
        Execute(InstallationToolsSchema, transaction);
        foreach (var (package, tool) in tools)
        {
            Execute("INSERT INTO installation_tools VALUES ($id, $version, $tool, $toolVersion)", transaction,
                ("$id", package.Id), ("$version", package.Version), ("$tool", tool.Id), ("$toolVersion", tool.Version));
        }
    }

    private static InstalledPackage ReadPackage(SqliteDataReader reader)
    {
        string? Optional(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
        return new InstalledPackage(reader.GetString(0), reader.GetString(1), Optional(2), Optional(3),
            Optional(4), Optional(5), Optional(6), Optional(7), Optional(8), Optional(9));
    }

    private void Execute(string sql, SqliteTransaction transaction, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(sql, transaction, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand CreateCommand(string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command;
    }

    public void Dispose() => connection.Dispose();
}
