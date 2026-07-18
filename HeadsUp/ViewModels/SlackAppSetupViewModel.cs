using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadsUp.Services;

namespace HeadsUp.ViewModels;

/// <summary>
/// Backs the Slack-app setup screen: create the app from a prefilled manifest, then paste back
/// the two values OAuth can't provide (the client id and the app-level token).
///
/// Those two are irreducible. Slack mints app-level tokens only in its own config UI — there's
/// no API for it — so this screen exists to make that unavoidable step as short as possible
/// rather than to work around it.
/// </summary>
public sealed partial class SlackAppSetupViewModel : ObservableObject
{
    /// <summary>
    /// The manifest, inlined so the deep link works with no network fetch and no file for the
    /// user to handle. Kept in step with <c>docs/slack-app-manifest.json</c>.
    /// </summary>
    private static readonly string ManifestJson = JsonSerializer.Serialize(new
    {
        display_information = new
        {
            name = "Heads-Up",
            description = "Unmissable desktop alerts for huddles, mentions and DMs.",
            background_color = "#1a1614",
        },
        oauth_config = new
        {
            redirect_urls = new[] { LoopbackOAuthListener.RedirectUri },
            scopes = new { user = SlackOAuthClient.UserScopes },
        },
        settings = new
        {
            event_subscriptions = new
            {
                user_events = new[] { "message.channels", "message.groups", "message.im", "message.mpim" },
            },
            socket_mode_enabled = true,
            token_rotation_enabled = true,
        },
    });

    private readonly IBrowserLauncher _browser;

    [ObservableProperty] private string _clientId = string.Empty;
    [ObservableProperty] private string _appToken = string.Empty;

    /// <summary>Set once the user has been sent to Slack, to reveal the "paste them back" step.</summary>
    [ObservableProperty] private bool _appCreationStarted;

    /// <summary>Validation message shown under the fields, or null when they look right.</summary>
    [ObservableProperty] private string? _validationError;

    /// <summary>The values the user confirmed, or null if they cancelled.</summary>
    public SlackAppSetup? Result { get; private set; }

    /// <summary>Raised when the window should close, having set <see cref="Result"/>.</summary>
    public event EventHandler? CloseRequested;

    public SlackAppSetupViewModel(IBrowserLauncher browser) => _browser = browser;

    /// <summary>
    /// The prefilled app-creation URL. Slack accepts the manifest as a query parameter, so the
    /// user never copies or pastes JSON — they just confirm the workspace.
    /// </summary>
    public static string CreateAppUrl =>
        "https://api.slack.com/apps?new_app=1&manifest_json=" + Uri.EscapeDataString(ManifestJson);

    [RelayCommand]
    private void CreateApp()
    {
        AppCreationStarted = true;
        _browser.Open(CreateAppUrl);
    }

    [RelayCommand]
    private void OpenSetupGuide() =>
        _browser.Open("https://github.com/CameronLambert/HeadsUp/blob/main/docs/slack-app-setup.md");

    [RelayCommand]
    private void Confirm()
    {
        var clientId = ClientId.Trim();
        var appToken = AppToken.Trim();

        if (Validate(clientId, appToken) is { } error)
        {
            ValidationError = error;
            return;
        }

        ValidationError = null;
        Result = new SlackAppSetup(clientId, appToken);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Catches the two mistakes that actually happen — swapping the fields, and pasting a bot
    /// or user token instead of the app-level one — so they surface here rather than as an
    /// opaque OAuth failure later.
    /// </summary>
    internal static string? Validate(string clientId, string appToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return "Enter the Client ID from your app's Basic Information page.";

        if (clientId.StartsWith("xapp-", StringComparison.OrdinalIgnoreCase))
            return "That looks like the app-level token. The Client ID goes in the first field.";

        // Slack client ids look like 1234567890123.1234567890123.
        if (!clientId.Contains('.') || !clientId.Replace(".", "").All(char.IsDigit))
            return "That doesn't look like a Client ID — expected digits in the form 1234567890.1234567890.";

        if (string.IsNullOrWhiteSpace(appToken))
            return "Enter the app-level token you generated (starts with xapp-).";

        if (!appToken.StartsWith("xapp-", StringComparison.OrdinalIgnoreCase))
            return appToken.StartsWith("xox", StringComparison.OrdinalIgnoreCase)
                ? "That's a bot or user token. Heads-Up needs the app-level token, which starts with xapp-."
                : "The app-level token should start with xapp-.";

        return null;
    }
}
