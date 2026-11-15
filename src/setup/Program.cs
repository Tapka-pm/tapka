using System.CommandLine;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Tapka;

var noPause = new Option<bool>("--no-pause") { Description = "Do not wait for a key after installation." };
var setup = new RootCommand("Install tapka and its dependencies for the current user.") { noPause };

#if TAPKA_TEST_BUILD
var repositoryPath = new Option<string>("--repository") { Required = true };
var testRoot = new Option<string>("--root") { Required = true };
var fixtureDownloads = new Option<string>("--fixture-downloads");
setup.Options.Add(repositoryPath);
setup.Options.Add(testRoot);
setup.Options.Add(fixtureDownloads);
#endif

setup.SetAction(async (parseResult, cancellationToken) =>
{
    const string name = "tapka-setup";
    try
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            throw new InvalidOperationException("Installation requires Windows x64.");
        }

        var version = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "TapkaVersion").Value!;
#if TAPKA_TEST_BUILD
        var scope = InstallScope.Create(parseResult.GetValue(testRoot));
        var source = parseResult.GetRequiredValue(repositoryPath);
        var fixtures = parseResult.GetValue(fixtureDownloads);
        using var http = fixtures is null ? new HttpClient() : new HttpClient(new TestDownloads(fixtures));
#else
        var scope = InstallScope.Create();
        const string source = PackageRepository.DefaultSource;
        using var http = new HttpClient();
#endif
        Console.WriteLine($"Installing tapka {version}...");
        var repository = new PackageRepository(http, source);
        var installer = new PackageInstaller(scope, repository, http, systemPowerShell: null);
        await installer.InstallAsync("tapka", version, "x64", cancellationToken, verifyTapka: true);
        Console.WriteLine("Tapka is installed.");
        if (!parseResult.GetValue(noPause))
        {
            Console.WriteLine("Open a new terminal and run tapka --help.");
        }
        return 0;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine($"{name}: installation cancelled.");
        return 130;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine($"{name}: the repository request timed out.");
        return 1;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
        or HttpRequestException or InvalidOperationException or Win32Exception
        or System.Security.SecurityException or ArgumentException)
    {
        Console.Error.WriteLine($"{name}: {error.Message}");
        return 1;
    }
    finally
    {
        if (!parseResult.GetValue(noPause) && !cancellationToken.IsCancellationRequested &&
            !Console.IsInputRedirected && !Console.IsOutputRedirected && SetupConsole.IsOnlyProcess())
        {
            Console.WriteLine("Press any key to close.");
            Console.ReadKey(intercept: true);
        }
    }
});

return await setup.Parse(args).InvokeAsync();

internal static class SetupConsole
{
    internal static bool IsOnlyProcess() => GetConsoleProcessList(out _, 1) == 1;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetConsoleProcessList(out uint processId, uint processCount);
}
