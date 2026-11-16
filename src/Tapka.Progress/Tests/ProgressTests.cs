using Meziantou.Framework.InlineSnapshotTesting;
using Tapka.Terminal;
using Tapka.Terminal.Testing;

namespace Tapka.Progress.Tests;

public sealed class ProgressTests
{
    private sealed record PlainTerminal(TextWriter Out, TextWriter Error) : ITerminal
    {
        public int Width => 80;
        public int Height => 24;
        public bool IsInteractive => false;
    }

    [Fact]
    public void Shared_dependencies_show_the_same_state_under_each_parent()
    {
        using var console = new TerminalTestConsole(120, 20);
        var app = new Dep { Name = "app" };
        var left = new Dep { Name = "left" };
        var right = new Dep { Name = "right" };
        var shared = new Dep { Name = "shared" };
        app.Add(left);
        app.Add(right);
        left.Add(shared);
        right.Add(shared);
        right.Add(shared);
        shared.SetStatus(DepStatus.Downloading, .5);

        var display = new ProgressDisplay(console, [app]);
        display.RenderFrame();

        Assert.Same(shared, Assert.Single(right.Deps));
        InlineSnapshot.Validate(console.GetScreenText(), """
            [.] app - Awaiting
            |- [.] left - Awaiting
            |  `- [>] shared - Downloading - [########--------] 50%
            `- [.] right - Awaiting
               `- [>] shared - Downloading - [########--------] 50% (shared)
            """);
    }

    [Fact]
    public void Cycles_stop_at_a_shared_reference()
    {
        using var console = new TerminalTestConsole(120, 10);
        var app = new Dep { Name = "app" };
        var child = new Dep { Name = "child" };
        app.Add(child);
        child.Add(app);

        var display = new ProgressDisplay(console, [app]);
        display.RenderFrame();

        InlineSnapshot.Validate(console.GetScreenText(), """
            [.] app - Awaiting
            `- [.] child - Awaiting
               `- [.] app - Awaiting (shared)
            """);
    }

    [Fact]
    public void Only_active_stages_display_a_bar()
    {
        using var console = new TerminalTestConsole(120, 10);
        var downloading = new Dep { Name = "downloading" };
        var downloaded = new Dep { Name = "downloaded" };
        var installing = new Dep { Name = "installing" };
        var installed = new Dep { Name = "installed" };
        downloading.SetStatus(DepStatus.Downloading, .5, "524,288 bytes");
        downloaded.SetStatus(DepStatus.Downloaded);
        installing.SetStatus(DepStatus.Installing, detail: "Extracting");
        installed.SetStatus(DepStatus.Installed, detail: "Pinned");

        var display = new ProgressDisplay(console, [new Dep(), downloading, downloaded, installing, installed]);
        display.RenderFrame();

        InlineSnapshot.Validate(console.GetScreenText(), """
            [.] (unnamed) - Awaiting
            [>] downloading - Downloading (524,288 bytes) - [########--------] 50%
            [.] downloaded - Downloaded
            [>] installing - Installing (Extracting) - [>---------------]
            [+] installed - Installed (Pinned)
            """);
    }

    [Theory]
    [InlineData(DepStatus.Downloading)]
    [InlineData(DepStatus.Installing)]
    public void Any_active_stage_can_animate_and_clear_a_previous_fraction(DepStatus stage)
    {
        using var console = new TerminalTestConsole(120, 10);
        var dep = new Dep { Name = "app" };
        var display = new ProgressDisplay(console, [dep]);
        dep.SetStatus(stage, .5, "A longer status detail");
        display.RenderFrame();
        Assert.Contains("[########--------] 50%", console.GetScreenText());

        dep.SetStatus(stage);
        display.RenderFrame();
        Assert.Equal($"[>] app - {stage} - [->--------------]", console.GetScreenText());
        display.RenderFrame();
        Assert.Equal($"[>] app - {stage} - [-->-------------]", console.GetScreenText());

        dep.SetStatus(DepStatus.Installed);
        display.Finish();
        display.RenderFrame();
        display.Finish();
        Assert.Equal("[+] app - Installed", console.GetScreenText());
    }

    [Fact]
    public async Task Plain_output_emits_diagnostics_and_one_final_tree()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var terminal = new PlainTerminal(output, error);
        var app = new Dep { Name = "app\n\u001b" };
        var dependency = new Dep { Name = "before" };
        app.Add(dependency);

        await Dep.RunAsync(terminal, [app], async diagnostics =>
        {
            app.SetStatus(DepStatus.Downloading, .5);
            await Task.Yield();
            Assert.Equal("", output.ToString());

            var added = new Dep { Name = "after" };
            app.Add(added);
            dependency.SetStatus(DepStatus.Installed);
            added.SetStatus(DepStatus.Installed);
            diagnostics.Out.WriteLine("Message\n\u001b[2J");
            diagnostics.Error.WriteLine("Warning");
            app.SetStatus(DepStatus.Installed);
        });

        InlineSnapshot.Validate(output.ToString().ReplaceLineEndings("\n").TrimEnd(), """
            Message  [2J
            [+] app   - Installed
            |- [+] before - Installed
            `- [+] after - Installed
            """);
        Assert.Equal("Warning" + Environment.NewLine, error.ToString());
        output.WriteLine("Still open");
        Assert.EndsWith("Still open" + Environment.NewLine, output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_preserves_node_states_and_marks_incomplete_rows(bool interactive)
    {
        using var console = new TerminalTestConsole(120, 10, interactive);
        using var cancellation = new CancellationTokenSource();
        var installed = new Dep { Name = "installed" };
        installed.SetStatus(DepStatus.Installed);
        var dep = new Dep { Name = "app" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Dep.RunAsync(console, [installed, dep], _ =>
            {
                dep.SetStatus(DepStatus.Installing, detail: "Awaiting commit");
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, cancellation.Token));

        Assert.Equal(DepStatus.Installing, dep.Status);
        InlineSnapshot.Validate(console.GetScreenText(), """
            [+] installed - Installed
            [x] app - Cancelled (Installing (Awaiting commit))
            """);
    }

    [Fact]
    public async Task Failed_operation_keeps_the_exception_and_reports_the_stage()
    {
        using var console = new TerminalTestConsole(120, 10);
        var dep = new Dep { Name = "app" };
        var failure = new IOException("Checksum mismatch");

        var error = await Assert.ThrowsAsync<IOException>(() => Dep.RunAsync(console, [dep], output =>
        {
            dep.SetStatus(DepStatus.Downloading, detail: "Verifying checksum");
            output.Error.WriteLine("Download failed");
            return Task.FromException(failure);
        }));

        Assert.Same(failure, error);
        Assert.Equal(DepStatus.Downloading, dep.Status);
        InlineSnapshot.Validate(console.GetScreenText(), """
            Download failed
            [!] app - Aborted (Downloading (Verifying checksum))
            """);
    }

    [Fact]
    public void Parallel_updates_leave_a_renderable_state()
    {
        using var console = new TerminalTestConsole(120, 10);
        var dep = new Dep { Name = "app" };
        Parallel.For(0, 100, i => dep.SetStatus(DepStatus.Downloading, i / 100d));
        dep.SetStatus(DepStatus.Downloaded);
        var display = new ProgressDisplay(console, [dep]);
        display.RenderFrame();

        Assert.Equal("[.] app - Downloaded", console.GetScreenText());
    }

    [Fact]
    public void Frames_reserve_a_column_and_do_not_wrap()
    {
        using var console = new TerminalTestConsole(20, 10);
        var dep = new Dep { Name = "app" };
        dep.SetStatus(DepStatus.Downloading, .5);
        var display = new ProgressDisplay(console, [dep]);
        display.RenderFrame();

        Assert.Equal("[>] app - Downloadi", console.GetScreenText());
    }

    [Fact]
    public void Frames_limit_height_but_completion_prints_the_full_tree()
    {
        using var console = new TerminalTestConsole(120, 6);
        var roots = new[] { "one", "two", "three", "four", "five" }.Select(name => new Dep { Name = name }).ToArray();
        foreach (var dep in roots) dep.SetStatus(DepStatus.Installed);
        var display = new ProgressDisplay(console, roots);
        display.RenderFrame();

        InlineSnapshot.Validate(console.GetScreenText(), """
            [+] one - Installed
            [+] two - Installed
            [+] three - Installed
            ... 2 more rows (full tree on completion)
            """);

        display.Finish();

        InlineSnapshot.Validate(console.GetScreenText(), """
            [+] one - Installed
            [+] two - Installed
            [+] three - Installed
            [+] four - Installed
            [+] five - Installed
            """);
    }

    [Fact]
    public void Diagnostic_writes_survive_subsequent_frames_and_completion()
    {
        using var console = new TerminalTestConsole(120, 10);
        var dep = new Dep { Name = "app" };
        var display = new ProgressDisplay(console, [dep]);
        display.RenderFrame();

        display.Out.Write("Catalog ");
        display.RenderFrame();
        Assert.Equal("Catalog", console.GetScreenText());
        display.Out.WriteLine("updated");
        display.Error.WriteLine("Warning\nmessage\u001b[2J");
        display.RenderFrame();

        InlineSnapshot.Validate(console.GetScreenText(), """
            Catalog updated
            Warning message [2J
            [.] app - Awaiting
            """);

        dep.SetStatus(DepStatus.Installed);
        display.Finish();
        InlineSnapshot.Validate(console.GetScreenText(), """
            Catalog updated
            Warning message [2J
            [+] app - Installed
            """);
    }
}
