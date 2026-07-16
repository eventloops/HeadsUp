using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackHuddleNotifier.Configuration;
using SlackHuddleNotifier.Models;
using SlackHuddleNotifier.ViewModels;
using SlackHuddleNotifier.Views;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Threading;

namespace SlackHuddleNotifier.Services;

/// <summary>
/// Shows a WPF popup window AND a Windows Toast notification when a huddle starts.
/// </summary>
public sealed class WpfNotificationService : INotificationService
{
    private readonly NotificationOptions _options;
    private readonly ILogger<WpfNotificationService> _logger;
    private readonly Dispatcher _dispatcher;

    public WpfNotificationService(
        IOptions<NotificationOptions> options,
        ILogger<WpfNotificationService> logger)
    {
        _options = options.Value;
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
        var vm = new HuddlePopupViewModel();
        vm.Populate(huddle);

        var window = new HuddlePopupWindow
        {
            DataContext = vm
        };

        // Auto-dismiss after configured duration.
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_options.ToastDurationSeconds)
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
        window.StartCountdown(TimeSpan.FromSeconds(_options.ToastDurationSeconds));
        timer.Start();

        _logger.LogDebug("WPF popup shown for huddle in {Channel}.", huddle.ChannelName);
    }

    private void PlayAlertSound()
    {
        if (!_options.PlaySound)
            return;

        try
        {
            if (!string.IsNullOrWhiteSpace(_options.SoundFilePath) &&
                File.Exists(_options.SoundFilePath))
            {
                using var player = new SoundPlayer(_options.SoundFilePath);
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
