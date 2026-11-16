using Tapka.Progress;

internal static class ProgressTests
{
    public static void Run()
    {
        using var output = new StringWriter();
        var tree = new ProgressTree(output);
        tree.Add("app");
        tree.Add("left", "app");
        tree.Add("right", "app");
        tree.Add("shared", "left");
        tree.Add("shared", "right");
        tree.Add("shared", "right");
        tree.Report("shared", "Downloading", .5);
        var snapshot = tree.Snapshot();
        Check(snapshot.Contains("|- [.] left") && snapshot.Contains("`- [.] right"), "Missing tree branches.");
        Check(snapshot.Split("shared - Downloading").Length == 3, "Shared dependency must appear once per parent.");
        Check(snapshot.Split("50%").Length == 3 && snapshot.Contains("(shared)"), "Shared state did not propagate.");
        Check(output.ToString() == "", "Redirected output must not emit intermediate frames.");

        tree.Add("app", "shared");
        Check(tree.Snapshot().Length < 2000, "Cycles must not recurse indefinitely.");
        tree.Report("app", "Done", state: ProgressState.Completed);
        tree.Report("left", "Unsafe\nstatus\u001b[2J");
        Parallel.For(0, 100, i => tree.Report("shared", "Downloading", i / 100d));
        tree.Dispose();
        var final = output.ToString();
        Check(final.Contains("[+] app - Done") && final.Contains("100%"), "Missing successful completion.");
        Check(final.Contains("[!] shared - Aborted"), "Unfinished work was reported as successful.");
        Check(!final.Contains('\u001b'), "Plain output contains terminal commands.");
        tree.Dispose();
        Check(final == output.ToString(), "Disposal duplicated the final tree.");

        using var cancelledOutput = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        using (var cancelled = new ProgressTree(cancelledOutput, cancellation.Token))
        {
            cancelled.Add("cancelled");
            cancelled.Report("cancelled", "Downloading");
            Check(!cancelled.Snapshot().Contains('%'), "Unknown size must not invent a percentage.");
            cancelled.Report("cancelled", "Unsafe\nstatus\u001b[2J");
            Check(!cancelled.Snapshot().Contains('\u001b') && !cancelled.Snapshot().Contains('\n'), "Status was not sanitized.");
            cancellation.Cancel();
        }
        Check(cancelledOutput.ToString().Contains("[x] cancelled - Cancelled"), "Cancellation state was lost.");
        Console.WriteLine("Progress tests passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
