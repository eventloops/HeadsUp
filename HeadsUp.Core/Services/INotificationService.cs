using HeadsUp.Models;

namespace HeadsUp.Services;

public interface INotificationService
{
    Task ShowHuddleNotificationAsync(HuddleEvent huddle, CancellationToken ct = default);
}
