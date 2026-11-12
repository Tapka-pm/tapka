using System.CommandLine;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Tapka;

var package = new Argument<string>("package") { Description = "Package to install (git)." };
var install = new Command("install", "Install a package for the current user.") { package };
var root = new RootCommand("Portable package manager for Windows x64.") { install };

#if TAPKA_TEST_BUILD
var testRoot = new Option<string>("--root") { Description = "Absolute test installation directory.", Required = true };
install.Options.Add(testRoot);
#endif

install.SetAction(async (parseResult, cancellationToken) =>
{
    if (!string.Equals(parseResult.GetValue(package), "git", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("tapka: unknown package. The available package is git.");
        return 1;
    }

    if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
    {
        Console.Error.WriteLine("tapka: installation requires Windows x64.");
        return 1;
    }

    try
    {
#if TAPKA_TEST_BUILD
        var scope = InstallScope.Create(parseResult.GetValue(testRoot));
#else
        var scope = InstallScope.Create();
#endif
        await new GitInstaller(scope).InstallAsync(cancellationToken);
        return 0;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine("tapka: installation cancelled.");
        return 130;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException
        or InvalidOperationException or Win32Exception or System.Security.SecurityException)
    {
        Console.Error.WriteLine($"tapka: {error.Message}");
        return 1;
    }
});

return await root.Parse(args).InvokeAsync();
