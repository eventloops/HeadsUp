using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>
/// A bounded, in-memory record of the alerts that were actually surfaced (newest
/// first), so the dashboard can answer "what did I miss while I was away". In-memory
/// only — it resets when the app restarts.
/// </summary>
public interface IAlertHistory
{
    /// <summary>Most-recent-first snapshot of the retained alerts.</summary>
    IReadOnlyList<Alert> Recent { get; }

    /// <summary>Raised whenever the list changes (may fire on a background thread).</summary>
    event EventHandler? Changed;

    /// <summary>Records a surfaced alert, evicting the oldest once past capacity.</summary>
    void Record(Alert alert);

    /// <summary>Drops all retained alerts.</summary>
    void Clear();
}

/// <inheritdoc cref="IAlertHistory"/>
public sealed class AlertHistory : IAlertHistory
{
    /// <summary>How many alerts to keep. Old entries beyond this are evicted.</summary>
    public const int Capacity = 100;

    private readonly LinkedList<Alert> _items = new(); // newest at the front
    private readonly object _gate = new();

    public event EventHandler? Changed;

    public IReadOnlyList<Alert> Recent
    {
        get
        {
            lock (_gate)
                return _items.ToArray();
        }
    }

    public void Record(Alert alert)
    {
        lock (_gate)
        {
            _items.AddFirst(alert);
            while (_items.Count > Capacity)
                _items.RemoveLast();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_items.Count == 0)
                return; // nothing to do; don't raise a spurious change
            _items.Clear();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
