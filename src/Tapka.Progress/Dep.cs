using System.Collections.Immutable;
using Tapka.Terminal;

namespace Tapka.Progress;

public enum DepStatus
{
    Awaiting,
    Downloading,
    Downloaded,
    Installing,
    Installed
}

public sealed class Dep
{
    private readonly object gate = new();
    private Snapshot state = new("", [], DepStatus.Awaiting, null, null);

    internal readonly record struct Snapshot(
        string Name, ImmutableArray<Dep> Deps, DepStatus Status, double? Fraction, string? Detail);

    /// <summary>Raised when this dependency or one of its descendants changes.</summary>
    public event EventHandler? Update;

    public string Name
    {
        get => Capture().Name;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (gate)
            {
                if (state.Name == value) return;
                state = state with { Name = value };
            }
            NotifyUpdate();
        }
    }

    public IReadOnlyList<Dep> Deps => Capture().Deps;
    public DepStatus Status => Capture().Status;

    /// <summary>Adds a dependency to an acyclic graph. The caller validates the graph.</summary>
    public void Add(Dep dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        lock (gate)
        {
            if (state.Deps.Contains(dependency)) return;
            var deps = state.Deps.Add(dependency);
            dependency.Update += OnChildUpdate;
            state = state with { Deps = deps };
        }
        NotifyUpdate();
    }

    /// <summary>A null fraction animates the bar during Downloading and Installing.</summary>
    public void SetStatus(DepStatus status, double? fraction = null, string? detail = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (fraction is { } value)
        {
            if (!double.IsFinite(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException(nameof(fraction));
            if (!ShowsProgress(status))
                throw new ArgumentException("Only Downloading and Installing can have a progress fraction.", nameof(fraction));
        }

        lock (gate)
        {
            if (state.Status == status && state.Fraction == fraction && state.Detail == detail) return;
            state = state with { Status = status, Fraction = fraction, Detail = detail };
        }
        NotifyUpdate();
    }

    private void NotifyUpdate() => Update?.Invoke(this, EventArgs.Empty);

    private void OnChildUpdate(object? sender, EventArgs args) => NotifyUpdate();

    /// <summary>Displays the roots while the operation runs and routes its diagnostics above the live tree.</summary>
    public static async Task RunAsync(ITerminal terminal, IReadOnlyList<Dep> roots,
        Func<ITerminal, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(operation);

        var display = new ProgressDisplay(terminal, roots);
        string? interruption = null;
        try
        {
            display.Start();
            cancellationToken.ThrowIfCancellationRequested();
            await operation(display).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            interruption = "Cancelled";
            throw;
        }
        catch
        {
            interruption = "Aborted";
            throw;
        }
        finally
        {
            display.Finish(interruption);
        }
    }

    internal Snapshot Capture()
    {
        lock (gate) return state;
    }

    internal static bool ShowsProgress(DepStatus status) => status is DepStatus.Downloading or DepStatus.Installing;
}
