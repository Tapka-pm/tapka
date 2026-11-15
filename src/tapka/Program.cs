using Microsoft.Data.Sqlite;
using System.CommandLine;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Tapka;

var package = new Argument<string>("package") { Description = "Package ID or ID@version-or-alias. Defaults to the latest release." };
var ids = new Argument<string[]>("package") { Arity = ArgumentArity.ZeroOrMore, Description = "Installed package IDs. Omit to upgrade all packages." };
var install = new Command("install", "Install a package for the current user.") { package };
var update = new Command("update", "Refresh local package catalogs.");
var upgrade = new Command("upgrade", "Upgrade installed packages using their saved selectors.") { ids };
var root = new RootCommand("Portable package manager for Windows.") { install, update, upgrade };
InventoryCommands.AddTo(root);

Configure(install, async (result, scope, http, cache, source, architecture, powerShell, token) =>
{
    var reference = result.GetRequiredValue(package).Split('@', 2);
    var selector = reference.Length == 2 ? reference[1] : "latest";
    if (string.IsNullOrWhiteSpace(reference[0]) || string.IsNullOrWhiteSpace(selector))
        throw new ArgumentException("Specify a package ID as <package> or <package>@<version-or-alias>.");
    var repository = new PackageRepository(http, source ?? PackageRepository.DefaultSource);
    var installer = new PackageInstaller(scope, repository, http, powerShell, cache);
    await installer.InstallAsync(PackageRepository.ValidateId(reference[0].ToLowerInvariant()), selector, architecture, token);
});

Configure(update, async (_, scope, _, cache, source, _, _, token) =>
{
    using var installLock = await scope.AcquireInstallLockAsync(token);
    var sources = new HashSet<string>(StringComparer.Ordinal) { source ?? PackageRepository.DefaultSource };
    if (source is null && InstalledPackages.Exists(scope))
    {
        using var inventory = await InstalledPackages.OpenForReadAsync(scope, token);
        foreach (var entry in inventory.List())
        {
            if (entry.Repository is not null) sources.Add(entry.Repository);
        }
    }
    foreach (var origin in sources) await cache.OpenAsync(origin, force: true, token);
});

Configure(upgrade, async (result, scope, http, cache, source, architecture, powerShell, token) =>
{
    var installer = new PackageInstaller(scope, new PackageRepository(http, PackageRepository.DefaultSource), http, powerShell, cache);
    var selected = (result.GetValue(ids) ?? []).Select(id => PackageRepository.ValidateId(id.ToLowerInvariant())).ToArray();
    var origin = source is null ? null : new PackageRepository(http, source).Source;
    await installer.UpgradeAsync(selected, architecture, token, origin);
});

return await root.Parse(args).InvokeAsync();

static void Configure(Command command,
    Func<ParseResult, InstallScope, HttpClient, CatalogCache, string?, string, string?, CancellationToken, Task> action)
{
    var source = new Option<string>("--repository") { Description = "HTTPS base URL or local path to a Tapka package repository." };
    command.Options.Add(source);

#if TAPKA_TEST_BUILD
    var testRoot = new Option<string>("--root") { Description = "Absolute test installation directory.", Required = true };
    var fixturesOption = new Option<string>("--fixture-downloads") { Description = "Directory containing local integration test downloads." };
    command.Options.Add(testRoot);
    command.Options.Add(fixturesOption);
#endif
    command.SetAction(async (result, cancellationToken) =>
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
#if TAPKA_TEST_BUILD
            var scope = InstallScope.Create(result.GetValue(testRoot));
            var fixtures = result.GetValue(fixturesOption);
            using var http = fixtures is null ? new HttpClient() : new HttpClient(new TestDownloads(fixtures));
            var powerShell = fixtures is null ? PackageInstaller.FindSystemPowerShell() : null;
#else
            var scope = InstallScope.Create();
            using var http = new HttpClient();
            var powerShell = PackageInstaller.FindSystemPowerShell();
#endif
            var cache = new CatalogCache(scope, http);
            await action(result, scope, http, cache, result.GetValue(source), architecture, powerShell, cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"tapka: {command.Name} cancelled.");
            return 130;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("tapka: the repository request timed out.");
            return 1;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or HttpRequestException or InvalidOperationException or Win32Exception or System.Security.SecurityException
            or ArgumentException or SqliteException)
        {
            Console.Error.WriteLine($"tapka: {error.Message}");
            return 1;
        }
    });
}
