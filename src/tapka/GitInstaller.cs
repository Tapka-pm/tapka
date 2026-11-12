using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Tapka;

internal sealed class GitInstaller(InstallScope scope)
{
    private const string InstallMarker = ".tapka-version";

    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(scope.Root);
        using var installLock = await AcquireLockAsync(cancellationToken);

        var packages = Directory.CreateDirectory(Path.Combine(scope.Root, "packages")).FullName;
        var destination = Path.Combine(packages, "git");
        var marker = Path.Combine(destination, InstallMarker);
        var installed = File.Exists(Path.Combine(destination, GitPackage.Executable)) &&
            File.Exists(marker) && File.ReadAllText(marker) == GitPackage.Version;

        if (!installed)
        {
            await InstallPackageAsync(packages, destination, cancellationToken);
        }

        var shims = Directory.CreateDirectory(Path.Combine(scope.Root, "shims")).FullName;
        InstallShim(shims);
        scope.RegisterShims(shims);

        Console.WriteLine(installed
            ? $"Git {GitPackage.Version} is already installed at \"{destination}\"."
            : $"Installed Git {GitPackage.Version} at \"{destination}\".");
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var lockFile = Path.Combine(scope.Root, ".install.lock");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return File.Open(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                if (elapsed.Elapsed >= TimeSpan.FromMinutes(10))
                {
                    throw new IOException("Timed out waiting for another tapka installation to finish.", error);
                }

                await Task.Delay(200, cancellationToken);
            }
        }
    }

    private static async Task InstallPackageAsync(string packages, string destination, CancellationToken cancellationToken)
    {
        // Staging beside the final directory allows publication with a single same-volume rename.
        var staging = Directory.CreateDirectory(Path.Combine(packages, $".git-{Guid.NewGuid():N}")).FullName;
        var archive = Path.Combine(staging, "PortableGit.7z.exe");
        // The pinned self-extractor creates PortableGit beside the downloaded archive.
        var extracted = Path.Combine(staging, "PortableGit");
        var previous = Path.Combine(staging, "previous");
        try
        {
            Console.WriteLine($"Downloading PortableGit {GitPackage.Version}...");
            await DownloadAsync(archive, cancellationToken);

            await using (var stream = File.OpenRead(archive))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!hash.Equals(GitPackage.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"PortableGit SHA-256 mismatch. Expected {GitPackage.Sha256}, got {hash}.");
                }
            }

            Console.WriteLine("Extracting PortableGit...");
            await ExtractAsync(archive, extracted, cancellationToken);
            File.WriteAllText(Path.Combine(extracted, InstallMarker), GitPackage.Version);
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(destination))
            {
                Directory.Move(destination, previous);
            }

            try
            {
                Directory.Move(extracted, destination);
            }
            catch
            {
                if (Directory.Exists(previous))
                {
                    Directory.Move(previous, destination);
                }

                throw;
            }
        }
        finally
        {
            // Keep the previous package recoverable if the rollback itself failed.
            if (!Directory.Exists(previous) || Directory.Exists(destination))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"tapka: could not clean up \"{staging}\": {error.Message}");
                }
            }
        }
    }

    private static async Task DownloadAsync(string archive, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            using var response = await client.GetAsync(GitPackage.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await using var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await response.Content.CopyToAsync(file, deadline.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("PortableGit download timed out.", error);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new IOException($"Could not download PortableGit: {error.Message}", error);
        }
    }

    private static async Task ExtractAsync(string archive, string extracted, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(archive)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(archive)!
        };
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add("-gm2");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Could not start the PortableGit extractor.");
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // The extractor exited between cancellation and Kill.
                }

                throw;
            }

            if (process.ExitCode != 0)
            {
                throw new IOException($"PortableGit extraction failed (exit code {process.ExitCode}).");
            }

            if (!File.Exists(Path.Combine(extracted, GitPackage.Executable)) ||
                !File.Exists(Path.Combine(extracted, @"ucrt64\bin\git.exe")) ||
                File.Exists(Path.Combine(extracted, "post-install.bat")))
            {
                throw new IOException("PortableGit extraction did not produce a complete installation.");
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("PortableGit extraction timed out.", error);
        }
        catch (Win32Exception error)
        {
            throw new IOException($"Could not run the PortableGit extractor: {error.Message}", error);
        }
    }

    private static void InstallShim(string shims)
    {
        using var resource = typeof(GitInstaller).Assembly.GetManifestResourceStream("tapka.git.exe")
            ?? throw new InvalidOperationException("The tapka binary is missing its native Git shim.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var shimBytes = buffer.ToArray();
        var destination = Path.Combine(shims, "git.exe");
        if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(shimBytes))
        {
            return;
        }

        var temporary = Path.Combine(shims, $".git-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, shimBytes);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
