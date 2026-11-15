using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Tapka;

var powerShell = PackageInstaller.FindSystemPowerShell() ?? throw new Exception("PowerShell 7 is required.");
var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
if (args is ["--archives", .. var archiveArgs])
{
    await ArchiveTests.RunAsync(arch, powerShell, archiveArgs);
    return;
}
if (args.Length != 0)
{
    throw new ArgumentException("Use test-archives.ps1 to run the archive integration tests.");
}

var root = Directory.CreateTempSubdirectory("tapka dependency tests ").FullName;
var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
try
{
    var installation = Path.Combine(root, "installed");
    var repository = Path.Combine(root, "catalog");
    var shims = new PackageShims(installation);
    var scope = InstallScope.Create(installation);
    var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    var cmdBytes = File.ReadAllBytes(cmd);
    var handler = new FixtureDownloads();
    using var http = new HttpClient(handler);
    var installer = new PackageInstaller(scope, new PackageRepository(http, repository), http, systemPowerShell: null);

    void Recipe(string id, object[] dependencies, string[] commands, string version = "1",
        string? script = null, Dictionary<string, byte[]>? files = null, bool? extract = null)
    {
        var directory = Directory.CreateDirectory(Path.Combine(repository, "packages", id)).FullName;
        var artifact = files ?? new Dictionary<string, byte[]> { ["tool.exe"] = cmdBytes };
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in artifact)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(content);
            }
        }
        var payload = stream.ToArray();
        var url = $"https://packages.invalid/{id}-{version}.zip";
        handler.Payloads[url] = payload;
        if (script is not null)
        {
            File.WriteAllText(Path.Combine(directory, "install.ps1"), script);
        }
        var install = new Dictionary<string, string> { ["type"] = "portable" };
        if (extract ?? (script is null))
        {
            install["archive"] = "zip";
        }
        if (script is not null)
        {
            install["script"] = "install.ps1";
        }
        var target = new
        {
            download = new { url, sha256 = Convert.ToHexString(SHA256.HashData(payload)) },
            install,
            commands = commands.ToDictionary(name => name, _ => id == "powershell" ? "pwsh.exe" : "tool.exe")
        };
        var versions = new Dictionary<string, object>
        {
            ["latest"] = new { type = "alias", to = version },
            [version] = new { type = "package", dependencies, architectures = new Dictionary<string, object> { [arch] = target } }
        };
        File.WriteAllText(Path.Combine(directory, "config.json"), JsonSerializer.Serialize(new { id, versions }));
    }

    static object Rename(string id, params (string Name, string Alias)[] names) =>
        new { id, rename = names.ToDictionary(pair => pair.Name, pair => pair.Alias) };
    Task Install(string id, string selector = "latest") => installer.InstallAsync(id, selector, arch, CancellationToken.None);
    string PackageFile(string id, string file) => Path.Combine(installation, "packages", id, file);
    string Public(string command) => Path.Combine(shims.PublicDirectory, command + ".exe");
    string Private(string id, string command) => Path.Combine(shims.DependencyDirectory(id), command + ".exe");
    void AssertPublic(params string[] expected)
    {
        var actual = Directory.GetFiles(shims.PublicDirectory, "*.exe").Select(Path.GetFileNameWithoutExtension);
        Check(actual.Order().SequenceEqual(expected.Order()), "Unexpected public commands.");
        Check(File.ReadAllText(Path.Combine(installation, "user-path.txt")) == shims.PublicDirectory,
            "Unexpected PATH registration.");
    }

    Recipe("powershell", [], ["pwsh"], files: new()
    {
        ["pwsh.exe"] = ShimBinary.Create(ReadShim(), @"..\..", Path.GetRelativePath(installation, powerShell), "")
    });
    Recipe("c", [], ["tapka-c", "old-c"]);
    Recipe("b", [Rename("c", ("TaPkA-C.ExE", "inner-c.exe"))], ["tapka-b"]);
    Recipe("d", [Rename("c")], []);
    const string installScript = """
        param([string]$Installer, [string]$Destination)
        $ErrorActionPreference = 'Stop'
        if (Get-Command tapka-b.exe -ErrorAction SilentlyContinue) { throw 'Original lookup name leaked' }
        & vendor-b.exe /d /c 'where inner-c.exe'
        if ($LASTEXITCODE -ne 0) { throw 'Dependency did not see its own renamed command' }
        & tapka-c.exe /d /c 'exit 23'
        if ($LASTEXITCODE -ne 23) { throw 'Transitive dependency or exit code missing' }
        Expand-Archive -LiteralPath $Installer -DestinationPath $Destination
        """;
    Recipe("a", ["d", Rename("b", ("tapka-b.EXE", "vendor-b"))], ["tapka-a"], script: installScript);
    await Install("a");
    AssertPublic("tapka-a");
    Check(File.Exists(PackageFile("powershell", "pwsh.exe")) && !File.Exists(PackageFile("powershell", ".tapka-selector")),
        "PowerShell was not bootstrapped privately.");
    Check(!Directory.Exists(shims.DependencyDirectory("c")), "Unexpected directory for a package with no dependencies.");
    Check(!File.Exists(Private("a", "tapka-b")) && !File.Exists(Private("a", "inner-c")), "A inherited the wrong names.");
    Check(File.Exists(Private("b", "inner-c")) && File.Exists(Private("a", "tapka-c")), "Dependency shims are missing.");

    async Task<(int ExitCode, string Output)> Run(string executable, string command)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = root,
            RedirectStandardOutput = true
        };
        start.Environment["PATH"] = shims.PublicDirectory + ";" + Environment.SystemDirectory;
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output.Trim());
    }

    Check((await Run(Public("tapka-a"), "where vendor-b.exe")) == (0, Private("a", "vendor-b")),
        "The public launcher did not prepend A's dependency PATH.");
    Check((await Run(Public("tapka-a"), "vendor-b.exe /d /c where inner-c.exe")) == (0, Private("b", "inner-c")),
        "The private launcher did not prepend B's dependency PATH.");
    Check((await Run(Public("tapka-a"), "tapka-c.exe /d /c exit 31")).ExitCode == 31, "Exit code changed.");

    Recipe("foreign", [], ["foreign"]);
    File.WriteAllBytes(Public("foreign"), cmdBytes);
    var requests = handler.Requests;
    await Fails(() => Install("foreign"), "conflicts with an existing executable");
    Check(handler.Requests == requests, "The conflicting command reached the download step.");

    Recipe("a", ["d", Rename("b", ("tapka-b", "vendor-b")), Rename("c", ("tapka-c", "vendor-c"))], ["tapka-a"]);
    await Install("a");
    Check(File.ReadAllBytes(Public("foreign")).SequenceEqual(cmdBytes), "Synchronization changed a foreign executable.");
    File.Delete(Public("foreign"));
    Check(!File.Exists(Private("a", "tapka-c")) && File.Exists(Private("a", "vendor-c")),
        "Direct declaration did not override the transitive name.");

    requests = handler.Requests;
    Directory.CreateDirectory(Public("tapka-b"));
    await Fails(() => Install("b", "1"));
    Check(!File.Exists(PackageFile("b", ".tapka-selector")), "Failed promotion left an explicit selector.");
    Directory.Delete(Public("tapka-b"));
    await Install("b", "1");
    Check(handler.Requests == requests, "Explicit install re-downloaded an already installed dependency.");
    var shimWrittenAt = File.GetLastWriteTimeUtc(Public("tapka-b"));
    await Install("a");
    Check(File.GetLastWriteTimeUtc(Public("tapka-b")) == shimWrittenAt, "Synchronization rewrote an unchanged shim.");
    AssertPublic("tapka-a", "tapka-b");
    Check(File.ReadAllText(PackageFile("b", ".tapka-selector")) == "1", "Explicit dependency selector was lost.");

    Recipe("c", [], ["tapka-c", "new-c"], "2");
    await Install("c");
    AssertPublic("tapka-a", "tapka-b", "tapka-c", "new-c");
    Check(File.Exists(Private("a", "new-c")) && !File.Exists(Private("a", "old-c")) &&
        File.Exists(Private("b", "new-c")), "Updating C left stale shims in installed consumers.");

    Recipe("c", [], ["tapka-c", "rollback-c"], "3");
    Recipe("broken", ["c"], ["broken"]);
    Directory.CreateDirectory(Public("broken")); // Force publication to fail after C has been replaced.
    await Fails(() => Install("broken"));
    Check(File.ReadAllText(PackageFile("c", ".tapka-version")) == "2", "Rollback did not restore C's files.");
    Check(File.Exists(Private("a", "new-c")) && !File.Exists(Private("a", "rollback-c")),
        "Rollback did not restore A's dependency shims.");
    Check(!Directory.Exists(PackageFile("broken", "")) && !File.Exists(Public("broken")),
        "Rollback left parts of the failed installation.");
    AssertPublic("tapka-a", "tapka-b", "tapka-c", "new-c");

    const string prepareScript = """
        param([string]$Installer, [string]$Destination)
        $ErrorActionPreference = 'Stop'
        if (!(Test-Path -LiteralPath $Installer -PathType Leaf)) { throw 'The download is missing' }
        Move-Item -LiteralPath (Join-Path $Destination 'unprepared.exe') -Destination (Join-Path $Destination 'tool.exe')
        Set-Content -LiteralPath (Join-Path $Destination 'prepared.txt') -Value 'ready' -NoNewline
        """;
    Recipe("prepared", [], ["prepared"], script: prepareScript, files: new() { ["unprepared.exe"] = cmdBytes }, extract: true);
    await Install("prepared");
    Check(File.ReadAllText(PackageFile("prepared", "prepared.txt")) == "ready" && File.Exists(Public("prepared")),
        "The script did not prepare the extracted package before command validation.");

    Recipe("prepared", [], ["replacement"], "2", script: prepareScript + "\nexit 23",
        files: new() { ["unprepared.exe"] = cmdBytes, ["version-2.txt"] = [] }, extract: true);
    await Fails(() => Install("prepared"), "exit code 23");
    Check(File.ReadAllText(PackageFile("prepared", ".tapka-version")) == "1" &&
        !File.Exists(PackageFile("prepared", "version-2.txt")) &&
        File.Exists(Public("prepared")) && !File.Exists(Public("replacement")),
        "The failed script exposed part of the update or removed the old installation.");

    requests = handler.Requests;
    Recipe("invalid", [], [], extract: false);
    await Fails(() => Install("invalid"), "must specify an archive, a PowerShell script, or both");
    Recipe("invalid", ["b", Rename("b")], []);
    await Fails(() => Install("invalid"), "Duplicate dependency");
    Recipe("invalid", [Rename("b", ("missing", "alias"))], []);
    await Fails(() => Install("invalid"), "does not provide");
    Recipe("invalid", [Rename("b", ("tapka-b", "../alias"))], []);
    await Fails(() => Install("invalid"), "Invalid command");
    Recipe("invalid", [Rename("b", ("tapka-b", "tapka-c"))], []);
    await Fails(() => Install("invalid"), "provided by both");
    Recipe("invalid", ["invalid"], []);
    await Fails(() => Install("invalid"), "Dependency cycle");
    Check(handler.Requests == requests, "An invalid recipe reached the download step.");
    Check(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) == userPath, "Real PATH changed.");
    Console.WriteLine("Dependency tests passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static byte[] ReadShim()
{
    using var stream = typeof(PackageInstaller).Assembly.GetManifestResourceStream("tapka.shim.exe")!;
    using var bytes = new MemoryStream();
    stream.CopyTo(bytes);
    return bytes.ToArray();
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static async Task Fails(Func<Task> run, string? message = null)
{
    try
    {
        await run();
    }
    catch (IOException error) when (message is null || error.Message.Contains(message)) { return; }
    catch (UnauthorizedAccessException) when (message is null) { return; }
    throw new Exception("Installation should have failed.");
}

internal sealed class FixtureDownloads : HttpMessageHandler
{
    public Dictionary<string, byte[]> Payloads { get; } = new();
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Payloads[request.RequestUri!.AbsoluteUri])
        });
    }
}
