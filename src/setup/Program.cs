using Microsoft.Data.Sqlite;
using System.CommandLine;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Tapka;
using Tapka.Terminal;
using Windows.Win32;

return await SetupCli.RunAsync(args, new SystemTerminal(), Pause);

static void Pause(ITerminal terminal)
{
    if (OperatingSystem.IsWindows() && !Console.IsInputRedirected && !Console.IsOutputRedirected && SetupConsole.IsOnlyProcess())
    {
        terminal.Out.WriteLine("Press any key to close.");
        Console.ReadKey(intercept: true);
    }
}

internal static class SetupCli
{
    internal static Task<int> RunAsync(string[] args, ITerminal terminal, Action<ITerminal>? pause = null)
    {
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
                terminal.Out.WriteLine($"Installing tapka {version}...");
                var repository = new PackageRepository(http, source);
                var installer = new PackageInstaller(scope, repository, http, terminal, systemPowerShell: null,
                    catalog: new CatalogCache(scope, http, terminal), refreshCatalog: true);
                using var sqlite = SetupSqlite.Load(terminal);
                await installer.InstallAsync("tapka", version, "x64", cancellationToken, verifyTapka: true, trackLatest: true);
                terminal.Out.WriteLine("Tapka is installed.");
                if (!parseResult.GetValue(noPause))
                {
                    terminal.Out.WriteLine("Open a new terminal and run tapka --help.");
                }
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                terminal.Error.WriteLine($"{name}: installation cancelled.");
                return 130;
            }
            catch (OperationCanceledException)
            {
                terminal.Error.WriteLine($"{name}: the repository request timed out.");
                return 1;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or HttpRequestException or InvalidOperationException or Win32Exception
                or System.Security.SecurityException or ArgumentException or SqliteException or DllNotFoundException)
            {
                terminal.Error.WriteLine($"{name}: {error.Message}");
                return 1;
            }
            finally
            {
                if (!parseResult.GetValue(noPause) && !cancellationToken.IsCancellationRequested)
                {
                    pause?.Invoke(terminal);
                }
            }
        });

        return setup.Parse(args).InvokeAsync(new InvocationConfiguration { Output = terminal.Out, Error = terminal.Error });
    }
}

internal static class SetupConsole
{
    internal static bool IsOnlyProcess() => PInvoke.GetConsoleProcessList(stackalloc uint[1]) == 1;
}
