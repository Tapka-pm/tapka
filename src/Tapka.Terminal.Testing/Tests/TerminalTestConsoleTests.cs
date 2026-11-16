using Meziantou.Framework.InlineSnapshotTesting;

namespace Tapka.Terminal.Testing.Tests;

public sealed class TerminalTestConsoleTests
{
    [Theory]
    [InlineData(0, 2, "width")]
    [InlineData(-1, 2, "width")]
    [InlineData(65536, 2, "width")]
    [InlineData(2, 0, "height")]
    [InlineData(2, -1, "height")]
    [InlineData(2, 65536, "height")]
    public void Rejects_invalid_dimensions(int width, int height, string parameter)
    {
        Assert.Throws<ArgumentOutOfRangeException>(parameter, () => new TerminalTestConsole(width, height));
    }

    [Theory]
    [InlineData(1, 1, "e")]
    [InlineData(2, 2, "cd\ne")]
    [InlineData(2, 3, "ab\ncd\ne")]
    [InlineData(4, 2, "abcd\ne")]
    [InlineData(5, 2, "abcde")]
    public void Wraps_and_scrolls_at_the_requested_dimensions(int width, int height, string expected)
    {
        using var console = new TerminalTestConsole(width, height);

        console.Out.Write("abcde");

        Assert.Equal(width, console.Width);
        Assert.Equal(height, console.Height);
        Assert.Equal(expected, console.GetScreenText());
    }

    [Fact]
    public void Progress_replaces_previous_line()
    {
        using var console = new TerminalTestConsole(width: 40, height: 10);

        console.Out.Write("Downloading 10%");
        console.Out.Write("\r\u001b[2KDone");

        InlineSnapshot.Validate(console.GetScreenText(), """
            Done
            """);
    }

    [Fact]
    public void Applies_cursor_movement_and_a_sequence_split_across_streams()
    {
        using var console = new TerminalTestConsole(width: 20, height: 4);
        console.Out.Write("Status\r\nobsolete");
        console.Out.Write("\u001b[");
        console.Error.Write("2;1H");
        console.Out.Write("\u001b[");
        console.Out.Write("2K");
        console.Error.Write("Ready");

        Assert.Equal("Status\nReady", console.GetScreenText());
    }

    [Fact]
    public void Preserves_combining_marks_wide_cells_and_split_surrogates()
    {
        using var console = new TerminalTestConsole(width: 20, height: 3);
        console.Out.Write("界e");
        console.Out.Write('\u0301');
        console.Out.Write('\ud83d');
        Assert.Equal("界e\u0301", console.GetScreenText());

        console.Out.Write('\ude42');
        console.Out.Write("\u001b[8G!");

        Assert.Equal("界e\u0301🙂  !", console.GetScreenText());
    }

    [Fact]
    public void Wide_character_wraps_when_one_column_remains()
    {
        using var console = new TerminalTestConsole(width: 3, height: 2);
        console.Out.Write("ab界c");

        Assert.Equal("ab\n界c", console.GetScreenText());
    }

    [Fact]
    public async Task Writers_apply_their_own_newlines_and_support_async_output()
    {
        using var console = new TerminalTestConsole(width: 24, height: 5);
        Assert.Equal(Environment.NewLine, console.Out.NewLine);
        Assert.Equal(Environment.NewLine, console.Error.NewLine);

        console.Out.NewLine = "\r";
        await console.Out.WriteLineAsync("loading");
        await console.Error.WriteAsync("Done".AsMemory());
        console.Error.Write("\u001b[K");
        await console.Error.WriteLineAsync();
        await console.Out.WriteLineAsync("output".AsMemory());
        await console.Error.WriteAsync('!');

        Assert.Equal("Done\n!utput", console.GetScreenText());
    }

    [Fact]
    public async Task A_cancelled_async_write_leaves_the_screen_unchanged()
    {
        using var console = new TerminalTestConsole(width: 20, height: 3);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => console.Out.WriteAsync("not written".AsMemory(), new CancellationToken(canceled: true)));

        Assert.Empty(console.GetScreenText());
    }

    [Fact]
    public void Preserves_leading_spaces_and_blank_rows_but_trims_right_and_bottom_padding()
    {
        using var console = new TerminalTestConsole(width: 24, height: 8);
        Assert.Empty(console.GetScreenText());
        console.Out.Write("\r\n  left  \r\n   \r\n right\u00a0  \r\n  ");

        var snapshot = console.GetScreenText();

        Assert.Equal("\n  left\n\n right\u00a0", snapshot);
        Assert.Equal(snapshot, console.GetScreenText());
        console.Out.Write("\r\nnext");
        Assert.Equal(snapshot + "\n\nnext", console.GetScreenText());
    }

    [Fact]
    public async Task Parallel_consoles_have_independent_screens_and_leave_standard_streams_intact()
    {
        var standardOut = Console.Out;
        var standardError = Console.Error;
        using var first = new TerminalTestConsole(width: 12, height: 4);
        using var second = new TerminalTestConsole(width: 12, height: 4);

        await Task.WhenAll(
            Task.Run(() =>
            {
                first.Out.Write("first");
                first.Error.Write("!");
            }),
            Task.Run(() =>
            {
                second.Error.Write("second");
                second.Out.Write("?");
            }));

        Assert.Equal("first!", first.GetScreenText());
        Assert.Equal("second?", second.GetScreenText());
        Assert.Same(standardOut, Console.Out);
        Assert.Same(standardError, Console.Error);
    }

    [Fact]
    public async Task Disposed_console_rejects_screen_reads_and_retained_writers()
    {
        var console = new TerminalTestConsole(width: 20, height: 3);
        var output = console.Out;
        var error = console.Error;
        console.Dispose();
        console.Dispose();

        Assert.Throws<ObjectDisposedException>(() => console.GetScreenText());
        Assert.Throws<ObjectDisposedException>(() => output.Write('x'));
        Assert.Throws<ObjectDisposedException>(() => output.Flush());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => error.WriteLineAsync("too late"));

        output.Dispose();
        await error.DisposeAsync();
    }

    [Fact]
    public void Disposing_one_writer_keeps_the_other_writer_and_screen_available()
    {
        using var console = new TerminalTestConsole(width: 20, height: 3);
        console.Out.Write("output");
        console.Out.Dispose();
        console.Error.Write(" error");

        Assert.Throws<ObjectDisposedException>(() => console.Out.Write("too late"));
        Assert.Equal("output error", console.GetScreenText());
    }
}
