using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tapka;

internal static class ArchiveTests
{
    private const string FileName = "каталог проба/check.txt";
    private const string Text = "archive fixture\n";

    public static async Task RunAsync(string arch, string pwsh, string[] args)
    {
        if (args is not [var binary, var catalogSource, var cache, var mode] ||
            mode is not ("full" or "offline" or "built-in"))
        {
            throw new ArgumentException("Use test-archives.ps1 to run the archive integration tests.");
        }
        binary = Path.GetFullPath(binary);
        catalogSource = Path.GetFullPath(catalogSource);
        cache = Path.GetFullPath(cache);
        var root = Directory.CreateTempSubdirectory("tapka archive tests ").FullName;
        try
        {
            await RunScenariosAsync(root, binary, catalogSource, cache, mode, arch, pwsh);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunScenariosAsync(string root, string binary, string catalogSource, string cache,
        string mode, string arch, string pwsh)
    {
        var installRoot = Path.Combine(root, "archives с пробелом");
        var catalog = Path.Combine(root, "archive-catalog");
        var recipes = Directory.CreateDirectory(Path.Combine(catalog, "packages")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(root, "downloads")).FullName;
        const string filename = "дистрибутив.payload";

        string Package(string id, string file = "") => Path.Combine(installRoot, "packages", id, file);
        string Public(string name) => Path.Combine(installRoot, "shims", "public", name);
        string Config(string id) => Path.Combine(recipes, id, "config.json");
        string Download(string id) => Path.Combine(downloads, id, filename);
        string Url(string id) => $"https://fixtures.tapka.invalid/{id}/{Uri.EscapeDataString(filename)}";

        async Task Install(string id, string? error = null)
        {
            var result = await Run(binary, root, "install", id, "--repository", catalog, "--root", installRoot,
                "--fixture-downloads", downloads);
            Check(error is null ? result.ExitCode == 0 : result.ExitCode == 1 &&
                result.Error.Contains(error, StringComparison.OrdinalIgnoreCase),
                $"Unexpected result for '{id}' (exit {result.ExitCode}).\n{result.Output}\n{result.Error}");
            Check(!Directory.Exists(installRoot) || !Directory.EnumerateDirectories(installRoot, ".install-*").Any(),
                $"'{id}' left an installation staging directory.");
        }

        void Recipe(string id, string archive, byte[] payload, string? checksum = null, string? command = null,
            string version = "1")
        {
            Directory.CreateDirectory(Path.Combine(recipes, id));
            Directory.CreateDirectory(Path.Combine(downloads, id));
            File.WriteAllBytes(Download(id), payload);
            // All fixtures have the same extension, regardless of the actual archive format.
            var target = new
            {
                download = new { url = Url(id), sha256 = checksum ?? Convert.ToHexString(SHA256.HashData(payload)) },
                install = new { type = "portable", archive },
                commands = command is null ? new Dictionary<string, string>() : new() { ["archive-test"] = command }
            };
            var config = new
            {
                id, version, dependencies = Array.Empty<string>(),
                architectures = new Dictionary<string, object> { [arch] = target }
            };
            File.WriteAllText(Config(id), JsonSerializer.Serialize(config));
        }

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = new StreamWriter(archive.CreateEntry(FileName).Open()))
        {
            entry.Write(Text);
        }
        var tar = Tar();
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(tar);
        }
        Recipe("zip", "zip", zip.ToArray());
        Recipe("tar", "tar", tar);
        Recipe("tar-gzip", "tar.gz", compressed.ToArray());
        foreach (var id in new[] { "zip", "tar", "tar-gzip" })
        {
            await Install(id);
            Check(File.ReadAllText(Package(id, FileName)) == Text, $"Wrong {id} output.");
        }
        Check(!Directory.Exists(Package("7zip")) && !Directory.Exists(Package("7zr")) &&
            !Directory.Exists(Package("powershell")), "Built-in formats installed an extractor.");

        Recipe("unknown", "iso", tar);
        File.Delete(Download("unknown"));
        await Install("unknown", "Unsupported archive format");
        Recipe("escape", "tar", Tar("../outside.txt"));
        await Install("escape", "Unsafe archive path");
        Recipe("link", "tar", Tar("link", TarEntryType.SymbolicLink));
        await Install("link", "Unsupported TAR entry type");
        Recipe("bad-hash", "tar", tar, new string('0', 64));
        await Install("bad-hash", "mismatch");
        Recipe("corrupt", "tar", "truncated"u8.ToArray());
        await Install("corrupt", "tapka:");
        Recipe("missing-command", "zip", zip.ToArray(), command: "missing.exe");
        await Install("missing-command", "did not provide 'missing.exe'");
        foreach (var id in new[] { "unknown", "escape", "link", "bad-hash", "corrupt", "missing-command" })
        {
            Check(!Directory.Exists(Package(id)), $"A failed install published '{id}'.");
        }
        Check(!File.Exists(Public("archive-test.exe")), "A failed install exposed a shim.");

        var tarMarker = Package("tar", ".tapka-version");
        var tarTimestamp = File.GetLastWriteTimeUtc(tarMarker);
        Recipe("tar", "tar", "truncated"u8.ToArray(), version: "2");
        await Install("tar", "tapka:");
        Check(File.ReadAllText(tarMarker) == "1" && File.GetLastWriteTimeUtc(tarMarker) == tarTimestamp &&
            File.ReadAllText(Package("tar", FileName)) == Text, "A failed update changed the installed TAR package.");

        await CheckInstallProcessAsync(pwsh);

        if (mode == "built-in")
        {
            Console.WriteLine("CLI archive tests passed (ZIP, TAR, TAR.GZ).");
            return;
        }

        async Task<string> Asset(string id)
        {
            var source = Path.Combine(catalogSource, "packages", id);
            var destination = Directory.CreateDirectory(Path.Combine(recipes, id)).FullName;
            var script = Path.Combine(source, "install.ps1");
            if (File.Exists(script))
            {
                File.Copy(script, Path.Combine(destination, "install.ps1"));
            }
            var config = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "config.json")))!;
            var versions = config["versions"]!;
            var latest = versions["latest"]!["to"]!.GetValue<string>();
            var download = versions[latest]!["architectures"]![arch]!["download"]!;
            var uri = new Uri(download["url"]!.GetValue<string>());
            var checksum = download["sha256"]!.GetValue<string>();
            var file = await CacheAssetAsync(cache, uri, checksum, mode == "offline");
            Directory.CreateDirectory(Path.Combine(downloads, id));
            File.Copy(file, Download(id));
            download["url"] = Url(id);
            File.WriteAllText(Config(id), config.ToJsonString());
            return file;
        }
        await Asset("powershell");
        var sevenZipInstaller = await Asset("7zip");
        var bootstrap = await Asset("7zr");
        var fixtures = Directory.CreateDirectory(Path.Combine(root, "archive fixtures")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(fixtures, "tools")).FullName;
        await RunChecked(bootstrap, fixtures, "x", "-bso0", "-bsp0", $"-o{destination}", "--", sevenZipInstaller, "7z.exe", "7z.dll");
        var sevenZip = Path.Combine(destination, "7z.exe");
        var input = Path.Combine(fixtures, FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        File.WriteAllText(input, Text);
        var command = Path.Combine(Path.GetDirectoryName(FileName)!, "tool.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(fixtures, command));
        File.WriteAllBytes(Path.Combine(fixtures, "data.tar"), tar);
        await RunChecked(sevenZip, fixtures, "a", "-bso0", "-bsp0", "-t7z", "plain.7z", Path.GetDirectoryName(FileName)!);
        await RunChecked(sevenZip, fixtures, "a", "-bso0", "-bsp0", "-t7z", "-pfixture-password", "encrypted.7z", FileName);
        await RunChecked(sevenZip, fixtures, "a", "-bso0", "-bsp0", "-txz", "data.xz", "data.tar");
        var seven = File.ReadAllBytes(Path.Combine(fixtures, "plain.7z"));
        Recipe("seven", "7z", seven, command: command);
        Recipe("encrypted", "7z", File.ReadAllBytes(Path.Combine(fixtures, "encrypted.7z")));
        Recipe("tar-xz", "tar.xz", File.ReadAllBytes(Path.Combine(fixtures, "data.xz")));
        // Stored RAR4 and RAR5 archives with the same check.txt. The third has the volume flag set.
        Recipe("rar4", "rar", Convert.FromBase64String(
            "UmFyIRoHAM+QcwAADQAAAAAAAABFBXQAgCkAEAAAABAAAAACxO8VdwCEblcUMAkAIAAAAGNoZWNrLnR4dGFyY2hpdmUgZml4dHVyZQoEsHsAAAcA"));
        Recipe("rar5", "rar", Convert.FromBase64String(
            "UmFyIRoHAQDFGjMyAwEAAC7yVQYWAgIQBBAgxO8VdwAACWNoZWNrLnR4dGFyY2hpdmUgZml4dHVyZQoZsjo1AwUAAA=="));
        Recipe("rar-volume", "rar", Convert.FromBase64String(
            "UmFyIRoHAQBTKjRFAwEAAS7yVQYWAgIQBBAgxO8VdwAACWNoZWNrLnR4dGFyY2hpdmUgZml4dHVyZQoZsjo1AwUAAA=="));

        // The expanded tools and PowerShell must roll back if the target fails after bootstrapping.
        Recipe("rollback", "7z", seven, new string('0', 64));
        await Install("rollback", "mismatch");
        foreach (var id in new[] { "7zr", "7zip", "powershell", "rollback" })
        {
            Check(!Directory.Exists(Package(id)), $"Rollback left {id} installed.");
        }
        await Install("seven");
        Check(File.Exists(Package("7zip", "7z.dll")) && File.Exists(Package("7zr", "7zr.exe")) &&
            File.Exists(Package("powershell", "pwsh.exe")), "Bootstrap chain did not run.");
        Check(File.ReadAllText(Package("seven", FileName)) == Text, "Wrong 7z output.");
        Check(File.Exists(Public("archive-test.exe")), "The extracted command does not have a public shim.");
        foreach (var exe in new[] { "7zr.exe", "7z.exe", "pwsh.exe" })
        {
            Check(!File.Exists(Public(exe)), $"Automatic installation exposed {exe}.");
        }
        Check(!Directory.Exists(Path.Combine(installRoot, "shims", "deps", "seven")), "Archive tools became runtime dependencies.");
        var userPath = File.ReadAllText(Path.Combine(installRoot, "user-path.txt"));
        Check(userPath == Path.GetDirectoryName(Public("archive-test.exe")), "Unexpected PATH registration.");

        var extractorConfig = Config("7zip");
        foreach (var id in new[] { "seven", "7zip", "7zr", "powershell" })
        {
            File.Delete(Download(id));
        }
        var installedAt = File.GetLastWriteTimeUtc(Package("seven", ".tapka-version"));
        File.Move(extractorConfig, extractorConfig + ".bak");
        await Install("seven@1");
        File.Move(extractorConfig + ".bak", extractorConfig);
        Check(File.GetLastWriteTimeUtc(Package("seven", ".tapka-version")) == installedAt &&
            File.ReadAllText(Package("seven", ".tapka-selector")) == "1", "Reinstall rewrote the package or lost the selector.");
        await Install("rar4");
        await Install("rar5");
        await Install("tar-xz");
        Check(File.ReadAllText(Package("rar4", "check.txt")) == Text, "Wrong RAR4 output.");
        Check(File.ReadAllText(Package("rar5", "check.txt")) == Text, "Wrong RAR5 output.");
        Check(File.ReadAllText(Package("tar-xz", FileName)) == Text, "Wrong TAR.XZ output.");
        await Install("rar-volume", "Multi-volume archives");
        await Install("encrypted", "Encrypted archives");
        Recipe("broken-seven", "7z", "truncated"u8.ToArray());
        await Install("broken-seven", "tapka:");
        Check(!Directory.Exists(Package("rar-volume")) && !Directory.Exists(Package("encrypted")) &&
            !Directory.Exists(Package("broken-seven")),
            "An unreadable archive was published.");

        await Install("7zip");
        Check(File.Exists(Public("7z.exe")) && !File.Exists(Public("7zr.exe")), "Explicit installation did not promote only 7-Zip.");
        File.Move(extractorConfig, extractorConfig + ".bak");
        Recipe("7zip", "7z", seven);
        await Install("7zip", "Dependency cycle");
        Check(File.Exists(Package("7zip", "7z.dll")) && File.Exists(Public("7z.exe")),
            "A failed update removed the existing extractor or its shim.");
        Console.WriteLine("CLI archive tests passed (ZIP, TAR, TAR.GZ, 7z, RAR, TAR.XZ).");
    }

    private static byte[] Tar(string name = FileName, TarEntryType type = TarEntryType.RegularFile)
    {
        using var stream = new MemoryStream();
        using var contents = new MemoryStream(Encoding.UTF8.GetBytes(Text));
        using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
            var entry = new PaxTarEntry(type, name);
            if (type == TarEntryType.SymbolicLink)
            {
                entry.LinkName = "../outside.txt";
            }
            else
            {
                entry.DataStream = contents;
            }
            writer.WriteEntry(entry);
        }
        return stream.ToArray();
    }

    private static async Task<string> CacheAssetAsync(string directory, Uri uri, string checksum, bool offline)
    {
        Check(uri.Scheme == Uri.UriSchemeHttps, $"Invalid archive test download URL '{uri}'.");
        var filename = Path.GetFileName(uri.LocalPath);
        var file = Path.Combine(directory, filename);
        if (File.Exists(file) && await MatchesAsync(file))
        {
            return file;
        }
        if (offline)
        {
            throw new IOException($"Missing or invalid cached artifact '{file}'. Run without -Offline to fetch it.");
        }

        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.partial");
        Console.WriteLine($"Fetching {filename} for archive tests...");
        using var http = new HttpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            using (var input = await http.GetStreamAsync(uri, deadline.Token))
            await using (var output = File.Create(temporary))
            {
                await input.CopyToAsync(output, deadline.Token);
            }
            Check(await MatchesAsync(temporary), $"Checksum mismatch for '{uri}'.");
            File.Move(temporary, file, overwrite: true);
            return file;
        }
        finally
        {
            File.Delete(temporary);
        }

        async Task<bool> MatchesAsync(string path)
        {
            using var input = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(input)).Equals(checksum, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task CheckInstallProcessAsync(string pwsh)
    {
        ProcessStartInfo SleepingProcess() => Start(pwsh, Environment.CurrentDirectory, "-NoProfile", "-Command", "Start-Sleep -Seconds 60");

        await Fails(() => InstallProcess.RunAsync(SleepingProcess(), "test process", CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(150)), "timed out");
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            try
            {
                await InstallProcess.RunAsync(SleepingProcess(), "test process", cancel.Token);
                throw new Exception("Process cancellation did not propagate.");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        }
        var failingProcess = Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Environment.CurrentDirectory,
            "/d", "/c", "exit 42");
        await Fails(() => InstallProcess.RunAsync(failingProcess, "test process", CancellationToken.None), "exit code 42");
    }

    private static async Task RunChecked(string executable, string directory, params string[] args)
    {
        var result = await Run(executable, directory, args);
        Check(result.ExitCode == 0, $"'{executable}' exited with {result.ExitCode}.\n{result.Output}\n{result.Error}");
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(string executable, string directory,
        params string[] args)
    {
        var start = Start(executable, directory, args);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new IOException($"Could not start '{executable}'.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw new TimeoutException($"'{executable}' did not finish.");
        }
    }

    private static ProcessStartInfo Start(string executable, string directory, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory };
        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private static async Task Fails(Func<Task> run, string? message = null)
    {
        try
        {
            await run();
        }
        catch (Exception error) when (error is IOException or InvalidDataException &&
            (message is null || error.Message.Contains(message, StringComparison.Ordinal)))
        {
            return;
        }
        throw new Exception("An archive operation should have failed.");
    }
}
