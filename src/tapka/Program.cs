using Microsoft.Data.Sqlite;
using System.CommandLine;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Tapka;

var package = new Argument<string>("package") { Description = "Package ID or ID@version-or-alias. Defaults to the latest release." };
var source = new Option<string>("--repository") { Description = "HTTPS base URL or local path to the Tapka package repository." };
var install = new Command("install", "Install a package for the current user.") { package, source };
var root = new RootCommand("Portable package manager for Windows.") { install };
InventoryCommands.AddTo(root);

#if TAPKA_TEST_BUILD
var testRoot = new Option<string>("--root") { Description = "Absolute test installation directory.", Required = true };
var fixtureDownloads = new Option<string>("--fixture-downloads") { Description = "Directory containing local integration test downloads." };
install.Options.Add(testRoot);
install.Options.Add(fixtureDownloads);
#endif

install.SetAction(async (parseResult, cancellationToken) =>
{
    var architecture = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => null
    };

    if (!OperatingSystem.IsWindows() || architecture is null)
    {
        Console.Error.WriteLine("tapka: installation requires Windows x64 or ARM64.");
        return 1;
    }

    try
    {
        var packageReference = parseResult.GetRequiredValue(package).Split('@', 2);
        var requestedVersion = packageReference.Length == 2 ? packageReference[1] : "latest";
        if (string.IsNullOrWhiteSpace(packageReference[0]) || string.IsNullOrWhiteSpace(requestedVersion))
        {
            throw new ArgumentException("Specify a package ID as <package> or <package>@<version-or-alias>.");
        }

#if TAPKA_TEST_BUILD
        var scope = InstallScope.Create(parseResult.GetValue(testRoot));
        var fixtures = parseResult.GetValue(fixtureDownloads);
        using var http = fixtures is null ? new HttpClient() : new HttpClient(new TestDownloads(fixtures));
        var powerShell = fixtures is null ? PackageInstaller.FindSystemPowerShell() : null;
#else
        var scope = InstallScope.Create();
        using var http = new HttpClient();
        var powerShell = PackageInstaller.FindSystemPowerShell();
#endif
        var repository = new PackageRepository(http, parseResult.GetValue(source) ?? PackageRepository.DefaultSource);
        var installer = new PackageInstaller(scope, repository, http, powerShell);
        await installer.InstallAsync(packageReference[0].ToLowerInvariant(), requestedVersion, architecture, cancellationToken);
        return 0;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine("tapka: installation cancelled.");
        return 130;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("tapka: the repository request timed out.");
        return 1;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or Win32Exception or System.Security.SecurityException or ArgumentException or SqliteException)
    {
        Console.Error.WriteLine($"tapka: {error.Message}");
        return 1;
    }
});

return await root.Parse(args).InvokeAsync();
