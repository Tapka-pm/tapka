using Microsoft.Data.Sqlite;
using System.CommandLine;

namespace Tapka;

internal static class InventoryCommands
{
    public static void AddTo(RootCommand root)
    {
        var query = new Argument<string?>("query")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Filter by a substring of the package ID."
        };
        var explicitOnly = new Option<bool>("--explicit") { Description = "Show only explicitly installed packages." };
        var list = new Command("list", "List installed packages.") { query, explicitOnly };
        var infoId = new Argument<string>("id") { Description = "Installed package ID." };
        var info = new Command("info", "Show details about an installed package.") { infoId };
        var pathId = new Argument<string>("id") { Description = "Installed package ID." };
        var path = new Command("path", "Print the installed package directory.") { pathId };

        Configure(list, (inventory, _, result) =>
        {
            var packages = inventory.List(result.GetValue(query), result.GetValue(explicitOnly));
            if (packages.Count > 0)
            {
                Console.WriteLine("ID\tVERSION\tSELECTOR\tINSTALLATION");
                foreach (var package in packages)
                {
                    Console.WriteLine($"{package.Id}\t{package.Version}\t{package.Selector ?? "-"}\t{Installation(package)}");
                }
            }
        });
        Configure(info, (inventory, scope, result) =>
        {
            var id = ReadId(result, infoId);
            var (package, manifest, requiredBy) = inventory.Details(id) ?? throw NotInstalled(id);
            var directory = Path.Combine(scope.Root, package.PackagePath);
            Console.WriteLine($"ID: {id}");
            Console.WriteLine($"Version: {package.Version}");
            Console.WriteLine($"Selector: {package.Selector ?? "(none)"}");
            Console.WriteLine($"Installation: {Installation(package)}");
            Console.WriteLine($"Path: {directory}");
            Console.WriteLine($"Architecture: {Known(package.Architecture)}");
            Console.WriteLine($"Repository: {Known(package.Repository)}");
            Console.WriteLine($"Download URL: {Known(package.DownloadUrl)}");
            Console.WriteLine($"Checksum: {Known(package.Checksum)}");
            Console.WriteLine($"Installed at: {Known(package.InstalledAt)}");
            Console.WriteLine($"Version changed at: {Known(package.VersionChangedAt)}");
            PrintItems("Commands", manifest.Commands.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key} -> {pair.Value}"));
            PrintItems("Dependencies", manifest.Dependencies.Select(dependency =>
                dependency.Rename.Count == 0 ? dependency.Id :
                $"{dependency.Id} ({string.Join(", ", dependency.Rename.Select(pair => $"{pair.Key} -> {pair.Value}"))})"));
            PrintItems("Required by", requiredBy);
        });
        Configure(path, (inventory, scope, result) =>
        {
            var id = ReadId(result, pathId);
            var package = inventory.Find(id) ?? throw NotInstalled(id);
            var directory = Path.Combine(scope.Root, package.PackagePath);
            if (!Directory.Exists(directory))
            {
                throw new IOException($"Package directory '{directory}' is missing.");
            }
            Console.WriteLine(directory);
        });

        root.Subcommands.Add(list);
        root.Subcommands.Add(info);
        root.Subcommands.Add(path);
    }

    private static void Configure(Command command, Action<InstalledPackages, InstallScope, ParseResult> action)
    {
#if TAPKA_TEST_BUILD
        var testRoot = new Option<string>("--root") { Description = "Absolute test installation directory.", Required = true };
        command.Options.Add(testRoot);
#endif
        command.SetAction(async (result, cancellationToken) =>
        {
            try
            {
#if TAPKA_TEST_BUILD
                var scope = InstallScope.Create(result.GetValue(testRoot));
#else
                var scope = InstallScope.Create();
#endif
                using var inventory = await InstalledPackages.OpenForReadAsync(scope, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                action(inventory, scope, result);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return 130;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or InvalidOperationException or SqliteException or ArgumentException)
            {
                Console.Error.WriteLine($"tapka: {error.Message}");
                return 1;
            }
        });
    }

    private static string ReadId(ParseResult result, Argument<string> argument) =>
        PackageRepository.ValidateId(result.GetRequiredValue(argument).ToLowerInvariant());

    private static IOException NotInstalled(string id) => new($"Package '{id}' is not installed.");
    private static string Known(string? value) => value ?? "(unknown)";
    private static string Installation(InstalledPackage package) => package.Selector is null ? "automatic" : "explicit";

    private static void PrintItems(string label, IEnumerable<string> items)
    {
        Console.WriteLine($"{label}:");
        foreach (var item in items.DefaultIfEmpty("(none)"))
        {
            Console.WriteLine($"  {item}");
        }
    }
}
