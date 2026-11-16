using System.Runtime.CompilerServices;
using System.Text;
using LibGhostty;

namespace Tapka.Terminal.Testing;

/// <summary>Emulates a terminal for code that accepts text writers for its output.</summary>
public sealed class TerminalTestConsole : IDisposable
{
    private readonly object gate = new();
    private readonly GhosttyNativeLibrary ghostty;
    private IntPtr terminal;
    private IntPtr renderState;
    private IntPtr rowIterator;
    private IntPtr rowCells;
    private bool disposed;

    /// <param name="width">Screen width in columns.</param>
    /// <param name="height">Screen height in rows.</param>
    public TerminalTestConsole(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ushort.MaxValue);

        Width = width;
        Height = height;
        Out = new TerminalWriter(this);
        Error = new TerminalWriter(this);

        ghostty = LoadGhostty();
        try
        {
            var options = new GhosttyNativeLibrary.TerminalOptions
            {
                Columns = (ushort)width,
                Rows = (ushort)height,
                MaxScrollback = 0,
            };
            Check(ghostty.TerminalNew(options, out terminal));
            Check(ghostty.RenderStateNew(out renderState));
            Check(ghostty.RowIteratorNew(out rowIterator));
            Check(ghostty.RowCellsNew(out rowCells));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Width { get; }
    public int Height { get; }
    public TextWriter Out { get; }
    public TextWriter Error { get; }

    /// <summary>
    /// Returns visible screen text with LF between rows, omitting trailing spaces and bottom blank rows.
    /// </summary>
    public string GetScreenText()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Check(ghostty.RenderStateUpdate(renderState, terminal));
            Check(ghostty.RenderStateGetU16(renderState, GhosttyNativeLibrary.RenderStateDataCols, out var columns));
            Check(ghostty.RenderStateGetHandle(renderState, GhosttyNativeLibrary.RenderStateDataRowIterator, ref rowIterator));

            var screen = new StringBuilder();
            Span<uint> codePoints = stackalloc uint[16];
            var lastNonEmptyRowEnd = 0;
            while (ghostty.RowIteratorNext(rowIterator))
            {
                Check(ghostty.RowGetCells(rowIterator, ref rowCells));
                var rowStart = screen.Length;
                for (ushort column = 0; column < columns; column++)
                {
                    Check(ghostty.RowCellsSelect(rowCells, column));
                    AppendCell(screen, codePoints);
                }

                while (screen.Length > rowStart && screen[^1] == ' ')
                    screen.Length--;

                if (screen.Length > rowStart)
                    lastNonEmptyRowEnd = screen.Length;

                screen.Append('\n');
            }

            screen.Length = lastNonEmptyRowEnd;
            return screen.ToString();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;

            disposed = true;
            if (rowCells != IntPtr.Zero) ghostty.RowCellsFree(rowCells);
            if (rowIterator != IntPtr.Zero) ghostty.RowIteratorFree(rowIterator);
            if (renderState != IntPtr.Zero) ghostty.RenderStateFree(renderState);
            if (terminal != IntPtr.Zero) ghostty.TerminalFree(terminal);
            ghostty.Dispose();
        }
    }

    private static GhosttyNativeLibrary LoadGhostty()
    {
        // With a portable build, NuGet keeps native libraries in the RID subdirectory.
        // LibGhostty.Net 1.0.2 only checks the flat output directory and its own staging locations.
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes",
            GhosttyRuntimeAssets.RuntimeIdentifier, "native", GhosttyRuntimeAssets.GhosttyLibraryFileName);
        return File.Exists(path)
            ? new GhosttyNativeLibrary(path)
            : GhosttyNativeLibrary.LoadFromPackage();
    }

    private unsafe void AppendCell(StringBuilder screen, Span<uint> codePoints)
    {
        Check(ghostty.RowCellsGetU32(rowCells, GhosttyNativeLibrary.RenderStateRowCellsDataGraphemesLength, out var length));
        if (length == 0)
        {
            // A wide character already accounts for the following cell.
            const int wideSpacerTail = 2; // GHOSTTY_CELL_WIDE_SPACER_TAIL
            Check(ghostty.RowCellsGetRaw(rowCells, out var cell));
            Check(ghostty.CellGetWide(cell, out var wide));
            if (wide != wideSpacerTail)
                screen.Append(' ');
            return;
        }

        var count = checked((int)length);
        var grapheme = count <= codePoints.Length ? codePoints[..count] : new uint[count];
        fixed (uint* buffer = grapheme)
            Check(ghostty.RowCellsGetBuffer(rowCells, GhosttyNativeLibrary.RenderStateRowCellsDataGraphemesBuffer, (IntPtr)buffer));

        Span<char> utf16 = stackalloc char[2];
        foreach (var codePoint in grapheme)
        {
            var rune = new Rune((int)codePoint);
            screen.Append(utf16[..rune.EncodeToUtf16(utf16)]);
        }
    }

    private static void Check(int result, [CallerArgumentExpression(nameof(result))] string? operation = null)
    {
        if (result != GhosttyNativeLibrary.Success)
            throw new InvalidOperationException($"Ghostty failed with status {result}: {operation}.");
    }

    private sealed class TerminalWriter(TerminalTestConsole console) : TextWriter
    {
        private readonly Encoder encoder = Encoding.UTF8.GetEncoder();
        private bool closed;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Write(new ReadOnlySpan<char>(in value));
        public override void Write(string? value) => Write(value.AsSpan());

        public override void Write(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer.AsSpan(index, count));
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            lock (console.gate)
            {
                ThrowIfDisposed();
                Feed(buffer);
            }
        }

        public override void WriteLine(char value) => WriteLine(new ReadOnlySpan<char>(in value));
        public override void WriteLine(string? value) => WriteLine(value.AsSpan());

        public override void WriteLine(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            WriteLine(buffer.AsSpan(index, count));
        }

        public override void WriteLine(ReadOnlySpan<char> buffer)
        {
            lock (console.gate)
            {
                ThrowIfDisposed();
                Feed(buffer);
                Feed(CoreNewLine);
            }
        }

        public override void Flush()
        {
            lock (console.gate)
            {
                ThrowIfDisposed();
                Feed([], flush: true);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (console.gate)
                {
                    if (!closed && !console.disposed)
                        Feed([], flush: true);
                    closed = true;
                }
            }
            base.Dispose(disposing);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(console.disposed, console);
            ObjectDisposedException.ThrowIf(closed, this);
        }

        private unsafe void Feed(ReadOnlySpan<char> text, bool flush = false)
        {
            Span<byte> utf8 = stackalloc byte[1024];
            bool completed;
            do
            {
                encoder.Convert(text, utf8, flush, out var charsUsed, out var bytesUsed, out completed);
                if (bytesUsed > 0)
                {
                    fixed (byte* data = utf8)
                        console.ghostty.TerminalWrite(console.terminal, (IntPtr)data, (nuint)bytesUsed);
                }
                text = text[charsUsed..];
            } while (!completed);
        }
    }
}
