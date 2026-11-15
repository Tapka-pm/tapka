using System.Diagnostics;

namespace Tapka;

internal static class InstallProcess
{
    public static async Task RunAsync(ProcessStartInfo start, string description, CancellationToken cancellationToken,
        Func<Process, CancellationToken, Task>? readOutput = null, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = readOutput is not null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {description}.");
        try
        {
            process.StandardInput.Close();
            if (readOutput is not null)
            {
                await readOutput(process, deadline.Token);
            }
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (Exception error)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }

            if (error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"The {description} timed out.", error);
            }
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new IOException($"The {description} failed (exit code {process.ExitCode}).");
        }
    }
}
