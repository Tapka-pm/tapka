using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tapka;

// The caller holds the installation lock while acquiring or using snapshots.
internal sealed class CatalogCache(InstallScope scope, HttpClient http)
{
    private const long MaxCatalogBytes = 128 * 1024 * 1024;

    public async Task<PackageRepository> OpenAsync(string source, bool force, CancellationToken cancellationToken)
    {
        var repository = new PackageRepository(http, source);
        var identity = repository.ArchiveUrl is null ? repository.Source.ToUpperInvariant() : repository.Source;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var directory = Path.Combine(scope.Root, "catalogs", key);
        var pointer = Path.Combine(directory, "current");
        var previous = File.Exists(pointer) ? File.ReadAllText(pointer) : null;
        var current = Guid.TryParseExact(previous, "N", out _) ? Path.Combine(directory, previous!) : null;
        if (!Directory.Exists(current))
        {
            current = null;
        }
        if (!force)
        {
            var disabled = Environment.GetEnvironmentVariable("TAPKA_NO_AUTO_UPDATE") == "1";
            if (current is not null && (disabled || DateTime.UtcNow - File.GetLastWriteTimeUtc(pointer) < TimeSpan.FromDays(1)))
            {
                return new PackageRepository(http, repository.Source, current);
            }
            if (disabled)
            {
                throw new IOException($"No catalog snapshot for '{repository.Source}'. Run 'tapka update --repository <source>'.");
            }
        }

        var generation = Guid.NewGuid().ToString("N");
        var next = Directory.CreateDirectory(Path.Combine(directory, generation)).FullName;
        var temporary = Path.Combine(directory, $"{generation}.tmp");
        var published = false;
        try
        {
            var packages = Directory.CreateDirectory(Path.Combine(next, "packages")).FullName;
            if (repository.ArchiveUrl is { } archive)
            {
                await DownloadAsync(archive, repository.Source == PackageRepository.DefaultSource, packages, next, cancellationToken);
            }
            else
            {
                CopyTree(Path.Combine(repository.Source, "packages"), packages, cancellationToken);
            }

            Validate(packages, cancellationToken);
            await File.WriteAllTextAsync(temporary, generation, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, pointer, overwrite: true);
            published = true;
            Console.WriteLine($"Updated catalog '{repository.Source}'.");
            return new PackageRepository(http, repository.Source, next);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException or IOException)
        {
            throw new IOException($"Could not update catalog '{repository.Source}': {error.Message}", error);
        }
        finally
        {
            File.Delete(temporary);
            var obsolete = published ? current : next;
            if (obsolete is not null)
            {
                try { Directory.Delete(obsolete, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task DownloadAsync(Uri url, bool wrapped, string packages, string temporary, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        token = deadline.Token;
        var zipPath = Path.Combine(temporary, "download.zip");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(token))
        await using (var output = File.Create(zipPath))
        {
            await CopyLimitedAsync(input, output, MaxCatalogBytes, token);
        }
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var remaining = MaxCatalogBytes;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var foundPackages = false;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var path = entry.FullName.Split('/');
                var offset = wrapped ? 1 : 0;
                if (path.Length <= offset + 1 || path[offset] != "packages")
                {
                    continue;
                }
                foundPackages = true;
                var segments = path[(offset + 1)..];
                var isDirectory = segments[^1].Length == 0;
                if (isDirectory) segments = segments[..^1];
                if (segments.Length == 0) continue;
                if (!segments.All(segment => segment.Length <= 255 && !segment.Contains('\\') &&
                        PackageRepository.IsPathSegment(segment)) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                {
                    throw new InvalidDataException($"Unsafe catalog entry '{entry.FullName}'.");
                }
                var destination = Path.Combine(packages, Path.Combine(segments));
                if (isDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                if (!paths.Add(destination) || entry.Length > remaining)
                {
                    throw new InvalidDataException($"Duplicate entry or catalog size limit exceeded at '{entry.FullName}'.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew);
                remaining -= await CopyLimitedAsync(input, output, remaining, token);
            }
            if (!foundPackages) throw new InvalidDataException("Catalog archive has no packages directory.");
        }
        File.Delete(zipPath);
    }

    private static async Task<long> CopyLimitedAsync(Stream source, Stream destination, long limit, CancellationToken token)
    {
        var buffer = new byte[65536];
        long total = 0;
        int length;
        while ((length = await source.ReadAsync(buffer, token)) > 0)
        {
            total += length;
            if (total > limit) throw new InvalidDataException("Catalog size limit exceeded.");
            await destination.WriteAsync(buffer.AsMemory(0, length), token);
        }
        return total;
    }

    private static void CopyTree(string source, string destination, CancellationToken token)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Catalog path '{source}' is a link.");
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Catalog path '{path}' is a link.");
            }
            var target = Path.Combine(destination, Path.GetFileName(path));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                CopyTree(path, Directory.CreateDirectory(target).FullName, token);
            }
            else
            {
                File.Copy(path, target);
            }
        }
    }

    private static void Validate(string packages, CancellationToken token)
    {
        foreach (var directory in Directory.EnumerateDirectories(packages))
        {
            token.ThrowIfCancellationRequested();
            var id = PackageRepository.ValidateId(Path.GetFileName(directory));
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "config.json")));
            var config = document.RootElement;
            if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("id", out var name) ||
                name.ValueKind != JsonValueKind.String || name.GetString() != id ||
                !config.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Invalid recipe for '{id}'.");
            }
        }
    }
}
