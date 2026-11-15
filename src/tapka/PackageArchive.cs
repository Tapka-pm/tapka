using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Tapka;

internal enum ArchiveFormat { Zip, Tar, TarGzip, SevenZip, Rar, TarXz }

internal static class PackageArchive
{
    public const string ExtractorPackage = "7zip";
    public const string BootstrapPackage = "7zr";

    public static ArchiveFormat Parse(string format) => format switch
    {
        "zip" => ArchiveFormat.Zip,
        "tar" => ArchiveFormat.Tar,
        "tar.gz" => ArchiveFormat.TarGzip,
        "7z" => ArchiveFormat.SevenZip,
        "rar" => ArchiveFormat.Rar,
        "tar.xz" => ArchiveFormat.TarXz,
        _ => throw new InvalidDataException($"Unsupported archive format '{format}'.")
    };

    public static bool Needs7Zip(ArchiveFormat? format) =>
        format is ArchiveFormat.SevenZip or ArchiveFormat.Rar or ArchiveFormat.TarXz;

    public static bool NeedsBootstrap(PackageRelease release) =>
        release.Id == ExtractorPackage && release.Script is not null;

    public static bool HasToolFiles(string directory, string id, Dictionary<string, string> commands) =>
        id is not (ExtractorPackage or BootstrapPackage) || FindTool(directory, id, commands) is not null;

    public static string GetTool(string packages, string id, Dictionary<string, string> commands) =>
        FindTool(Path.Combine(packages, id), id, commands) ??
        throw new IOException($"Package '{id}' did not provide the required extractor files.");

    private static string? FindTool(string directory, string id, Dictionary<string, string> commands)
    {
        var executable = id == ExtractorPackage ? "7z.exe" : "7zr.exe";
        if (!commands.TryGetValue(executable, out var target) ||
            !Path.GetFileName(target).Equals(executable, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var path = Path.Combine(directory, target);
        return File.Exists(path) && (id != ExtractorPackage ||
            File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "7z.dll"))) ? path : null;
    }

    public static async Task ExtractAsync(ArchiveFormat format, string archive, string destination, string? extractor,
        CancellationToken cancellationToken)
    {
        using var input = File.OpenRead(archive);
        switch (format)
        {
            case ArchiveFormat.Zip:
                ZipFile.ExtractToDirectory(input, destination);
                break;
            case ArchiveFormat.Tar:
                await ExtractTarAsync(input, destination, cancellationToken);
                break;
            case ArchiveFormat.TarGzip:
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                {
                    await ExtractTarAsync(gzip, destination, cancellationToken);
                    // Read through the gzip trailer so its checksum is checked.
                    await gzip.CopyToAsync(Stream.Null, cancellationToken);
                }
                break;
            case ArchiveFormat.TarXz:
                // A fixed name outside the package tree avoids using the name stored in the XZ stream.
                var tar = Path.Combine(Path.GetDirectoryName(destination)!, "decoded.tar");
                await using (var output = new FileStream(tar, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, useAsync: true))
                {
                    await Run7ZipAsync(extractor!, archive, "x", ["-txz", "-so", "-bso0"], cancellationToken,
                        (process, token) => process.StandardOutput.BaseStream.CopyToAsync(output, token));
                }
                using (var decoded = File.OpenRead(tar))
                {
                    await ExtractTarAsync(decoded, destination, cancellationToken);
                }
                break;
            case ArchiveFormat.SevenZip:
            case ArchiveFormat.Rar:
                var type = format == ArchiveFormat.SevenZip ? "-t7z" : ReadRarType(input);
                await Run7ZipAsync(extractor!, archive, "l", [type, "-slt"], cancellationToken,
                    (process, token) => ValidateListingAsync(process.StandardOutput, destination, token));
                await Run7ZipAsync(extractor!, archive, "x",
                    [type, "-bso0", "-snl-", "-snh-", "-sns-", "-y", $"-o{destination}"], cancellationToken);
                ValidateExtractedTree(destination, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static async Task ExtractTarAsync(Stream input, string destination, CancellationToken cancellationToken)
    {
        using var reader = new TarReader(input, leaveOpen: true);
        while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(EntryPath(destination, entry.Name, directory: true));
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    var path = EntryPath(destination, entry.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await entry.ExtractToFileAsync(path, overwrite: false, cancellationToken);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported TAR entry type '{entry.EntryType}' for '{entry.Name}'.");
            }
        }
    }

    private static string ReadRarType(Stream input)
    {
        Span<byte> signature = stackalloc byte[8];
        input.ReadExactly(signature);
        if (signature.SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }))
        {
            return "-trar5";
        }
        if (signature[..7].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }))
        {
            return "-trar";
        }
        throw new InvalidDataException("Invalid RAR signature.");
    }

    private static Task Run7ZipAsync(string extractor, string archive, string command, string[] switches,
        CancellationToken cancellationToken, Func<Process, CancellationToken, Task>? readOutput = null)
    {
        var start = new ProcessStartInfo(extractor) { WorkingDirectory = Path.GetDirectoryName(extractor)! };
        if (readOutput is not null)
        {
            start.StandardOutputEncoding = Encoding.UTF8;
        }
        foreach (var argument in new[] { command, "-sccUTF-8", "-bsp0", "-spd" }.Concat(switches).Concat(["--", archive]))
        {
            start.ArgumentList.Add(argument);
        }
        return InstallProcess.RunAsync(start, "7-Zip archive operation", cancellationToken, readOutput);
    }

    private static async Task ValidateListingAsync(StreamReader output, string destination, CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = false;
        var hasPath = false;
        var trailingDataWarning = false;
        var warningSummary = false;
        while (await output.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!entries)
            {
                if (line == "----------")
                {
                    entries = true;
                }
                else if (line == "There are data after the end of archive")
                {
                    trailingDataWarning = true;
                }
                else if (line == "Multivolume = +" ||
                    line.StartsWith("Volumes = ", StringComparison.Ordinal) && line != "Volumes = 1" ||
                    line.StartsWith("Characteristics = ", StringComparison.Ordinal) &&
                    line.Split(' ').Any(word => word is "Volume" or "VolumeField" or "FirstVolume"))
                {
                    throw new InvalidDataException("Multi-volume archives are unsupported.");
                }
                continue;
            }

            if (line.Length == 0)
            {
                hasPath = false;
                continue;
            }
            // A signed self-extracting archive can have a signature after the archive data.
            if (!hasPath && trailingDataWarning && !warningSummary && line == "Warnings: 1")
            {
                warningSummary = true;
                continue;
            }
            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (warningSummary || separator < 1 || (!hasPath && !line.StartsWith("Path = ", StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Invalid 7-Zip archive listing.");
            }
            var key = line[..separator];
            var value = line[(separator + 3)..];
            switch (key)
            {
                case "Path":
                    if (hasPath || !paths.Add(EntryPath(destination, value)))
                    {
                        throw new InvalidDataException($"Duplicate or invalid archive entry '{value}'.");
                    }
                    hasPath = true;
                    break;
                case "Encrypted" when value != "-":
                    throw new InvalidDataException("Encrypted archives are unsupported.");
                case "Split Before" or "Split After" when value == "+":
                    throw new InvalidDataException("Multi-volume archives are unsupported.");
                case "Anti" when value == "+":
                    throw new InvalidDataException("Archive entries that delete files are unsupported.");
                case "Symbolic Link" or "Hard Link" or "Copy Link" or "Alternate Stream" when value is not ("" or "-"):
                case "Attributes" when HasSpecialAttributes(value):
                    throw new InvalidDataException("Archive links, special files and alternate streams are unsupported.");
            }
        }
        if (!entries)
        {
            throw new InvalidDataException("Could not read the 7-Zip archive listing.");
        }
    }

    private static bool HasSpecialAttributes(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word =>
            word.Contains('L') || word[0] is 'l' or 'b' or 'c' or 'p' or 's');

    private static string EntryPath(string destination, string name, bool directory = false)
    {
        var relative = name.Replace('/', '\\');
        var hasRootMarker = relative.StartsWith(@".\", StringComparison.Ordinal);
        while (relative.StartsWith(@".\", StringComparison.Ordinal))
        {
            relative = relative[2..];
        }
        if (directory)
        {
            if (relative == "." || (hasRootMarker && relative.Length == 0))
            {
                return destination;
            }
            relative = relative.TrimEnd('\\');
        }
        var parts = relative.Split('\\');
        if (!parts.All(PackageRepository.IsPathSegment) ||
            parts[0].Equals(".tapka-version", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals(".tapka-selector", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals(".tapka-package.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsafe archive path '{name}'.");
        }
        return Path.Combine(destination, relative);
    }

    private static void ValidateExtractedTree(string destination, CancellationToken cancellationToken)
    {
        var directories = new Stack<string>();
        directories.Push(destination);
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException($"Archive entry '{entry.Name}' is a link.");
                }
                EntryPath(destination, Path.GetRelativePath(destination, entry.FullName));
                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    directories.Push(entry.FullName);
                }
            }
        }
    }
}
