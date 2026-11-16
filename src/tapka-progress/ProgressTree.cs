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
    private readonly TextWriter output;
    private readonly bool interactive;
    private readonly Func<(int Width, int Height)> getSize;
    private readonly CancellationToken cancellationToken;
    private readonly Timer? timer;
    private int drawnLines;
    private int frame;
    private int pauses;
    private bool disposed;

    /// <param name="output">An explicit writer uses plain text output, suitable for logs and tests.</param>
    public ProgressTree(TextWriter? output = null, CancellationToken cancellationToken = default)
        : this(output ?? Console.Out,
            output is null && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("TERM") != "dumb",
            () => (Console.WindowWidth, Console.WindowHeight), cancellationToken)
    {
        if (interactive) timer = new Timer(_ => RenderFrame(), null, 0, 100);
    }

    // Tests drive frames explicitly; the public constructor owns automatic rendering.
    internal ProgressTree(TextWriter output, bool interactive, Func<(int Width, int Height)> getSize,
        CancellationToken cancellationToken = default)
    {
        this.output = output;
        this.interactive = interactive;
        this.getSize = getSize;
        this.cancellationToken = cancellationToken;
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

    public void WriteLine(string message, TextWriter? writer = null)
    {
        lock (gate)
        {
            Clear();
            (writer ?? output).WriteLine(Clean(message));
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
            if (!interactive || disposed || pauses > 0) return;
            try
            {
                Clear();
                var size = getSize();
                var width = Math.Max(1, size.Width - 1);
                var height = Math.Max(1, size.Height - 2);
                var lines = Lines();
                var visible = lines.Take(height).ToList();
                if (lines.Count > height) visible[^1] = $"... {lines.Count - height + 1} more rows (full tree on completion)";
                foreach (var line in visible)
                    output.WriteLine(line.Length <= width ? line : line[..width]);
                drawnLines = visible.Count;
                output.Flush();
                frame = (frame + 1) % 16;
            }
            catch (IOException) { /* A detached console must not fail installation. */ }
        }
    }

    private void Clear()
    {
        if (drawnLines == 0) return;
        output.Write($"\u001b[{drawnLines}A\r\u001b[J");
        drawnLines = 0;
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
            foreach (var line in Lines()) output.WriteLine(line);
            output.Flush();
        }
    }

    private static string Clean(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
