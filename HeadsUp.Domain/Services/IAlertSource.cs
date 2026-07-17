using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>Raised whenever activity worth alerting on is detected by a connector.</summary>
public interface IAlertSource
{
    /// <summary>Stable identity for this connector. Used to namespace settings, pick a
    /// deep-link resolver, and label per-source status.</summary>
    SourceId SourceId { get; }

    /// <summary>Human-readable connector name, e.g. "Slack".</summary>
    string SourceName { get; }

    event EventHandler<Alert>? AlertRaised;
}
