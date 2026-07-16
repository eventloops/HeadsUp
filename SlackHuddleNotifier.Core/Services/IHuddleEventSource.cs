using SlackHuddleNotifier.Models;

namespace SlackHuddleNotifier.Services;

/// <summary>Raised whenever a Huddle is detected in the workspace.</summary>
public interface IHuddleEventSource
{
    event EventHandler<HuddleEvent>? HuddleStarted;
}
