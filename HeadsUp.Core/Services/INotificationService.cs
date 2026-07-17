using HeadsUp.Models;

namespace HeadsUp.Services;

public interface INotificationService
{
    Task ShowAlertAsync(Alert huddle, CancellationToken ct = default);
}
