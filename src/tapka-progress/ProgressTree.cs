using Tapka.Terminal;
using XenoAtom.Ansi;

namespace Tapka.Progress;

public enum ProgressState { Waiting, Running, Completed, Failed, Cancelled }

/// <summary>A thread-safe dependency tree. Shared dependencies have one state and multiple appearances.</summary>
public sealed class ProgressTree : IDisposable
{
    private sealed class Node(string name)
    {
        public string Name = name;
        public string Status = "Waiting";
        public ProgressState State;
        public double? Fraction;
        public readonly List<string> Children = [];
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Node> nodes = new(StringComparer.Ordinal);
    private readonly List<string> roots = [];
    private readonly ITerminal terminal;
    private readonly AnsiWriter ansi;
    private readonly CancellationToken cancellationToken;
    private readonly Timer? timer;
    private int drawnLines;
    private int frame;
    private int pauses;
    private bool disposed;

    public ProgressTree(ITerminal terminal, CancellationToken cancellationToken = default)
        : this(terminal, autoRender: true, cancellationToken) { }

    // Tests drive frames explicitly; the public constructor owns automatic rendering.
    internal ProgressTree(ITerminal terminal, bool autoRender, CancellationToken cancellationToken = default)
    {
        this.terminal = terminal;
        ansi = new AnsiWriter(terminal.Out, AnsiCapabilities.Default);
        this.cancellationToken = cancellationToken;
        if (autoRender && terminal.IsInteractive)
            timer = new Timer(_ => RenderFrame(), null, 0, 100);
    }

    public void Add(string id, string? parentId = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (parentId is not null && !nodes.ContainsKey(parentId))
                throw new ArgumentException("The parent must be added first.", nameof(parentId));
            nodes.TryAdd(id, new Node(Clean(id)));
            var siblings = parentId is null ? roots : nodes[parentId].Children;
            if (!siblings.Contains(id)) siblings.Add(id);
        }
    }

    /// <summary>Fraction is the progress of the named stage; null denotes indeterminate work.</summary>
    public void Report(string id, string status, double? fraction = null,
        ProgressState state = ProgressState.Running, string? name = null)
    {
        if (fraction is { } value && (!double.IsFinite(value) || value < 0 || value > 1))
            throw new ArgumentOutOfRangeException(nameof(fraction));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var node = nodes[id];
            node.Status = Clean(status);
            node.State = state;
            node.Fraction = state == ProgressState.Completed ? 1 : fraction;
            if (name is not null) node.Name = Clean(name);
        }
    }

    /// <summary>Stops live rendering while another component owns console output.</summary>
    public IDisposable Pause()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pauses++;
            Clear();
            return new PauseScope(this);
        }
    }

    private sealed class PauseScope(ProgressTree owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            lock (owner.gate)
            {
                if (disposed) return;
                disposed = true;
                owner.pauses--;
            }
        }
    }

    public void WriteLine(string message) => WriteLine(terminal.Out, message);

    public void WriteErrorLine(string message) => WriteLine(terminal.Error, message);

    private void WriteLine(TextWriter writer, string message)
    {
        lock (gate)
        {
            Clear();
            writer.WriteLine(Clean(message));
            writer.Flush();
        }
    }

    /// <summary>Returns a plain text snapshot without changing terminal state.</summary>
    public string Snapshot()
    {
        lock (gate) return string.Join(Environment.NewLine, Lines());
    }

    private List<string> Lines()
    {
        var lines = new List<string>();
        // Expand a shared dependency only once, keeping a reference at every other parent.
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in roots) Visit(id, "", "");
        return lines;

        void Visit(string id, string indent, string branch)
        {
            var node = nodes[id];
            var repeated = !expanded.Add(id);
            var icon = node.State switch
            {
                ProgressState.Completed => "+",
                ProgressState.Failed => "!",
                ProgressState.Cancelled => "x",
                ProgressState.Running => "|/-\\"[frame % 4].ToString(),
                _ => "."
            };
            var filled = (int)((node.Fraction ?? 0) * 16);
            var bar = node.Fraction is { } fraction
                ? $"[{new string('#', filled)}{new string('-', 16 - filled)}] {fraction * 100:0}%"
                : node.State == ProgressState.Running ? "[" + new string('-', frame % 16) + ">" + new string('-', 15 - frame % 16) + "]" : "[----------------]";
            lines.Add($"{indent}{branch}[{icon}] {node.Name} - {node.Status} - {bar}{(repeated ? " (shared)" : "")}");
            if (repeated) return;
            var childIndent = indent + (branch.Length == 0 ? "" : branch == "`- " ? "   " : "|  ");
            for (var i = 0; i < node.Children.Count; i++)
                Visit(node.Children[i], childIndent, i == node.Children.Count - 1 ? "`- " : "|- ");
        }
    }

    internal void RenderFrame()
    {
        lock (gate)
        {
            if (disposed || pauses > 0 || !terminal.IsInteractive) return;
            try
            {
                Clear();
                var width = Math.Max(1, terminal.Width - 1);
                var height = Math.Max(1, terminal.Height - 2);
                var lines = Lines();
                var visible = lines.Take(height).ToList();
                if (lines.Count > height) visible[^1] = $"... {lines.Count - height + 1} more rows (full tree on completion)";
                foreach (var line in visible)
                    terminal.Out.WriteLine(line.Length <= width ? line : line[..width]);
                drawnLines = visible.Count;
                terminal.Out.Flush();
                frame = (frame + 1) % 16;
            }
            catch (IOException) { /* A detached console must not fail installation. */ }
        }
    }

    private void Clear()
    {
        if (drawnLines == 0) return;
        ansi.CursorUp(drawnLines).CursorHorizontalAbsolute(1).EraseInDisplay(0);
        drawnLines = 0;
        terminal.Out.Flush();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer?.Dispose();
            foreach (var node in nodes.Values.Where(n => n.State is ProgressState.Waiting or ProgressState.Running))
            {
                node.State = cancellationToken.IsCancellationRequested ? ProgressState.Cancelled : ProgressState.Failed;
                node.Status = (cancellationToken.IsCancellationRequested ? "Cancelled" : "Aborted") + $" ({node.Status})";
                node.Fraction = null;
            }
            Clear();
            foreach (var line in Lines()) terminal.Out.WriteLine(line);
            terminal.Out.Flush();
        }
    }

    private static string Clean(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
