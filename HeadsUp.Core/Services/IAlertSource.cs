using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>Raised whenever Slack activity worth alerting on is detected.</summary>
public interface IAlertSource
{
    event EventHandler<Alert>? AlertRaised;
}
