using System.Text.Json;
using HeadsUp.Relay.Endpoints;
using Xunit;

namespace HeadsUp.Relay.Tests;

/// <summary>
/// The relay's Slack app is a different app from the per-user one, with settings that cannot
/// coexist: PKCE makes localhost a desktop redirect, and desktop redirects may not request bot
/// scopes — so a single app asking for both could never be granted what it asked for.
///
/// These read the manifest people are told to paste, because a mismatch between it and the code
/// fails at the end of an OAuth round trip with an opaque Slack error.
/// </summary>
public class RelayManifestTests
{
    private static JsonDocument Manifest()
    {
        // Walk up from the test binary to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "slack-relay-manifest.json")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return JsonDocument.Parse(File.ReadAllText(
            Path.Combine(dir!.FullName, "docs", "slack-relay-manifest.json")));
    }

    [Fact]
    public void It_does_not_enable_pkce()
    {
        // The relay holds a client secret, so it is a confidential client. Enabling PKCE would
        // reclassify localhost redirects as desktop ones and forfeit bot scopes — permanently,
        // since PKCE cannot be turned off once on.
        using var manifest = Manifest();

        Assert.False(manifest.RootElement.GetProperty("oauth_config")
            .TryGetProperty("pkce_enabled", out var pkce) && pkce.GetBoolean());
    }

    [Fact]
    public void It_requests_the_bot_scopes_name_resolution_needs()
    {
        using var manifest = Manifest();
        var bot = manifest.RootElement.GetProperty("oauth_config").GetProperty("scopes")
            .GetProperty("bot").EnumerateArray().Select(e => e.GetString()).ToList();

        // Without users:read the relay cannot turn an id into a person, and every alert reads
        // "U0BK42E575E started a huddle".
        Assert.Contains("users:read", bot);
        Assert.Contains("channels:read", bot);
    }

    [Fact]
    public void It_declares_a_bot_user()
    {
        // Slack's validator rejects bot scopes without one, whatever the schema docs imply.
        using var manifest = Manifest();

        Assert.False(string.IsNullOrWhiteSpace(manifest.RootElement
            .GetProperty("features").GetProperty("bot_user").GetProperty("display_name").GetString()));
    }

    [Fact]
    public void It_registers_the_callback_the_relay_actually_redirects_to()
    {
        using var manifest = Manifest();
        var redirects = manifest.RootElement.GetProperty("oauth_config")
            .GetProperty("redirect_urls").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Contains(redirects, r => r.EndsWith(RelayEndpoints.CallbackPath, StringComparison.Ordinal));
    }

    [Fact]
    public void It_enables_socket_mode_and_subscribes_to_user_events()
    {
        using var manifest = Manifest();
        var settings = manifest.RootElement.GetProperty("settings");

        Assert.True(settings.GetProperty("socket_mode_enabled").GetBoolean());

        // User events, not bot events: the relay routes by which user Slack says may see an
        // event, which only user-scoped subscriptions produce.
        var events = settings.GetProperty("event_subscriptions");
        Assert.True(events.TryGetProperty("user_events", out _));
        Assert.False(events.TryGetProperty("bot_events", out _));
    }

    [Fact]
    public void It_leaves_token_rotation_off()
    {
        // The relay's credentials are the non-expiring app-level token and a bot token. Turning
        // rotation on would make the bot token expire in ~12 hours, and the relay has no
        // renewal logic — name resolution would work today and stop tomorrow.
        using var manifest = Manifest();

        Assert.False(manifest.RootElement.GetProperty("settings")
            .GetProperty("token_rotation_enabled").GetBoolean());
    }
}
