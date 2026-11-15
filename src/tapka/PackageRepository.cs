using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tapka;

internal sealed record PackageDependency(string Id, Dictionary<string, string> Rename);

internal sealed record PackageRelease(string Id, string Version, PackageDependency[] Dependencies,
    Uri DownloadUrl, string Checksum, string? Script, ArchiveFormat? Archive, Dictionary<string, string> Commands);

internal sealed class PackageRepository(HttpClient http, string source)
{
    public const string DefaultSource = "https://raw.githubusercontent.com/Tapka-pm/packages/master/";

    private static readonly Regex PackageId = new(@"\A[a-z0-9]+(?:-[a-z0-9]+)*\z");
    private static readonly Regex CommandName = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z");
    private static readonly Regex DeviceName = new(
        @"\A(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Uri? remote = GetRemoteSource(source);
    public string Source => remote?.AbsoluteUri ?? Path.GetFullPath(source);

    public async Task<PackageRelease> ResolveAsync(string id, string requestedVersion, string architecture,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        var recipe = await ReadAsync(id, "config.json", cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(recipe);
            var config = document.RootElement;
            if (RequiredString(config, "id") != id)
            {
                throw new InvalidDataException($"The recipe ID does not match '{id}'.");
            }

            var (version, release) = ResolveVersion(config, requestedVersion);

            if (!release.GetProperty("architectures").TryGetProperty(architecture, out var target))
            {
                throw new InvalidDataException($"Version '{version}' is unavailable for Windows {architecture}.");
            }

            if (!target.TryGetProperty("download", out var download))
            {
                throw new InvalidDataException($"Version '{version}' has no published download for Windows {architecture}.");
            }

            if (!Uri.TryCreate(RequiredString(download, "url"), UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("The download URL must use HTTPS.");
            }

            var hasSha256 = download.TryGetProperty("sha256", out var sha256);
            var hasSha512 = download.TryGetProperty("sha512", out var sha512);
            var checksum = hasSha256 == hasSha512 ? null : (hasSha256 ? sha256 : sha512).GetString();
            if (checksum is null || checksum.Length != (hasSha256 ? 64 : 128) ||
                !checksum.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("A download must have exactly one valid SHA-256 or SHA-512 checksum.");
            }

            var install = target.GetProperty("install");
            var hasScript = install.TryGetProperty("script", out _);
            var hasArchive = install.TryGetProperty("archive", out _);
            if (RequiredString(install, "type") != "portable" || (!hasScript && !hasArchive))
            {
                throw new InvalidDataException("A portable recipe must specify an archive, a PowerShell script, or both.");
            }

            var script = hasScript ? RelativePath(RequiredString(install, "script")) : null;
            ArchiveFormat? archive = hasArchive ? PackageArchive.Parse(RequiredString(install, "archive")) : null;
            if (script is not null && !script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The installation script must use the .ps1 extension.");
            }

            var commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var command in target.GetProperty("commands").EnumerateObject())
            {
                if (!commands.TryAdd(NormalizeCommand(command.Name), RelativePath(command.Value.GetString())))
                {
                    throw new InvalidDataException($"Invalid or duplicate command '{command.Name}'.");
                }
            }

            var dependencies = ReadDependencies(release.GetProperty("dependencies"));
            return new PackageRelease(id, version, dependencies, url, checksum, script, archive, commands);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException
            or KeyNotFoundException or InvalidOperationException)
        {
            throw new IOException($"Invalid recipe for '{id}': {error.Message}", error);
        }
    }

    private static PackageDependency[] ReadDependencies(JsonElement entries)
    {
        var dependencies = new List<PackageDependency>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string id;
            if (entry.ValueKind == JsonValueKind.String)
            {
                id = ValidateId(entry.GetString());
            }
            else
            {
                id = ValidateId(RequiredString(entry, "id"));
                foreach (var command in entry.GetProperty("rename").EnumerateObject())
                {
                    if (!rename.TryAdd(NormalizeCommand(command.Name), NormalizeCommand(command.Value.GetString())))
                    {
                        throw new InvalidDataException($"Duplicate rename for '{command.Name}' in dependency '{id}'.");
                    }
                }
            }

            if (!ids.Add(id))
            {
                throw new InvalidDataException($"Duplicate dependency '{id}'.");
            }

            dependencies.Add(new PackageDependency(id, rename));
        }

        return dependencies.ToArray();
    }

    internal static string NormalizeCommand(string? name)
    {
        if (name is null || !CommandName.IsMatch(name) || DeviceName.IsMatch(name))
        {
            throw new InvalidDataException($"Invalid command '{name}'.");
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    private static (string Version, JsonElement Release) ResolveVersion(JsonElement config, string requestedVersion)
    {
        var versions = config.GetProperty("versions");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var version = requestedVersion;
        while (visited.Add(version))
        {
            if (!versions.TryGetProperty(version, out var release))
            {
                throw new InvalidDataException($"Version or alias '{version}' was not found.");
            }

            switch (RequiredString(release, "type"))
            {
                case "package":
                    return (version, release);
                case "alias":
                    version = RequiredString(release, "to");
                    break;
                default:
                    throw new InvalidDataException($"Unsupported version type at '{version}'.");
            }
        }

        throw new InvalidDataException($"Version alias cycle at '{version}'.");
    }

    public Task<byte[]> ReadScriptAsync(PackageRelease release, CancellationToken cancellationToken) =>
        ReadAsync(release.Id, release.Script!, cancellationToken);

    public static string GetDownloadFilename(Uri url)
    {
        var filename = Path.GetFileName(url.LocalPath);
        return IsPathSegment(filename) ? filename : "installer";
    }

    private async Task<byte[]> ReadAsync(string id, string file, CancellationToken cancellationToken)
    {
        try
        {
            if (remote is null)
            {
                return await File.ReadAllBytesAsync(Path.Combine(Path.GetFullPath(source), "packages", id, file), cancellationToken);
            }

            var path = string.Join('/', file.Split('\\').Select(Uri.EscapeDataString));
            return await http.GetByteArrayAsync(new Uri(remote, $"packages/{id}/{path}"), cancellationToken);
        }
        catch (Exception error) when (file == "config.json" && (error is FileNotFoundException
            or DirectoryNotFoundException or HttpRequestException { StatusCode: HttpStatusCode.NotFound }))
        {
            throw new IOException($"Package '{id}' was not found in the repository '{source}'.", error);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new IOException($"Could not read '{file}' for '{id}' from the repository: {error.Message}", error);
        }
    }

    private static Uri? GetRemoteSource(string source)
    {
        if (source.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
                url.Query.Length != 0 || url.Fragment.Length != 0)
            {
                throw new InvalidOperationException("--repository requires a local directory or an HTTPS base URL without a query or fragment.");
            }

            return new Uri(url.AbsoluteUri.TrimEnd('/') + "/");
        }

        return null;
    }

    internal static string ValidateId(string? id)
    {
        if (id is null || !PackageId.IsMatch(id) || DeviceName.IsMatch(id))
        {
            throw new InvalidDataException($"Invalid package ID '{id}'.");
        }

        return id;
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.GetProperty(name).GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text :
            throw new InvalidDataException($"'{name}' must be a non-empty string.");

    internal static string RelativePath(string? path)
    {
        var normalized = path?.Replace('/', '\\');
        if (normalized is null || !normalized.Split('\\').All(IsPathSegment))
        {
            throw new InvalidDataException($"Invalid relative path '{path}'.");
        }

        return normalized;
    }

    internal static bool IsPathSegment(string part) => part.Length > 0 && !part.EndsWith('.') && !part.EndsWith(' ') &&
        !DeviceName.IsMatch(part) && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
