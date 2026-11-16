using Meziantou.Framework.InlineSnapshotTesting;
using Tapka.Terminal;
using Tapka.Terminal.Testing;

namespace Tapka.Progress.Tests;

public sealed class ProgressTests
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(200);

    private sealed record PlainTerminal(TextWriter Out, TextWriter Error) : ITerminal
    {
        public int Width => 80;
        public int Height => 24;
        public bool IsInteractive => false;
    }

    private static ProgressDisplay StartDisplay(ITerminal terminal, out ManualTimeProvider clock, params Dep[] roots)
    {
        clock = new ManualTimeProvider();
        var display = new ProgressDisplay(terminal, roots, clock);
        display.Start();
        clock.Advance(TimeSpan.Zero);
        return display;
    }

    [Fact]
    public void Updates_bubble_from_new_descendants_and_skip_unchanged_state()
    {
        var root = new Dep();
        var branch = new Dep();
        var leaf = new Dep();
        root.Add(branch);
        var updates = new List<Dep>();
        foreach (var node in new[] { root, branch, leaf })
        {
            node.Update += (sender, _) =>
            {
                Assert.Same(node, sender);
                updates.Add(node);
            };
        }

        branch.Add(leaf);
        Assert.Equal(2, updates.Count);
        Assert.Contains(branch, updates);
        Assert.Contains(root, updates);
        updates.Clear();

        leaf.Name = "leaf";
        leaf.SetStatus(DepStatus.Downloading);
        leaf.SetStatus(DepStatus.Downloading, .5);
        leaf.SetStatus(DepStatus.Downloading, .5, "Downloading archive");
        branch.Add(leaf);
        leaf.Name = "leaf";
        leaf.SetStatus(DepStatus.Downloading, .5, "Downloading archive");

        Assert.Equal(12, updates.Count);
        Assert.All(new[] { leaf, branch, root }, node => Assert.Equal(4, updates.Count(dep => dep == node)));
    }

    [Fact]
    public void Reentrant_updates_reach_the_parent()
    {
        var root = new Dep();
        var child = new Dep();
        root.Add(child);
        var updates = 0;
        root.Update += (_, _) => updates++;
        child.Update += (_, _) =>
        {
            if (child.Status == DepStatus.Downloading) child.SetStatus(DepStatus.Installed);
        };

        child.SetStatus(DepStatus.Downloading);

        Assert.Equal(DepStatus.Installed, child.Status);
        Assert.Equal(2, updates);
    }

    [Fact]
    public void Updates_along_shared_paths_coalesce_into_one_frame_after_200_milliseconds()
    {
        using var console = new TerminalTestConsole(120, 10);
        var app = new Dep { Name = "app" };
        var left = new Dep { Name = "left" };
        var right = new Dep { Name = "right" };
        app.Add(left);
        app.Add(right);
        var display = StartDisplay(console, out var clock, app, app);
        var initialScreen = console.GetScreenText();
        Assert.Equal("[.] app - Awaiting\n|- [.] left - Awaiting\n`- [.] right - Awaiting", initialScreen);
        Assert.False(clock.HasPendingFrame);

        var child = new Dep { Name = "child" };
        left.Add(child);
        right.Add(child);
        var updates = 0;
        app.Update += (_, _) => updates++;
        child.SetStatus(DepStatus.Downloading, .25);
        Assert.Equal(2, updates);
        clock.Advance(FrameInterval - TimeSpan.FromMilliseconds(1));
        Assert.Equal(initialScreen, console.GetScreenText());

        child.SetStatus(DepStatus.Downloading, .75);
        Assert.Equal(4, updates);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("""
            [.] app - Awaiting
            |- [.] left - Awaiting
            |  `- [>] child - Downloading - [############----] 75%
            `- [.] right - Awaiting
               `- [>] child - Downloading - [############----] 75% (shared)
            """, console.GetScreenText());
        Assert.False(clock.HasPendingFrame);

        clock.Advance(TimeSpan.FromSeconds(1));
        child.SetStatus(DepStatus.Downloaded);
        clock.Advance(TimeSpan.Zero);
        Assert.Contains("child - Downloaded (shared)", console.GetScreenText());
        display.Finish();
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

        StartDisplay(console, out _, app);

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

        StartDisplay(console, out _, new Dep(), downloading, downloaded, installing, installed);

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
        dep.SetStatus(stage, .5, "A longer status detail");
        var display = StartDisplay(console, out var clock, dep);
        Assert.Contains("[########--------] 50%", console.GetScreenText());
        Assert.False(clock.HasPendingFrame);

        dep.SetStatus(stage);
        clock.Advance(FrameInterval);
        Assert.Equal($"[>] app - {stage} - [->--------------]", console.GetScreenText());
        clock.Advance(FrameInterval);
        Assert.Equal($"[>] app - {stage} - [-->-------------]", console.GetScreenText());
        Assert.True(clock.HasPendingFrame);

        dep.SetStatus(DepStatus.Installed);
        display.Finish();
        Assert.False(clock.HasPendingFrame);
        dep.SetStatus(DepStatus.Awaiting);
        clock.Advance(FrameInterval);
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
        var app = new Dep { Name = "app" };
        var dep = new Dep { Name = "dependency" };
        Parallel.For(0, 100, _ => app.Add(dep));
        Assert.Same(dep, Assert.Single(app.Deps));
        var updates = 0;
        app.Update += (_, _) => Interlocked.Increment(ref updates);

        Parallel.For(0, 100, i => dep.SetStatus(DepStatus.Downloading, i / 100d));
        dep.SetStatus(DepStatus.Downloaded);
        StartDisplay(console, out _, app);

        Assert.Equal(101, updates);
        Assert.Equal("[.] app - Awaiting\n`- [.] dependency - Downloaded", console.GetScreenText());
    }

    [Fact]
    public void Frames_reserve_a_column_and_do_not_wrap()
    {
        using var console = new TerminalTestConsole(20, 10);
        var dep = new Dep { Name = "app" };
        dep.SetStatus(DepStatus.Downloading, .5);
        StartDisplay(console, out _, dep);

        Assert.Equal("[>] app - Downloadi", console.GetScreenText());
    }

    [Fact]
    public void Frames_limit_height_but_completion_prints_the_full_tree()
    {
        using var console = new TerminalTestConsole(120, 6);
        var roots = new[] { "one", "two", "three", "four", "five" }.Select(name => new Dep { Name = name }).ToArray();
        foreach (var dep in roots) dep.SetStatus(DepStatus.Installed);
        var display = StartDisplay(console, out _, roots);

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
        var display = StartDisplay(console, out var clock, dep);

        display.Out.Write("Catalog ");
        dep.Name = "renamed";
        clock.Advance(FrameInterval / 2);
        Assert.Equal("Catalog", console.GetScreenText());
        display.Out.WriteLine("updated");
        display.Error.WriteLine("Warning\nmessage\u001b[2J");
        clock.Advance(TimeSpan.Zero);
        Assert.Equal("Catalog updated\nWarning message [2J", console.GetScreenText());
        clock.Advance(FrameInterval / 2);

        InlineSnapshot.Validate(console.GetScreenText(), """
            Catalog updated
            Warning message [2J
            [.] renamed - Awaiting
            """);

        dep.SetStatus(DepStatus.Installed);
        display.Finish();
        InlineSnapshot.Validate(console.GetScreenText(), """
            Catalog updated
            Warning message [2J
            [+] renamed - Installed
            """);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        private ManualTimer? timer;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public bool HasPendingFrame => timer?.DueAt is not null;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Null(timer);
            timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            timestamp += elapsed.Ticks;
            if (timer?.DueAt is { } due && due <= timestamp) timer.Fire();
        }

        private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public long? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetTimestamp() + dueTime.Ticks;
                return true;
            }

            public void Fire()
            {
                DueAt = null;
                callback(state);
            }

            public void Dispose()
            {
                disposed = true;
                DueAt = null;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
