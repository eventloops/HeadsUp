using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>
/// Resolves the ordered launch targets for any alert by dispatching to the
/// <see cref="IAlertLinkResolver"/> registered for the alert's connector, then always
/// appending the raw <see cref="Alert.Link"/> as a final fallback. Injected wherever an
/// alert is opened (the huddle popup and the dashboard's history), so no call site needs
/// to know which connector produced the alert.
/// </summary>
public sealed class AlertLinks
{
    private readonly Dictionary<string, IAlertLinkResolver> _bySource;

    public AlertLinks(IEnumerable<IAlertLinkResolver> resolvers) =>
        // Group so two connectors that share a source id (e.g. both Slack routes) don't
        // collide; the first registered resolver for a source wins.
        _bySource = resolvers
            .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Preferred deep-link targets for the alert, best first, ending in the raw link.</summary>
    public IReadOnlyList<string> Targets(Alert alert)
    {
        var targets = new List<string>();
        if (_bySource.TryGetValue(alert.SourceId, out var resolver))
            targets.AddRange(resolver.Targets(alert));

        if (!string.IsNullOrEmpty(alert.Link))
            targets.Add(alert.Link); // always the last-resort fallback
        return targets;
    }
}
