using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HeadsUp.AvaloniaApp.Views;
using HeadsUp.Configuration;
using HeadsUp.Models;
using HeadsUp.Services;
using HeadsUp.ViewModels;
using System.Diagnostics;

namespace HeadsUp.AvaloniaApp.Services;

/// <summary>
/// Cross-platform popup + alert sound when a huddle starts (macOS, Windows, Linux).
/// </summary>
public sealed class AvaloniaNotificationService : INotificationService
{
    private readonly IOptionsMonitor<NotificationOptions> _options;
    private readonly ILogger<AvaloniaNotificationService> _logger;

    public AvaloniaNotificationService(
        IOptionsMonitor<NotificationOptions> options,
        ILogger<AvaloniaNotificationService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task ShowHuddleNotificationAsync(HuddleEvent huddle, CancellationToken ct = default)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                PlayAlertSound();
                ShowPopup(huddle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to show huddle popup.");
            }
        });
    }

    private void ShowPopup(HuddleEvent huddle)
    {
        var vm = new HuddlePopupViewModel();
        vm.Populate(huddle);

        var window = new HuddlePopupWindow { DataContext = vm };
        window.SetPriority(huddle.IsPriorityUser);
        window.LoadAvatar(huddle.InitiatorAvatarUrl);

        var lifetime = TimeSpan.FromSeconds(_options.CurrentValue.ToastDurationSeconds);
        var timer = new DispatcherTimer { Interval = lifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.AnimateOut();
        };

        vm.CloseRequested += (_, _) =>
        {
            timer.Stop();
            window.AnimateOut();
        };

        window.AnimateIn();
        window.StartCountdown(lifetime);
        timer.Start();

        _logger.LogDebug("Popup shown for huddle in {Channel}.", huddle.ChannelName);
    }

    private void PlayAlertSound()
    {
        var options = _options.CurrentValue;
        if (!options.PlaySound)
            return;

        try
        {
            var custom = !string.IsNullOrWhiteSpace(options.SoundFilePath) && File.Exists(options.SoundFilePath)
                ? options.SoundFilePath
                : null;

            if (OperatingSystem.IsMacOS())
            {
                // afplay ships with macOS; Glass is the classic "ping".
                var path = custom ?? "/System/Library/Sounds/Glass.aiff";
                Process.Start(new ProcessStartInfo("afplay", $"\"{path}\"") { UseShellExecute = false });
            }
            else if (OperatingSystem.IsLinux() && custom is not null)
            {
                Process.Start(new ProcessStartInfo("aplay", $"\"{custom}\"") { UseShellExecute = false });
            }
            else if (OperatingSystem.IsWindows())
            {
                // The WPF head is the primary Windows experience; keep a simple chime here.
                Console.Beep(880, 200);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not play alert sound.");
        }
    }
}
