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
    private readonly ILogger<WpfNotificationService> _logger;
    private readonly Dispatcher _dispatcher;

    public WpfNotificationService(
        IOptionsMonitor<NotificationOptions> options,
        ILogger<WpfNotificationService> logger)
    {
        _options = options;
        _logger = logger;
        _dispatcher = Application.Current.Dispatcher;
    }

    public Task ShowHuddleNotificationAsync(HuddleEvent huddle, CancellationToken ct = default)
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

    private void ShowPopup(HuddleEvent huddle)
    {
        var options = _options.CurrentValue;
        var vm = new HuddlePopupViewModel();
        vm.Populate(huddle);

        var window = new HuddlePopupWindow
        {
            DataContext = vm
        };

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
