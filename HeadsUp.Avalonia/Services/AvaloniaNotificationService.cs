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
    private readonly AlertLinks _links;
    private readonly ILogger<AvaloniaNotificationService> _logger;

    // Popups currently on screen, so simultaneous alerts stack instead of overlapping.
    // Only touched on the UI thread.
    private readonly List<HuddlePopupWindow> _openPopups = new();

    public AvaloniaNotificationService(
        IOptionsMonitor<NotificationOptions> options,
        AlertLinks links,
        ILogger<AvaloniaNotificationService> logger)
    {
        _options = options;
        _links = links;
        _logger = logger;
    }

    public async Task ShowAlertAsync(Alert huddle, CancellationToken ct = default)
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

    private void ShowPopup(Alert huddle)
    {
        var vm = new HuddlePopupViewModel();
        vm.Populate(huddle);
        vm.JoinTargets = _links.Targets(huddle);

        var window = new HuddlePopupWindow
        {
            DataContext = vm,
            // Stack above any popups already showing (12px gap between each).
            StackOffset = _openPopups.Sum(w => w.Bounds.Height + 12)
        };
        _openPopups.Add(window);
        window.Closed += (_, _) => _openPopups.Remove(window);
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
                // Same behaviour as the WPF head: custom .wav if configured, else the
                // system chime. (Console.Beep is unreliable in a windowed app.)
                if (custom is not null)
                {
                    using var player = new System.Media.SoundPlayer(custom);
                    player.Play();
                }
                else
                {
                    System.Media.SystemSounds.Exclamation.Play();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not play alert sound.");
        }
    }
}
