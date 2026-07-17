using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>
/// Produces the ordered list of launch targets for an alert from a specific connector —
/// e.g. a desktop-app deep link preferred over the web URL. One implementation per
/// connector, selected by <see cref="SourceId"/>. The raw <see cref="Alert.Link"/> is
/// appended as a final fallback by <see cref="AlertLinks"/>, so a resolver only needs to
/// yield the richer targets it knows how to build.
/// </summary>
public interface IAlertLinkResolver
{
    /// <summary>The connector this resolver handles, matched against <see cref="Alert.SourceId"/>.</summary>
    SourceId SourceId { get; }

    /// <summary>Preferred launch targets for the alert, best first. May be empty.</summary>
    IEnumerable<string> Targets(Alert alert);
}
