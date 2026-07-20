using System.Text.Json;
using HeadsUp.Services;
using HeadsUp.ViewModels;
using Xunit;

namespace HeadsUp.Wpf.Tests;

public class SlackAppSetupViewModelTests
{
    private readonly RecordingBrowser _browser = new();

    private SlackAppSetupViewModel Vm() => new(_browser);

    // ------------------------------------------------------------------
    // The prefilled create-app deep link
    // ------------------------------------------------------------------

    [Fact]
    public void The_create_app_url_carries_a_valid_prefilled_manifest()
    {
        var url = SlackAppSetupViewModel.CreateAppUrl;

        Assert.StartsWith("https://api.slack.com/apps?new_app=1&manifest_json=", url);

        // The whole point is that the user never handles JSON, so it must be valid and complete.
        var encoded = url[(url.IndexOf("manifest_json=", StringComparison.Ordinal) + 14)..];
        using var manifest = JsonDocument.Parse(Uri.UnescapeDataString(encoded));
        var root = manifest.RootElement;

        Assert.Equal("Heads-Up", root.GetProperty("display_information").GetProperty("name").GetString());
        Assert.True(root.GetProperty("settings").GetProperty("socket_mode_enabled").GetBoolean());
    }

    [Fact]
    public void The_manifest_registers_the_redirect_the_listener_actually_uses()
    {
        var url = SlackAppSetupViewModel.CreateAppUrl;
        var encoded = url[(url.IndexOf("manifest_json=", StringComparison.Ordinal) + 14)..];
        using var manifest = JsonDocument.Parse(Uri.UnescapeDataString(encoded));

        var redirects = manifest.RootElement
            .GetProperty("oauth_config").GetProperty("redirect_urls")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        // A mismatch here would fail only at the end of the OAuth round trip, so pin it.
        Assert.Contains(LoopbackOAuthListener.RedirectUri, redirects);
    }

    [Fact]
    public void The_manifest_requests_exactly_the_scopes_the_oauth_client_asks_for()
    {
        // The manifest is now the embedded docs file rather than a copy in code, so this is
        // what stops the two drifting: asking for a scope Slack was never told about fails at
        // the consent screen, and granting one nothing uses is silent over-permission.
        var manifest = ParseManifest();
        var scopes = manifest.RootElement
            .GetProperty("oauth_config").GetProperty("scopes").GetProperty("user")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Equal(SlackOAuthClient.UserScopes.OrderBy(s => s), scopes.OrderBy(s => s));
    }

    [Fact]
    public void This_manifest_is_the_per_user_app_and_carries_no_bot_scopes()
    {
        // The per-user app and the relay app are different apps with incompatible settings:
        // PKCE (which a desktop needs, since it can't hold a secret) makes localhost a desktop
        // redirect, and desktop redirects may not request bot scopes. Combining them produced
        // an app that could never be granted the scopes it asked for.
        var oauth = ParseManifest().RootElement.GetProperty("oauth_config");

        Assert.True(oauth.GetProperty("pkce_enabled").GetBoolean());
        Assert.False(oauth.GetProperty("scopes").TryGetProperty("bot", out _));

        var redirects = oauth.GetProperty("redirect_urls")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.All(redirects, r => Assert.Contains($":{LoopbackOAuthListener.Port}", r));
    }

    [Fact]
    public void The_manifest_enables_the_token_rotation_the_desktop_handles()
    {
        var settings = ParseManifest().RootElement.GetProperty("settings");

        Assert.True(settings.GetProperty("token_rotation_enabled").GetBoolean());
    }

    private static JsonDocument ParseManifest() =>
        JsonDocument.Parse(SlackAppSetupViewModel.ManifestJson);

    [Fact]
    public void The_manifest_subscribes_to_user_events_not_bot_events()
    {
        var url = SlackAppSetupViewModel.CreateAppUrl;
        var encoded = url[(url.IndexOf("manifest_json=", StringComparison.Ordinal) + 14)..];
        using var manifest = JsonDocument.Parse(Uri.UnescapeDataString(encoded));
        var subscriptions = manifest.RootElement.GetProperty("settings").GetProperty("event_subscriptions");

        // Desktop OAuth redirects can't request bot scopes, so bot events would never fire.
        Assert.True(subscriptions.TryGetProperty("user_events", out var userEvents));
        Assert.False(subscriptions.TryGetProperty("bot_events", out _));
        Assert.Equal(4, userEvents.GetArrayLength());
    }

    [Fact]
    public void Creating_the_app_opens_the_browser_and_reveals_the_next_step()
    {
        var vm = Vm();

        vm.CreateAppCommand.Execute(null);

        Assert.True(vm.AppCreationStarted);
        Assert.Equal(SlackAppSetupViewModel.CreateAppUrl, _browser.Urls.Single());
    }

    // ------------------------------------------------------------------
    // Validation — catching the mistakes people actually make
    // ------------------------------------------------------------------

    [Fact]
    public void Swapped_fields_are_named_explicitly()
    {
        var error = SlackAppSetupViewModel.Validate("xapp-1234", "xapp-1234");

        Assert.Contains("Client ID goes in the first field", error);
    }

    [Fact]
    public void A_user_token_in_the_app_token_field_is_called_out()
    {
        var error = SlackAppSetupViewModel.Validate("123.456", "xoxp-abc");

        Assert.Contains("bot or user token", error);
    }

    [Theory]
    [InlineData("", "xapp-1", "Client ID")]
    [InlineData("not-an-id", "xapp-1", "doesn't look like a Client ID")]
    [InlineData("123.456", "", "app-level token")]
    [InlineData("123.456", "totally-wrong", "should start with xapp-")]
    public void Malformed_input_is_rejected_with_a_specific_message(string clientId, string appToken, string expected) =>
        Assert.Contains(expected, SlackAppSetupViewModel.Validate(clientId, appToken));

    [Fact]
    public void Well_formed_values_pass() =>
        Assert.Null(SlackAppSetupViewModel.Validate("1234567890.0987654321", "xapp-1-A0B1C2"));

    // ------------------------------------------------------------------
    // Confirm / cancel
    // ------------------------------------------------------------------

    [Fact]
    public void Confirming_valid_values_yields_a_result_and_closes()
    {
        var vm = Vm();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.ClientId = "  1234567890.0987654321  "; // pasted values often carry whitespace
        vm.AppToken = " xapp-1-A0B1C2 ";

        vm.ConfirmCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal("1234567890.0987654321", vm.Result!.ClientId);
        Assert.Equal("xapp-1-A0B1C2", vm.Result.AppToken);
    }

    [Fact]
    public void Confirming_invalid_values_shows_the_error_and_stays_open()
    {
        var vm = Vm();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.ClientId = "nope";
        vm.AppToken = "also-nope";

        vm.ConfirmCommand.Execute(null);

        Assert.False(closed);
        Assert.Null(vm.Result);
        Assert.NotNull(vm.ValidationError);
    }

    [Fact]
    public void Cancelling_yields_no_result()
    {
        var vm = Vm();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.Null(vm.Result);
    }

    private sealed class RecordingBrowser : IBrowserLauncher
    {
        public List<string> Urls { get; } = [];
        public void Open(string url) => Urls.Add(url);
    }
}
