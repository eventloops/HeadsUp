using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Toolkit.Uwp.Notifications;
using SlackHuddleNotifier.Configuration;
using SlackHuddleNotifier.Models;
using System.Media;

namespace SlackHuddleNotifier.Services;

/// <summary>
/// Shows a Windows Toast notification and optionally plays a sound when a huddle starts.
/// </summary>
public sealed class ToastNotificationService : INotificationService
{
    private readonly NotificationOptions _options;
    private readonly ILogger<ToastNotificationService> _logger;

    private const string AppId = "SlackHuddleNotifier";

    public ToastNotificationService(
        IOptions<NotificationOptions> options,
        ILogger<ToastNotificationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task ShowHuddleNotificationAsync(HuddleEvent huddle, CancellationToken ct = default)
    {
        try
        {
            PlayAlertSound();
            ShowToast(huddle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show notification for huddle in {Channel}.", huddle.ChannelName);
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------

    private void ShowToast(HuddleEvent huddle)
    {
        var channelDisplay = huddle.ChannelName.StartsWith('D')
            ? $"DM with {huddle.InitiatorDisplayName}"
            : $"#{huddle.ChannelName}";

        var priorityPrefix = huddle.IsPriorityUser ? "?? PRIORITY – " : string.Empty;

        new ToastContentBuilder()
            .AddAppLogoOverride(new Uri("ms-appx:///Assets/tray.png"), ToastGenericAppLogoCrop.Circle)
            .AddHeader("huddle", $"{priorityPrefix}Slack Huddle Started", string.Empty)
            .AddText($"{huddle.InitiatorDisplayName} started a huddle", hintStyle: AdaptiveTextStyle.Title)
            .AddText(channelDisplay, hintStyle: AdaptiveTextStyle.Body)
            .AddText(huddle.OccurredAt.ToLocalTime().ToString("h:mm tt"), hintStyle: AdaptiveTextStyle.CaptionSubtle)
            .AddButton(new ToastButton()
                .SetContent("Join Huddle")
                .SetProtocolActivation(new Uri(huddle.HuddleLink)))
            .AddButton(new ToastButtonDismiss("Dismiss"))
            .SetToastDuration(ToastDuration.Long)
            .SetToastScenario(huddle.IsPriorityUser
                ? ToastScenario.IncomingCall
                : ToastScenario.Default)
            .Show(toast =>
            {
                toast.ExpirationTime = DateTimeOffset.Now.AddSeconds(_options.ToastDurationSeconds);
                toast.Tag = $"huddle_{huddle.ChannelId}";
                toast.Group = "Huddles";
            });

        _logger.LogDebug("Toast notification shown for huddle in {Channel}.", huddle.ChannelName);
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
