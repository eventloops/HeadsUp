using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Toolkit.Uwp.Notifications;
using SlackHuddleNotifier.Configuration;
using SlackHuddleNotifier.Models;
using System.IO;
using System.Media;

namespace SlackHuddleNotifier.Services;

/// <summary>
/// Shows a Windows Toast notification and optionally plays a sound when a huddle starts.
/// </summary>
public sealed class ToastNotificationService : INotificationService
{
    private readonly IOptionsMonitor<NotificationOptions> _options;
    private readonly ILogger<ToastNotificationService> _logger;

    private const string AppId = "SlackHuddleNotifier";

    public ToastNotificationService(
        IOptionsMonitor<NotificationOptions> options,
        ILogger<ToastNotificationService> logger)
    {
        _options = options;
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
        var channelDisplay = huddle.IsDirectMessage
            ? $"DM with {huddle.InitiatorDisplayName}"
            : $"#{huddle.ChannelName}";

        var priorityPrefix = huddle.IsPriorityUser ? "PRIORITY - " : string.Empty;

        var builder = new ToastContentBuilder();

        // App logo: this is an unpackaged app, so ms-appx URIs don't resolve.
        // Use the icon copied next to the executable, if present.
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
        if (File.Exists(logoPath))
            builder.AddAppLogoOverride(new Uri(logoPath), ToastGenericAppLogoCrop.Circle);

        builder
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
                toast.ExpirationTime = DateTimeOffset.Now.AddSeconds(_options.CurrentValue.ToastDurationSeconds);
                toast.Tag = $"huddle_{huddle.ChannelId}";
                toast.Group = "Huddles";
            });

        _logger.LogDebug("Toast notification shown for huddle in {Channel}.", huddle.ChannelName);
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
