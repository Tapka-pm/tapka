using System.Diagnostics;

namespace Tapka;

internal static class InstallProcess
{
    public static async Task RunAsync(ProcessStartInfo start, string description, CancellationToken cancellationToken,
        Func<Process, CancellationToken, Task>? readOutput = null, TimeSpan? timeout = null, Action<string>? diagnostic = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {description}.");
        try
        {
            process.StandardInput.Close();
            var readers = new List<Task>
            {
                readOutput is not null ? readOutput(process, deadline.Token)
                    : ForwardAsync(process.StandardOutput, diagnostic ?? Console.WriteLine, deadline.Token),
                ForwardAsync(process.StandardError, diagnostic ?? Console.Error.WriteLine, deadline.Token),
                process.WaitForExitAsync(deadline.Token)
            };
            // Observe a rejected archive listing immediately, even while the child is still writing.
            while (readers.Count > 0)
            {
                var finished = await Task.WhenAny(readers);
                await finished;
                readers.Remove(finished);
            }
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

    private static async Task ForwardAsync(StreamReader reader, Action<string> write, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) is { } line) write(line);
    }
}
