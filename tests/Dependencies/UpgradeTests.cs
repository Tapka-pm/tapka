using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Tapka;

internal static class UpgradeTests
{
    public static async Task RunAsync(string binary, string architecture)
    {
        var root = Directory.CreateTempSubdirectory("tapka upgrade tests ").FullName;
        var previousAutoUpdate = Environment.GetEnvironmentVariable("TAPKA_NO_AUTO_UPDATE");
        Environment.SetEnvironmentVariable("TAPKA_NO_AUTO_UPDATE", null);
        try
        {
            await CheckCommandsAsync(root, binary, architecture);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAPKA_NO_AUTO_UPDATE", previousAutoUpdate);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckCommandsAsync(string root, string binary, string arch)
    {
        var installation = Path.Combine(root, "installed");
        var local = Path.Combine(root, "local");
        var remote = Path.Combine(root, "remote");
        var downloads = Directory.CreateDirectory(Path.Combine(root, "downloads")).FullName;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var shims = new PackageShims(installation);
        const string remoteSource = "https://fixtures.tapka.invalid";

        string Fixture(string id, string version) => Path.Combine(downloads, id, $"{version}.zip");
        string Public(string command) => Path.Combine(shims.PublicDirectory, command + ".exe");
        string PathFor(string id, string version) => Path.Combine(installation, "packages", id, version);

        void Recipe(string source, string id, string version, string command, string executable, string[] dependencies,
            string? fixtureVersion = null)
        {
            var directory = Directory.CreateDirectory(Path.Combine(source, "packages", id)).FullName;
            var fixture = Fixture(id, fixtureVersion ?? version);
            var target = id is "7zip" or "7zr" ? $"{command}.exe" : "tool.exe";
            if (!File.Exists(fixture))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
                using var zip = ZipFile.Open(fixture, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(executable, target, CompressionLevel.Fastest);
                if (id == "7zip") zip.CreateEntry("7z.dll");
            }
            using var payload = File.OpenRead(fixture);
            var release = new
            {
                type = "package", dependencies,
                architectures = new Dictionary<string, object>
                {
                    [arch] = new
                    {
                        download = new
                        {
                            url = $"{remoteSource}/{id}/{Path.GetFileName(fixture)}",
                            sha256 = Convert.ToHexString(SHA256.HashData(payload))
                        },
                        install = new { type = "portable", archive = "zip" },
                        commands = new Dictionary<string, string> { [command] = target }
                    }
                }
            };
            File.WriteAllText(Path.Combine(directory, "config.json"), JsonSerializer.Serialize(new
            {
                id, versions = new Dictionary<string, object>
                {
                    ["latest"] = new { type = "alias", to = version },
                    ["lts"] = new { type = "alias", to = version },
                    [version] = release
                }
            }));
        }

        void PublishRemote()
        {
            var archive = Path.Combine(downloads, "repository.zip");
            File.Delete(archive);
            ZipFile.CreateFromDirectory(remote, archive);
        }

        async Task<string> Run(string executable, int exit, params string[] arguments)
        {
            var result = await ArchiveTests.Run(executable, root, [.. arguments, "--root", installation,
                .. arguments[0] is "install" or "update" or "upgrade" ? new[] { "--fixture-downloads", downloads } : []]);
            Check(result.ExitCode == exit, $"{string.Join(' ', arguments)} returned {result.ExitCode}.\n{result.Output}\n{result.Error}");
            return result.Output + result.Error;
        }

        Task<string> Command(params string[] args) => Run(binary, 0, args);
        void ExecuteSql(string statement)
        {
            var database = Path.Combine(installation, "installed.db");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            connection.Open();
            using var sql = connection.CreateCommand();
            sql.CommandText = statement;
            sql.ExecuteNonQuery();
        }

        async Task CheckPath(string id, string version)
        {
            Check((await Command("path", id)).Trim() == PathFor(id, version), $"Wrong active version of {id}.");
        }

        Recipe(local, "demo", "1", "demo", cmd, ["dep"]);
        Recipe(local, "dep", "1", "dep-tool", cmd, []);
        Recipe(local, "pinned", "1", "pin-tool", cmd, []);
        Recipe(remote, "other", "1", "other-tool", cmd, []);
        PublishRemote();
        Environment.SetEnvironmentVariable("TAPKA_NO_AUTO_UPDATE", "1");
        Check((await Run(binary, 1, "install", "demo", "--repository", local)).Contains("tapka update"),
            "A missing snapshot did not explain how to fetch it.");
        await Command("update", "--repository", local);
        Check((await Command("list")).Length == 0, "Updating a catalog installed a package.");
        await Command("install", "demo@lts", "--repository", local);
        await Command("install", "pinned@1", "--repository", local);
        Environment.SetEnvironmentVariable("TAPKA_NO_AUTO_UPDATE", null);
        await Command("install", "other", "--repository", remoteSource);
        await CheckPath("demo", "1");
        await CheckPath("other", "1");

        var generation = shims.ActiveGeneration!;
        var oldPrivate = shims.DependencyDirectory(generation, "demo");
        var originalShim = File.GetLastWriteTimeUtc(Public("demo"));
        var signal = Path.Combine(root, "release");
        var ready = Path.Combine(root, "ready");
        var script = Path.Combine(root, "worker.cmd");
        File.WriteAllLines(script, ["@echo off", $"echo ready > \"{ready}\"", ":wait",
            $"if not exist \"{signal}\" goto wait", "where dep-tool.exe", "dep-tool.exe /d /c exit 23",
            "if not %errorlevel%==23 exit /b 9", "exit /b 0"]);
        var readyTask = ArchiveTests.Run(Public("demo"), root, "/d", "/c", script);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!File.Exists(ready))
            {
                if (readyTask.IsCompleted) throw new Exception("Old process stopped before the upgrade.");
                await Task.Delay(20, limit.Token);
            }

            Recipe(local, "demo", "2", "demo", cmd, ["dep"]);
            Recipe(local, "dep", "2", "dep-tool", cmd, []);
            Recipe(local, "pinned", "2", "pin-tool", cmd, []);
            Recipe(remote, "other", "2", "other-tool", cmd, []);
            PublishRemote();

            await Command("upgrade", "demo");
            await CheckPath("demo", "1");
            await Command("update", "--repository", local);
            var changes = await Task.WhenAll(Command("upgrade", "demo"), Command("upgrade", "demo"));
            Check(changes.Count(result => result.Contains("Updated 2 package(s).")) == 1, "Concurrent upgrades did not share installed state.");
            await CheckPath("demo", "2");
            await CheckPath("dep", "2");
            await CheckPath("other", "1");
            Check(File.GetLastWriteTimeUtc(Public("demo")) == originalShim && Directory.Exists(PathFor("demo", "1")) &&
                Directory.Exists(PathFor("dep", "1")) && !File.Exists(Public("dep-tool")), "Upgrade replaced a running shim or exposed a dependency.");
            var latest = await ArchiveTests.Run(Public("demo"), root, "/d", "/c", "where dep-tool.exe");
            Check(latest.ExitCode == 0 && latest.Output.Trim().StartsWith(shims.DependencyDirectory(shims.ActiveGeneration!, "demo"),
                StringComparison.OrdinalIgnoreCase), "A new process did not see the new dependency generation.");
            var parallelLaunch = ArchiveTests.Run(Public("demo"), root, "/d", "/c", "dep-tool.exe /d /c exit 24");
            var cleanup = await Command("cleanup");
            Check(cleanup.Contains("Keeping demo@1") && cleanup.Contains("Keeping dep@1") &&
                Directory.Exists(PathFor("demo", "1")) && Directory.Exists(PathFor("dep", "1")) &&
                (await parallelLaunch).ExitCode == 24, $"Cleanup broke an old or concurrent launch.\n{cleanup}");

            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(installation, "shims", "generations")))
                File.Delete(Path.Combine(directory, PackageShims.GenerationPackagesFile));
            cleanup = await Command("cleanup");
            Check(cleanup.Contains("Keeping dep@1") && Directory.Exists(PathFor("dep", "1")),
                $"Cleanup could not recover the old shim's dependencies.\n{cleanup}");
        }
        finally
        {
            File.WriteAllText(signal, "");
        }
        var old = await readyTask;
        Check(old.ExitCode == 0 && old.Output.Trim() == Path.Combine(oldPrivate, "dep-tool.exe"),
            "A running process lost its original dependency shims.");
        var exited = await Command("cleanup", "--dry-run");
        Check(exited.Contains("Would remove demo@1.") && exited.Contains("Would remove dep@1."),
            $"Exited processes still protected their old versions.\n{exited}");

        foreach (var pointer in Directory.EnumerateFiles(Path.Combine(installation, "catalogs"), "current", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(pointer, DateTime.UtcNow.AddHours(-25));

        File.WriteAllText(Path.Combine(downloads, "repository.zip"), "broken ZIP");
        await Run(binary, 1, "upgrade", "other");
        await CheckPath("other", "1");
        Environment.SetEnvironmentVariable("TAPKA_NO_AUTO_UPDATE", "1");
        await Command("upgrade", "other");
        await CheckPath("other", "1");
        Environment.SetEnvironmentVariable("TAPKA_NO_AUTO_UPDATE", null);
        PublishRemote();

        Recipe(local, "demo", "1", "demo", cmd, ["dep"]);
        Recipe(local, "dep", "1", "dep-tool", cmd, []);
        File.Delete(Fixture("demo", "1"));
        File.Delete(Fixture("dep", "1"));
        var all = await Command("upgrade");
        Check(all.Contains("pinned 1 - Installed (Pinned)"), "A pin was not reported.");
        await CheckPath("demo", "1");
        await CheckPath("dep", "1");
        await CheckPath("other", "2");
        await CheckPath("pinned", "1");
        await Run(binary, 1, "upgrade", "missing");
        Check(Directory.Exists(PathFor("demo", "2")) && Directory.Exists(PathFor("dep", "2")), "A downgrade removed newer binaries.");

        var moved = Path.Combine(root, "moved installation");
        Directory.Move(installation, moved);
        var relocated = await ArchiveTests.Run(Path.Combine(moved, "shims", "public", "demo.exe"), root, "/d", "/c", "where dep-tool.exe");
        Check(relocated.ExitCode == 0 && relocated.Output.StartsWith(moved, StringComparison.OrdinalIgnoreCase), "Shims did not survive relocation.");
        Directory.Move(moved, installation);

        Recipe(local, "dotnet-runtime-11", "1", "dotnet", cmd, []);
        Recipe(local, "tapka", "1", "tapka", binary, ["dotnet-runtime-11"]);
        await Command("update", "--repository", local);
        await Command("install", "tapka", "--repository", local);
        var oldTapkaShim = File.GetLastWriteTimeUtc(Public("tapka"));
        Recipe(local, "dotnet-runtime-11", "2", "dotnet", cmd, []);
        Recipe(local, "tapka", "2", "tapka", binary, ["dotnet-runtime-11"], fixtureVersion: "1");
        await Command("update", "--repository", local);
        await Run(Public("tapka"), 0, "upgrade", "tapka");
        Check((await Run(Public("tapka"), 0, "path", "tapka")).Trim() == PathFor("tapka", "2"), "Upgraded Tapka did not start.");
        await CheckPath("dotnet-runtime-11", "2");
        Check(Directory.Exists(PathFor("tapka", "1")) && Directory.Exists(PathFor("dotnet-runtime-11", "1")) &&
            File.GetLastWriteTimeUtc(Public("tapka")) == oldTapkaShim, "Self-upgrade replaced an in-use file.");

        Recipe(local, "clean-app", "1", "clean-app", cmd, ["7zip", "7zr", "powershell", "shared-clean", "orphan-clean"]);
        Recipe(local, "7zip", "1", "7z", cmd, []);
        Recipe(local, "7zr", "1", "7zr", cmd, []);
        Recipe(local, "powershell", "1", "pwsh", cmd, []);
        Recipe(local, "shared-clean", "1", "shared-clean", cmd, ["shared-leaf"]);
        Recipe(local, "shared-leaf", "1", "shared-leaf", cmd, []);
        Recipe(local, "orphan-clean", "1", "orphan-clean", cmd, ["orphan-leaf"]);
        Recipe(local, "orphan-leaf", "1", "orphan-leaf", cmd, []);
        Recipe(local, "clean-peer", "1", "clean-peer", cmd, ["shared-clean"]);
        await Command("update", "--repository", local);
        await Command("install", "clean-app", "--repository", local);
        await Command("install", "clean-peer", "--repository", local);

        Recipe(local, "clean-app", "2", "clean-app", cmd, []);
        Recipe(local, "7zip", "2", "7z", cmd, []);
        await Command("update", "--repository", local);
        await Command("upgrade", "clean-app", "7zip");

        ExecuteSql("""
                DROP TABLE installation_tools;
                CREATE TABLE installation_tools (
                    package_id TEXT NOT NULL, package_version TEXT NOT NULL COLLATE NOCASE,
                    tool_id TEXT NOT NULL, tool_version TEXT NOT NULL COLLATE NOCASE,
                    PRIMARY KEY (package_id, package_version, tool_id),
                    FOREIGN KEY (package_id, package_version) REFERENCES package_versions(id, version) ON DELETE CASCADE,
                    FOREIGN KEY (tool_id, tool_version) REFERENCES package_versions(id, version)
                );
                CREATE INDEX installation_tools_reverse ON installation_tools(tool_id, tool_version, package_id);
                INSERT INTO installation_tools VALUES ('clean-app', '2', '7zip', '1');
                """);

        var oldApp = PathFor("clean-app", "1");
        var orphan = PathFor("orphan-clean", "1");
        var orphanLeaf = PathFor("orphan-leaf", "1");
        var readOnly = Path.Combine(oldApp, "tool.exe");
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
        using (File.Open(Path.Combine(orphan, "tool.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var dryRun = await Run(Public("tapka"), 0, "cleanup", "--dry-run");
            Check(dryRun.Contains("Would remove clean-app@1.") && dryRun.Contains("Would remove 7zip@1.") &&
                dryRun.Contains("Keeping orphan-clean@1") && dryRun.Contains("Keeping orphan-leaf@1"),
                $"Cleanup preview mishandled the held file.\n{dryRun}");
            Check(Directory.Exists(oldApp) && Directory.Exists(orphan), "Cleanup preview moved files.");
            var removed = await Run(Public("tapka"), 0, "cleanup");
            Check(RemovalLines(dryRun, "Would remove ").SequenceEqual(RemovalLines(removed, "Removed ")) &&
                !Directory.Exists(oldApp) && Directory.Exists(orphan) && Directory.Exists(orphanLeaf),
                $"Cleanup disagreed with the preview.\n{dryRun}\n{removed}");
        }
        Check(!Directory.Exists(PathFor("7zip", "1")) && Directory.Exists(PathFor("7zip", "2")) &&
            Directory.Exists(PathFor("7zr", "1")) && Directory.Exists(PathFor("powershell", "1")) &&
            Directory.Exists(PathFor("shared-clean", "1")) && Directory.Exists(PathFor("shared-leaf", "1")) &&
            (await Command("info", "clean-app")).Contains($"Installation tools:{Environment.NewLine}  7zip 1"),
            "Cleanup lost active packages or installation history.");
        Check(!Directory.Exists(PathFor("tapka", "1")) && !Directory.Exists(PathFor("dotnet-runtime-11", "1")),
            "The active cleanup invocation retained older Tapka versions.");

        ExecuteSql("""
            CREATE TRIGGER reject_cleanup BEFORE DELETE ON package_versions WHEN OLD.id = 'orphan-clean'
            BEGIN SELECT RAISE(ABORT, 'cleanup test failure'); END;
            """);
        var rejected = await Run(binary, 1, "cleanup");
        Check(rejected.Contains("cleanup test failure") && Directory.Exists(orphan) && Directory.Exists(orphanLeaf) &&
            !CleanupJournal.HasPending(installation), $"Failed cleanup did not restore its files.\n{rejected}");
        await CheckPath("orphan-clean", "1");
        ExecuteSql("DROP TRIGGER reject_cleanup");

        var orphanCleanup = await Command("cleanup");
        Check(orphanCleanup.Contains("Removed orphan-clean@1.") && orphanCleanup.Contains("Removed orphan-leaf@1.") &&
            !Directory.Exists(orphan) && !Directory.Exists(orphanLeaf), $"Cleanup left an orphaned chain.\n{orphanCleanup}");
        await Run(binary, 1, "path", "orphan-clean");
        Recipe(local, "clean-app", "1", "clean-app", cmd, ["orphan-clean"]);
        await Command("update", "--repository", local);
        await Command("install", "clean-app@1", "--repository", local);
        await CheckPath("clean-app", "1");
        Check(Directory.Exists(orphan) && Directory.Exists(orphanLeaf), "An explicitly requested version could not be reinstalled after cleanup.");

        var precommit = CleanupJournal.Create(installation, [oldApp]);
        precommit.Stage(0);
        Check(!Directory.Exists(oldApp), "The recovery fixture did not stage the package.");
        await Command("cleanup", "--dry-run");
        Check(!Directory.Exists(oldApp) && CleanupJournal.HasPending(installation), "Preview changed an interrupted cleanup.");
        await CheckPath("clean-app", "1");
        Check(Directory.Exists(oldApp) && !CleanupJournal.HasPending(installation), "Recovery did not restore the staged path.");

        await Command("install", "clean-peer", "--repository", local);
        var postcommit = CleanupJournal.Create(installation, [PathFor("clean-app", "2")]);
        postcommit.Stage(0);
        using (var inventory = InstalledPackages.OpenForInstall(InstallScope.Create(installation), CancellationToken.None))
            postcommit.Commit(inventory, [new PackageVersion("clean-app", "2")]);
        await Command("cleanup");
        Check(!CleanupJournal.HasPending(installation) && !Directory.Exists(PathFor("clean-app", "2")) && Directory.Exists(oldApp),
            "Recovery did not finish the committed cleanup.");

        installation = Path.Combine(root, "legacy");
        var legacy = Directory.CreateDirectory(Path.Combine(installation, "packages", "old-app")).FullName;
        File.Copy(cmd, Path.Combine(legacy, "tool.exe"));
        File.WriteAllText(Path.Combine(legacy, ".tapka-version"), "1");
        File.WriteAllText(Path.Combine(legacy, ".tapka-selector"), "latest");
        File.WriteAllText(Path.Combine(legacy, LegacyPackageMetadata.ManifestFile),
            """{"Dependencies":[],"Commands":{"old-app.exe":"tool.exe"}}""");
        await Command("list");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(installation, "installed.db"), Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var sql = connection.CreateCommand();
            sql.CommandText = "DROP TABLE installation_tools; DROP TABLE package_versions; ALTER TABLE packages DROP COLUMN relative_path; PRAGMA user_version = 1;";
            sql.ExecuteNonQuery();
        }
        Check((await Command("path", "old-app")).Trim() == legacy, "Reading the v1 database lost the legacy path.");

        var official = Path.Combine(root, "packages-master");
        var officialZip = Path.Combine(downloads, "official-repository.zip");
        Recipe(official, "old-app", "1", "old-app", cmd, []);
        ZipFile.CreateFromDirectory(official, officialZip, CompressionLevel.Fastest, includeBaseDirectory: true);
        Check((await Command("upgrade")).Contains("All selected packages are up to date."),
            "A legacy package at the current version did not resolve through the official catalog.");
        Check((await Command("path", "old-app")).Trim() == legacy, "Upgrading to the same version changed the legacy path.");

        var legacyShims = new PackageShims(installation);
        var oldLauncher = Path.Combine(Directory.CreateDirectory(legacyShims.PublicDirectory).FullName, "old-app.exe");
        using var template = typeof(UpgradeTests).Assembly.GetManifestResourceStream("tapka.shim.exe")!;
        using var shimBytes = new MemoryStream();
        template.CopyTo(shimBytes);
        var embedded = ShimBinary.Create(shimBytes.ToArray(), @"..\..", @"packages\old-app\tool.exe", "");
        File.WriteAllBytes(oldLauncher, embedded);
        Recipe(official, "old-app", "2", "old-app", cmd, []);
        File.Delete(officialZip);
        ZipFile.CreateFromDirectory(official, officialZip, CompressionLevel.Fastest, includeBaseDirectory: true);
        await Command("update");
        await Command("upgrade");
        await CheckPath("old-app", "2");
        var archive = Path.Combine(legacyShims.GenerationDirectory(legacyShims.ActiveGeneration!), "legacy", "old-app.exe");
        Check(File.ReadAllBytes(archive).SequenceEqual(embedded) && File.Exists(Path.Combine(legacy, "tool.exe")) &&
            (await ArchiveTests.Run(oldLauncher, root, "/d", "/c", "exit 42")).ExitCode == 42,
            "Migration lost the old executable or failed to switch the shim.");

        Recipe(local, "old-app", "3", "old-app", cmd, []);
        await Command("upgrade", "old-app", "--repository", local);
        await CheckPath("old-app", "3");
        var legacyCleanup = await Command("cleanup");
        Check(legacyCleanup.Contains("Removed old-app@1.") && legacyCleanup.Contains("Removed old-app@2.") &&
            !File.Exists(Path.Combine(legacy, "tool.exe")) && File.Exists(Path.Combine(PathFor("old-app", "3"), "tool.exe")) &&
            (await ArchiveTests.Run(oldLauncher, root, "/d", "/c", "exit 42")).ExitCode == 42,
            $"Cleanup failed for overlapping legacy and current paths.\n{legacyCleanup}");
        Console.WriteLine("Upgrade integration tests passed.");
    }

    private static IEnumerable<string> RemovalLines(string output, string prefix) => output.Split(Environment.NewLine)
        .Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).Select(line => line[prefix.Length..]);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
