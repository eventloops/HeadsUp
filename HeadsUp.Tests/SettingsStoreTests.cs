using HeadsUp.Configuration;
using HeadsUp.Services;
using System.Text.Json.Nodes;
using Xunit;

namespace HeadsUp.Tests;

/// <summary>
/// Both stores write the same appsettings.Local.json beside the app, so all their
/// tests live in one class (xunit runs a class's tests sequentially) and the file
/// is reset before each test.
/// </summary>
public class SettingsStoreTests
{
    private static readonly string LocalSettingsPath =
        Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");

    public SettingsStoreTests()
    {
        if (File.Exists(LocalSettingsPath))
            File.Delete(LocalSettingsPath);
    }

    private static JsonObject ReadLocalSettings() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(LocalSettingsPath))!;

    // ------------------------------------------------------------------
    // Live accessors (read straight from options)
    // ------------------------------------------------------------------

    [Fact]
    public void Priority_store_exposes_current_ids_from_options()
    {
        var store = new JsonPrioritySettingsStore(
            new StaticMonitor<SlackOptions>(new() { PriorityUsers = ["U1", "U2"] }));

        Assert.Equal(new[] { "U1", "U2" }, store.CurrentUserIds);
    }

    [Fact]
    public void Alert_store_exposes_current_options()
    {
        var options = new AlertOptions { Keywords = true, KeywordList = ["deploy"] };
        var store = new JsonAlertSettingsStore(new StaticMonitor<AlertOptions>(options));

        Assert.Same(options, store.Current);
    }

    [Fact]
    public async Task Priority_store_recovers_from_a_non_object_settings_file()
    {
        // A file holding valid-but-wrong JSON (array) is replaced rather than crashing.
        await File.WriteAllTextAsync(LocalSettingsPath, "[ 1, 2 ]");
        var store = new JsonPrioritySettingsStore(new StaticMonitor<SlackOptions>(new()));

        await store.SetAsync(["U1"]);

        Assert.Equal(new[] { "U1" },
            ReadLocalSettings()["Slack"]!["PriorityUsers"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ------------------------------------------------------------------
    // JsonPrioritySettingsStore
    // ------------------------------------------------------------------

    [Fact]
    public async Task Priority_store_creates_the_file_when_missing()
    {
        var store = new JsonPrioritySettingsStore(new StaticMonitor<SlackOptions>(new()));

        await store.SetAsync(["U1", "U2"]);

        var users = ReadLocalSettings()["Slack"]!["PriorityUsers"]!.AsArray();
        Assert.Equal(new[] { "U1", "U2" }, users.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Priority_store_drops_blank_and_duplicate_ids()
    {
        var store = new JsonPrioritySettingsStore(new StaticMonitor<SlackOptions>(new()));

        await store.SetAsync(["U1", "U1", "", "  ", "U2"]);

        var users = ReadLocalSettings()["Slack"]!["PriorityUsers"]!.AsArray();
        Assert.Equal(new[] { "U1", "U2" }, users.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Priority_store_preserves_every_other_setting()
    {
        // Simulate a file already holding the user's secrets and another Slack key.
        await File.WriteAllTextAsync(LocalSettingsPath, """
            {
              "SlackClient": { "ClientToken": "xoxc-secret", "Cookie": "d=xoxd-secret" },
              "Slack": { "UserToken": "xoxp-secret", "PriorityUsers": [ "OLD" ] }
            }
            """);
        var store = new JsonPrioritySettingsStore(new StaticMonitor<SlackOptions>(new()));

        await store.SetAsync(["U9"]);

        var root = ReadLocalSettings();
        Assert.Equal("xoxc-secret", root["SlackClient"]!["ClientToken"]!.GetValue<string>());
        Assert.Equal("xoxp-secret", root["Slack"]!["UserToken"]!.GetValue<string>());
        Assert.Equal(new[] { "U9" }, root["Slack"]!["PriorityUsers"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ------------------------------------------------------------------
    // JsonAlertSettingsStore
    // ------------------------------------------------------------------

    [Fact]
    public async Task Alert_store_writes_toggles_and_cleans_keywords()
    {
        var store = new JsonAlertSettingsStore(new StaticMonitor<AlertOptions>(new()));

        await store.SaveAsync(new AlertOptions
        {
            Huddles = true,
            Mentions = false,
            DirectMessages = true,
            Keywords = true,
            KeywordList = [" deploy ", "Deploy", "", "urgent"]
        });

        var alerts = ReadLocalSettings()["Alerts"]!;
        Assert.True(alerts["Huddles"]!.GetValue<bool>());
        Assert.False(alerts["Mentions"]!.GetValue<bool>());
        Assert.True(alerts["DirectMessages"]!.GetValue<bool>());
        Assert.True(alerts["Keywords"]!.GetValue<bool>());
        // Trimmed and de-duplicated case-insensitively.
        Assert.Equal(new[] { "deploy", "urgent" },
            alerts["KeywordList"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Alert_store_writes_muted_channels_and_dedupes_by_id()
    {
        var store = new JsonAlertSettingsStore(new StaticMonitor<AlertOptions>(new()));

        await store.SaveAsync(new AlertOptions
        {
            MutedChannels =
            {
                new MutedChannel { Id = "C1", Name = "general" },
                new MutedChannel { Id = "c1", Name = "general-dupe" }, // same id, dropped
                new MutedChannel { Id = "", Name = "blank" },          // no id, dropped
                new MutedChannel { Id = "C2", Name = "random" },
            }
        });

        var muted = ReadLocalSettings()["Alerts"]!["MutedChannels"]!.AsArray();
        Assert.Equal(new[] { "C1", "C2" }, muted.Select(m => m!["Id"]!.GetValue<string>()));
        Assert.Equal("general", muted[0]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Alert_store_preserves_every_other_setting()
    {
        await File.WriteAllTextAsync(LocalSettingsPath, """
            {
              "SlackClient": { "ClientToken": "xoxc-secret" },
              "Slack": { "PriorityUsers": [ "U1" ] }
            }
            """);
        var store = new JsonAlertSettingsStore(new StaticMonitor<AlertOptions>(new()));

        await store.SaveAsync(new AlertOptions());

        var root = ReadLocalSettings();
        Assert.Equal("xoxc-secret", root["SlackClient"]!["ClientToken"]!.GetValue<string>());
        Assert.Equal(new[] { "U1" }, root["Slack"]!["PriorityUsers"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.NotNull(root["Alerts"]);
    }
}
