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
/// Silences alerts while the user has manually snoozed, or while their Slack Do Not Disturb
/// is active. History still records everything (see <see cref="HuddleNotificationCoordinator"/>);
/// snooze only suppresses the popup and sound.
/// </summary>
public sealed class SnoozeService : IAlertGate, IDisposable
{
    private readonly object _gate = new();
    private readonly Timer _expiry = new() { AutoReset = false };
    private DateTimeOffset? _manualUntil;   // manual snooze end
    private DateTimeOffset? _slackDndUntil; // end of the user's active Slack DND, if any

    public event EventHandler? Changed;

    public SnoozeService() => _expiry.Elapsed += OnExpiry;

    /// <summary>The manual-snooze end time while snoozed; null otherwise.</summary>
    public DateTimeOffset? SnoozedUntil
    {
        get { lock (_gate) return Active(_manualUntil) ? _manualUntil : null; }
    }

    /// <summary>True while the user's Slack Do Not Disturb is in effect.</summary>
    public bool SlackDndActive
    {
        get { lock (_gate) return Active(_slackDndUntil); }
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
            if (Active(_slackDndUntil))
            {
                reason = "Do Not Disturb is on in Slack";
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

    /// <summary>Records the user's current Slack DND end time (null clears it). Fed by the DND poller.</summary>
    public void SetSlackDnd(DateTimeOffset? until)
    {
        lock (_gate)
        {
            var next = Active(until) ? until : null;
            if (Nullable.Equals(next, _slackDndUntil))
                return; // unchanged — don't churn the UI
            _slackDndUntil = next;
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

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _expiry.Elapsed -= OnExpiry;
        _expiry.Dispose();
    }
}
