namespace HeadsUp.Configuration;

/// <summary>
/// One entry in <see cref="DetectionOptions.Connectors"/>: a detection route to run.
/// Multiple entries run side by side, so a future release can watch Slack and (say)
/// Teams at once. <see cref="Type"/> is a <see cref="DetectionOptions"/> route name
/// (e.g. "SlackClientApi", "SlackApi").
/// </summary>
public sealed class ConnectorOptions
{
    public string Type { get; set; } = string.Empty;
}
