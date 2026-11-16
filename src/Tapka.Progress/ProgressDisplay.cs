using System.Text;
using Tapka.Terminal;
using XenoAtom.Ansi;

namespace Tapka.Progress;

internal sealed class ProgressDisplay : ITerminal
{
    private const int BarWidth = 16;

    private readonly object gate = new();
    private readonly ITerminal terminal;
    private readonly Dep[] roots;
    private readonly AnsiWriter ansi;
    private int drawnLines;
    private int frame;
    private bool finished;
    private TextWriter? partialLine;

    public ProgressDisplay(ITerminal terminal, IReadOnlyList<Dep> roots)
    {
        this.terminal = terminal;
        this.roots = roots.Distinct().ToArray();
        foreach (var root in this.roots) ArgumentNullException.ThrowIfNull(root);

        ansi = new AnsiWriter(terminal.Out, AnsiCapabilities.Default);
        Out = new DiagnosticWriter(this, terminal.Out);
        Error = new DiagnosticWriter(this, terminal.Error);
    }

    public TextWriter Out { get; }
    public TextWriter Error { get; }
    public int Width => terminal.Width;
    public int Height => terminal.Height;
    public bool IsInteractive => terminal.IsInteractive;

    internal Timer? Start() => IsInteractive && roots.Length > 0
        ? new Timer(_ => RenderFrame(), null, 0, 100)
        : null;

    private List<string> Lines(bool live, string? interruption = null)
    {
        var lines = new List<string>();
        var snapshots = new Dictionary<Dep, Dep.Snapshot>();
        foreach (var root in roots) Visit(root, "", "");
        return lines;

        void Visit(Dep dep, string indent, string branch)
        {
            var repeated = snapshots.TryGetValue(dep, out var node);
            if (!repeated)
            {
                node = dep.Capture();
                snapshots.Add(dep, node);
            }

            var active = Dep.ShowsProgress(node.Status);
            var status = node.Status.ToString();
            if (!string.IsNullOrEmpty(node.Detail)) status += $" ({Clean(node.Detail)})";

            string icon;
            if (node.Status == DepStatus.Installed)
            {
                icon = "+";
            }
            else if (interruption is not null)
            {
                icon = interruption == "Cancelled" ? "x" : "!";
                status = $"{interruption} ({status})";
            }
            else
            {
                icon = active ? ">" : ".";
            }

            var name = string.IsNullOrEmpty(node.Name) ? "(unnamed)" : Clean(node.Name);
            var bar = live && active ? $" - {Bar(node.Fraction)}" : "";
            lines.Add($"{indent}{branch}[{icon}] {name} - {status}{bar}{(repeated ? " (shared)" : "")}");
            if (repeated) return;

            var childIndent = indent + (branch.Length == 0 ? "" : branch == "`- " ? "   " : "|  ");
            for (var i = 0; i < node.Deps.Length; i++)
                Visit(node.Deps[i], childIndent, i == node.Deps.Length - 1 ? "`- " : "|- ");
        }
    }

    private string Bar(double? fraction)
    {
        if (fraction is not { } value)
            return $"[{new string('-', frame)}>{new string('-', BarWidth - 1 - frame)}]";

        var filled = (int)(value * BarWidth);
        var percent = (int)(value * 100);
        return FormattableString.Invariant($"[{new string('#', filled)}{new string('-', BarWidth - filled)}] {percent}%");
    }

    internal void RenderFrame()
    {
        lock (gate)
        {
            if (finished || !IsInteractive || partialLine is not null) return;
            try
            {
                Clear();
                var width = Math.Max(1, Width - 1);
                var height = Math.Max(1, Height - 2);
                var lines = Lines(live: true);
                var visible = Math.Min(lines.Count, height);
                if (lines.Count > height)
                    lines[visible - 1] = $"... {lines.Count - height + 1} more rows (full tree on completion)";

                for (var i = 0; i < visible; i++)
                {
                    var line = lines[i];
                    terminal.Out.WriteLine(line.Length <= width ? line : line[..width]);
                    drawnLines++;
                }
                terminal.Out.Flush();
                frame = (frame + 1) % BarWidth;
            }
            catch (IOException) { /* A detached console must not fail installation. */ }
        }
    }

    internal void Finish(string? interruption = null)
    {
        lock (gate)
        {
            if (finished) return;
            finished = true;
            try
            {
                Clear();
                if (partialLine is not null && roots.Length > 0)
                {
                    partialLine.WriteLine();
                    partialLine.Flush();
                    partialLine = null;
                }

                foreach (var line in Lines(live: false, interruption)) terminal.Out.WriteLine(line);
                terminal.Out.Flush();
            }
            catch (IOException) { /* A detached console must not mask the operation result. */ }
        }
    }

    private void Clear()
    {
        if (drawnLines == 0) return;
        ansi.CursorUp(drawnLines).CursorHorizontalAbsolute(1).EraseInDisplay(0);
        drawnLines = 0;
        terminal.Out.Flush();
    }

    private void Write(TextWriter writer, ReadOnlySpan<char> value, bool endLine)
    {
        if (!endLine && value.IsEmpty) return;
        lock (gate)
        {
            Clear();
            var sharesScreen = IsInteractive || ReferenceEquals(writer, terminal.Out);
            if (sharesScreen && partialLine is not null && !ReferenceEquals(partialLine, writer))
            {
                partialLine.WriteLine();
                partialLine.Flush();
            }

            if (endLine) writer.WriteLine(Clean(value.ToString()));
            else writer.Write(Clean(value.ToString(), allowLineBreaks: true));
            writer.Flush();

            if (sharesScreen)
                partialLine = endLine || value[^1] == '\n' ? null : writer;
        }
    }

    private sealed class DiagnosticWriter(ProgressDisplay owner, TextWriter writer) : TextWriter
    {
        public override Encoding Encoding => writer.Encoding;
        public override void Write(char value) => Write(new ReadOnlySpan<char>(in value));
        public override void Write(string? value) => Write(value.AsSpan());
        public override void Write(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer.AsSpan(index, count));
        }

        public override void Write(ReadOnlySpan<char> buffer) => owner.Write(writer, buffer, endLine: false);
        public override void WriteLine() => WriteLine(ReadOnlySpan<char>.Empty);
        public override void WriteLine(char value) => WriteLine(new ReadOnlySpan<char>(in value));
        public override void WriteLine(string? value) => WriteLine(value.AsSpan());
        public override void WriteLine(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            WriteLine(buffer.AsSpan(index, count));
        }

        public override void WriteLine(ReadOnlySpan<char> buffer) => owner.Write(writer, buffer, endLine: true);

        public override void Flush()
        {
            lock (owner.gate) writer.Flush();
        }
    }

    private static string Clean(string text, bool allowLineBreaks = false) => new(text.Select(c =>
        char.IsControl(c) && !(allowLineBreaks && c is '\r' or '\n') ? ' ' : c).ToArray());
}
