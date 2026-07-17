using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HeadsUp.Configuration;
using HeadsUp.Models;
using HeadsUp.ViewModels;
using HeadsUp.Views;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Threading;

namespace HeadsUp.Services;

/// <summary>
/// Shows a WPF popup window AND a Windows Toast notification when a huddle starts.
/// </summary>
public sealed class WpfNotificationService : INotificationService
{
    private readonly IOptionsMonitor<NotificationOptions> _options;
    private readonly AlertLinks _links;
    private readonly ILogger<WpfNotificationService> _logger;
    private readonly Dispatcher _dispatcher;

    // Popups currently on screen, so simultaneous alerts stack instead of overlapping.
    // Only touched on the dispatcher thread.
    private readonly List<HuddlePopupWindow> _openPopups = new();

    public WpfNotificationService(
        IOptionsMonitor<NotificationOptions> options,
        AlertLinks links,
        ILogger<WpfNotificationService> logger)
    {
        _options = options;
        _links = links;
        _logger = logger;
        _dispatcher = Application.Current.Dispatcher;
    }

    public Task ShowAlertAsync(Alert huddle, CancellationToken ct = default)
    {
        return _dispatcher.InvokeAsync(() =>
        {
            try
            {
                PlayAlertSound();
                ShowPopup(huddle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to show WPF huddle popup.");
            }
        }).Task;
    }

    private void ShowPopup(Alert huddle)
    {
        var options = _options.CurrentValue;
        var vm = new HuddlePopupViewModel();
        vm.Populate(huddle);
        vm.JoinTargets = _links.Targets(huddle);

        var window = new HuddlePopupWindow
        {
            DataContext = vm,
            // Stack above any popups already showing (12px gap between each).
            StackOffset = _openPopups.Sum(w => w.ActualHeight + 12)
        };
        _openPopups.Add(window);
        window.Closed += (_, _) => _openPopups.Remove(window);

        // Auto-dismiss after configured duration.
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(options.ToastDurationSeconds)
        };
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
        window.StartCountdown(TimeSpan.FromSeconds(options.ToastDurationSeconds));
        timer.Start();

        _logger.LogDebug("WPF popup shown for huddle in {Channel}.", huddle.ChannelName);
    }

    private void PlayAlertSound()
    {
        var options = _options.CurrentValue;
        if (!options.PlaySound)
            return;

        try
        {
            if (!string.IsNullOrWhiteSpace(options.SoundFilePath) &&
                File.Exists(options.SoundFilePath))
            {
                using var player = new SoundPlayer(options.SoundFilePath);
                player.Play();
            }
            else
            {
                SystemSounds.Exclamation.Play();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not play alert sound.");
        }
    }
}
