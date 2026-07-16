using SlackHuddleNotifier.Models;

namespace SlackHuddleNotifier.Services;

public interface INotificationService
{
    Task ShowHuddleNotificationAsync(HuddleEvent huddle, CancellationToken ct = default);
}
