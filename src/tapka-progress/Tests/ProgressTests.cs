using Meziantou.Framework.InlineSnapshotTesting;
using Tapka.Terminal.Testing;

namespace Tapka.Progress.Tests;

public sealed class ProgressTests
{
    private static ProgressTree Live(TerminalTestConsole console, CancellationToken token = default) =>
        new(console.Out, true, () => (console.Width, console.Height), token);

    [Fact]
    public void Shared_dependencies_propagate_state_without_duplicate_edges()
    {
        using var console = new TerminalTestConsole(120, 20);
        using var tree = Live(console);
        tree.Add("app");
        tree.Add("left", "app");
        tree.Add("right", "app");
        tree.Add("shared", "left");
        tree.Add("shared", "right");
        tree.Add("shared", "right");
        tree.Report("shared", "Downloading", .5);
        tree.RenderFrame();

        InlineSnapshot.Validate(console.GetScreenText(), """
            [.] app - Waiting - [----------------]
            |- [.] left - Waiting - [----------------]
            |  `- [|] shared - Downloading - [########--------] 50%
            `- [.] right - Waiting - [----------------]
               `- [|] shared - Downloading - [########--------] 50% (shared)
            """);
    }

    [Fact]
    public void Cycles_stop_at_a_shared_reference()
    {
        using var console = new TerminalTestConsole(120, 10);
        using var tree = Live(console);
        tree.Add("app");
        tree.Add("child", "app");
        tree.Add("app", "child");
        tree.RenderFrame();

        InlineSnapshot.Validate(console.GetScreenText(), """
            [.] app - Waiting - [----------------]
            `- [.] child - Waiting - [----------------]
               `- [.] app - Waiting - [----------------] (shared)
            """);
    }

    [Fact]
    public void Plain_output_emits_only_one_final_tree()
    {
        using var console = new TerminalTestConsole(120, 10);
        using var tree = new ProgressTree(console.Out);
        tree.Add("app");
        tree.Add("pending", "app");
        tree.Report("app", "Downloading", .5);
        tree.RenderFrame();
        Assert.Equal("", console.GetScreenText());
        Assert.Contains("50%", tree.Snapshot());
        Assert.Equal("", console.GetScreenText());
        tree.Report("app", "Done", state: ProgressState.Completed);
        tree.Dispose();

        InlineSnapshot.Validate(console.GetScreenText(), """
            [+] app - Done - [################] 100%
            `- [!] pending - Aborted (Waiting) - [----------------]
            """);
        var final = console.GetScreenText();
        tree.Dispose();
        tree.RenderFrame();
        Assert.Equal(final, console.GetScreenText());
    }

    [Fact]
    public void Plain_stream_contains_no_terminal_commands_and_sanitizes_input()
    {
        using var output = new StringWriter();
        using var tree = new ProgressTree(output);
        tree.Add("app\n\u001b");
        tree.Report("app\n\u001b", "Unsafe\nstatus\u001b[2J", name: "safe\tname");
        Assert.Equal("[|] safe name - Unsafe status [2J - [>---------------]", tree.Snapshot());
        Assert.Equal("", output.ToString());
        tree.WriteLine("Message\n\u001b[2J");
        tree.Dispose();
        Assert.DoesNotContain('\u001b', output.ToString());
        Assert.StartsWith("Message  [2J" + Environment.NewLine, output.ToString());
        var final = output.ToString();
        tree.Dispose();
        Assert.Equal(final, output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_preserves_completed_work_and_cancels_unfinished_nodes(bool interactive)
    {
        using var console = new TerminalTestConsole(120, 10);
        using var cancellation = new CancellationTokenSource();
        using var tree = interactive ? Live(console, cancellation.Token) : new ProgressTree(console.Out, cancellation.Token);
        tree.Add("done");
        tree.Report("done", "Done", state: ProgressState.Completed);
        tree.Add("running");
        tree.Report("running", "Downloading");
        tree.Add("waiting");
        tree.RenderFrame();
        cancellation.Cancel();
        tree.Dispose();

        InlineSnapshot.Validate(console.GetScreenText(), """
            [+] done - Done - [################] 100%
            [x] running - Cancelled (Downloading) - [----------------]
            [x] waiting - Cancelled (Waiting) - [----------------]
            """);
    }

    [Fact]
    public void Parallel_reports_leave_a_renderable_shared_state()
    {
        using var console = new TerminalTestConsole(120, 10);
        using var tree = Live(console);
        tree.Add("app");
        Parallel.For(0, 100, i => tree.Report("app", "Downloading", i / 100d));
        tree.Report("app", "Downloaded", 1);
        tree.RenderFrame();

        InlineSnapshot.Validate(console.GetScreenText(), """
            [|] app - Downloaded - [################] 100%
            """);
    }

    [Fact]
    public void Frames_replace_longer_lines_and_advance_indeterminate_animation()
    {
        using var console = new TerminalTestConsole(120, 10);
        using var tree = Live(console);
        tree.Add("app");
        tree.Report("app", "A much longer downloading status", .5);
        tree.RenderFrame();
        tree.Report("app", "Loading");
        tree.RenderFrame();

        InlineSnapshot.Validate(console.GetScreenText(), """
            [/] app - Loading - [->--------------]
            """);
        tree.RenderFrame();
        InlineSnapshot.Validate(console.GetScreenText(), """
            [-] app - Loading - [-->-------------]
            """);
        tree.Report("app", "Done", state: ProgressState.Completed);
        tree.Dispose();
        InlineSnapshot.Validate(console.GetScreenText(), """
            [+] app - Done - [################] 100%
            """);
    }

    [Fact]
    public void Frames_reserve_a_column_and_do_not_wrap()
    {
        using var console = new TerminalTestConsole(20, 10);
        using var tree = Live(console);
        tree.Add("app");
        tree.RenderFrame();
        InlineSnapshot.Validate(console.GetScreenText(), """
            [.] app - Waiting -
            """);
        tree.Report("app", "Done", state: ProgressState.Completed);
        tree.RenderFrame();
        InlineSnapshot.Validate(console.GetScreenText(), """
            [+] app - Done - [#
            """);
    }

    [Fact]
    public void Frames_limit_height_but_disposal_prints_the_full_tree()
    {
        using var console = new TerminalTestConsole(120, 5);
        using var tree = Live(console);
        foreach (var id in new[] { "one", "two", "three", "four" })
        {
            tree.Add(id);
            tree.Report(id, "Done", state: ProgressState.Completed);
        }
        tree.RenderFrame();
        InlineSnapshot.Validate(console.GetScreenText(), """
            [+] one - Done - [################] 100%
            [+] two - Done - [################] 100%
            ... 2 more rows (full tree on completion)
            """);
        tree.Dispose();
        InlineSnapshot.Validate(console.GetScreenText(), """
            [+] one - Done - [################] 100%
            [+] two - Done - [################] 100%
            [+] three - Done - [################] 100%
            [+] four - Done - [################] 100%
            """);
    }

    [Fact]
    public void Nested_pauses_clear_the_frame_and_resume_after_the_last_scope()
    {
        using var console = new TerminalTestConsole(120, 10);
        using var tree = Live(console);
        tree.Add("app");
        tree.Report("app", "Loading");
        tree.RenderFrame();
        using var outer = tree.Pause();
        using var inner = tree.Pause();
        Assert.Equal("", console.GetScreenText());
        console.Out.WriteLine("External output");
        tree.RenderFrame();
        outer.Dispose();
        outer.Dispose();
        tree.RenderFrame();
        Assert.Equal("External output", console.GetScreenText());
        inner.Dispose();
        tree.RenderFrame();
        InlineSnapshot.Validate(console.GetScreenText(), """
            External output
            [/] app - Loading - [->--------------]
            """);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diagnostics_survive_subsequent_frames_and_disposal(bool useError)
    {
        using var console = new TerminalTestConsole(120, 10);
        using var tree = Live(console);
        tree.Add("app");
        tree.RenderFrame();
        tree.WriteLine("Warning\nmessage\u001b[2J", useError ? console.Error : null);
        Assert.Equal("Warning message [2J", console.GetScreenText());
        tree.RenderFrame();
        InlineSnapshot.Validate(console.GetScreenText(), """
            Warning message [2J
            [.] app - Waiting - [----------------]
            """);
        tree.Dispose();
        InlineSnapshot.Validate(console.GetScreenText(), """
            Warning message [2J
            [!] app - Aborted (Waiting) - [----------------]
            """);
    }
}
