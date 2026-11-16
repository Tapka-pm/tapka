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

    public string Name
    {
        get => Capture().Name;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (gate) state = state with { Name = value };
        }
    }

    public IReadOnlyList<Dep> Deps => Capture().Deps;
    public DepStatus Status => Capture().Status;

    public void Add(Dep dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        lock (gate)
        {
            if (!state.Deps.Contains(dependency))
                state = state with { Deps = state.Deps.Add(dependency) };
        }
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

        lock (gate) state = state with { Status = status, Fraction = fraction, Detail = detail };
    }

    /// <summary>Displays the roots while the operation runs and routes its diagnostics above the live tree.</summary>
    public static async Task RunAsync(ITerminal terminal, IReadOnlyList<Dep> roots,
        Func<ITerminal, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(operation);

        var display = new ProgressDisplay(terminal, roots);
        using var timer = display.Start();
        string? interruption = null;
        try
        {
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
