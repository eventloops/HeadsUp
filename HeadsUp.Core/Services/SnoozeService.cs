using System.Timers;
using Timer = System.Timers.Timer;

namespace HeadsUp.Services;

/// <summary>Decides whether alerts should be silenced right now.</summary>
public interface IAlertGate
{
    /// <summary>True when alerts should be silenced; <paramref name="reason"/> explains why.</summary>
    bool IsMuted(out string reason);

    /// <summary>Fires when the mute state may have changed (snooze set/cleared/expired, or a DND update).</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Silences alerts while the user has manually snoozed, or while any connected source's
/// Do Not Disturb is active. History still records everything (see
/// <see cref="HuddleNotificationCoordinator"/>); snooze only suppresses the popup and sound.
/// </summary>
public sealed class SnoozeService : IAlertGate, IDisposable
{
    private readonly object _gate = new();
    private readonly Timer _expiry = new() { AutoReset = false };
    private DateTimeOffset? _manualUntil;   // manual snooze end
    // Per-source Do-Not-Disturb: source id -> (display name, end time). A source with no
    // active DND is removed. Keyed so multiple connectors each contribute independently.
    private readonly Dictionary<string, (string Name, DateTimeOffset Until)> _sourceDnd = new();

    public event EventHandler? Changed;

    public SnoozeService() => _expiry.Elapsed += OnExpiry;

    /// <summary>The manual-snooze end time while snoozed; null otherwise.</summary>
    public DateTimeOffset? SnoozedUntil
    {
        get { lock (_gate) return Active(_manualUntil) ? _manualUntil : null; }
    }

    /// <summary>True while any connected source's Do Not Disturb is in effect.</summary>
    public bool AnySourceDndActive
    {
        get { lock (_gate) return ActiveDndSource() is not null; }
    }

    public bool IsMuted(out string reason)
    {
        lock (_gate)
        {
            if (Active(_manualUntil))
            {
                reason = $"Snoozed until {_manualUntil!.Value.ToLocalTime():h:mm tt}";
                return true;
            }
            if (ActiveDndSource() is { } dnd)
            {
                reason = $"Do Not Disturb is on in {dnd.Name}";
                return true;
            }
            reason = string.Empty;
            return false;
        }
    }

    /// <summary>Snoozes alerts for a relative duration.</summary>
    public void SnoozeFor(TimeSpan duration) => SnoozeUntil(DateTimeOffset.Now + duration);

    /// <summary>Snoozes alerts until an absolute time.</summary>
    public void SnoozeUntil(DateTimeOffset until)
    {
        lock (_gate)
        {
            _manualUntil = until;
            ScheduleExpiry(until);
        }
        RaiseChanged();
    }

    /// <summary>Ends a manual snooze immediately.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _manualUntil = null;
            _expiry.Stop();
        }
        RaiseChanged();
    }

    /// <summary>
    /// Records a source's current Do Not Disturb end time (null clears it). Fed by each
    /// connector's DND poller; keyed by <paramref name="sourceId"/> so sources are independent.
    /// </summary>
    public void SetSourceDnd(string sourceId, string sourceName, DateTimeOffset? until)
    {
        lock (_gate)
        {
            var next = Active(until) ? until : (DateTimeOffset?)null;
            var had = _sourceDnd.TryGetValue(sourceId, out var existing);
            if (next is null)
            {
                if (!had) return;       // already clear — don't churn the UI
                _sourceDnd.Remove(sourceId);
            }
            else
            {
                if (had && existing.Until == next.Value) return; // unchanged
                _sourceDnd[sourceId] = (sourceName, next.Value);
            }
        }
        RaiseChanged();
    }

    private void OnExpiry(object? sender, ElapsedEventArgs e) => RaiseChanged();

    private void ScheduleExpiry(DateTimeOffset until)
    {
        _expiry.Stop();
        var ms = (until - DateTimeOffset.Now).TotalMilliseconds;
        if (ms > 0 && ms <= int.MaxValue)
        {
            _expiry.Interval = ms;
            _expiry.Start();
        }
    }

    private static bool Active(DateTimeOffset? until) => until is { } u && u > DateTimeOffset.Now;

    /// <summary>The first source whose DND is active right now, or null. Call under <see cref="_gate"/>.</summary>
    private (string Name, DateTimeOffset Until)? ActiveDndSource()
    {
        foreach (var entry in _sourceDnd.Values)
            if (Active(entry.Until))
                return entry;
        return null;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _expiry.Elapsed -= OnExpiry;
        _expiry.Dispose();
    }
}
