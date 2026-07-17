using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>Raised whenever a Huddle is detected in the workspace.</summary>
public interface IHuddleEventSource
{
    event EventHandler<HuddleEvent>? HuddleStarted;
}
