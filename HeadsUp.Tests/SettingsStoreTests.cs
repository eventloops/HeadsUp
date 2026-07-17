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

    private static PriorityOptions Priority(params (string Source, string Id)[] users) =>
        new() { Users = users.Select(u => new PriorityUser { Source = u.Source, Id = u.Id }).ToList() };

    private static IEnumerable<(string Source, string Id)> ReadPriorityUsers() =>
        ReadLocalSettings()["Priority"]!["Users"]!.AsArray()
            .Select(n => (n!["Source"]!.GetValue<string>(), n["Id"]!.GetValue<string>()));

    // ------------------------------------------------------------------
    // Live accessors (read straight from options)
    // ------------------------------------------------------------------

    [Fact]
    public void Priority_store_exposes_current_ids_for_a_source()
    {
        var store = new JsonPrioritySettingsStore(
            new StaticMonitor<PriorityOptions>(Priority(("slack", "U1"), ("slack", "U2"), ("teams", "T1"))));

        Assert.Equal(new[] { "U1", "U2" }, store.CurrentUserIds("slack"));
        Assert.Equal(new[] { "T1" }, store.CurrentUserIds("teams"));
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
        await File.WriteAllTextAsync(LocalSettingsPath, "[ 1, 2 ]");
        var store = new JsonPrioritySettingsStore(new StaticMonitor<PriorityOptions>(new()));

        await store.SetAsync("slack", ["U1"]);

        Assert.Equal(new[] { ("slack", "U1") }, ReadPriorityUsers());
    }

    // ------------------------------------------------------------------
    // JsonPrioritySettingsStore
    // ------------------------------------------------------------------

    [Fact]
    public async Task Priority_store_creates_the_file_with_namespaced_entries()
    {
        var store = new JsonPrioritySettingsStore(new StaticMonitor<PriorityOptions>(new()));

        await store.SetAsync("slack", ["U1", "U2"]);

        Assert.Equal(new[] { ("slack", "U1"), ("slack", "U2") }, ReadPriorityUsers());
    }

    [Fact]
    public async Task Priority_store_drops_blank_and_duplicate_ids()
    {
        var store = new JsonPrioritySettingsStore(new StaticMonitor<PriorityOptions>(new()));

        await store.SetAsync("slack", ["U1", "U1", "", "  ", "U2"]);

        Assert.Equal(new[] { ("slack", "U1"), ("slack", "U2") }, ReadPriorityUsers());
    }

    [Fact]
    public async Task Priority_store_replaces_only_its_source_and_keeps_others()
    {
        var store = new JsonPrioritySettingsStore(
            new StaticMonitor<PriorityOptions>(Priority(("slack", "OLD"), ("teams", "T1"))));

        await store.SetAsync("slack", ["U9"]);

        // Teams entry kept; Slack replaced.
        Assert.Equal(new[] { ("teams", "T1"), ("slack", "U9") }, ReadPriorityUsers());
    }

    [Fact]
    public async Task Priority_store_preserves_every_other_setting()
    {
        await File.WriteAllTextAsync(LocalSettingsPath, """
            {
              "SlackClient": { "ClientToken": "xoxc-secret", "Cookie": "d=xoxd-secret" },
              "Slack": { "UserToken": "xoxp-secret" }
            }
            """);
        var store = new JsonPrioritySettingsStore(new StaticMonitor<PriorityOptions>(new()));

        await store.SetAsync("slack", ["U9"]);

        var root = ReadLocalSettings();
        Assert.Equal("xoxc-secret", root["SlackClient"]!["ClientToken"]!.GetValue<string>());
        Assert.Equal("xoxp-secret", root["Slack"]!["UserToken"]!.GetValue<string>());
        Assert.Equal(new[] { ("slack", "U9") }, ReadPriorityUsers());
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
        Assert.Equal(new[] { "deploy", "urgent" },
            alerts["KeywordList"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Alert_store_writes_muted_channels_with_source_and_dedupes()
    {
        var store = new JsonAlertSettingsStore(new StaticMonitor<AlertOptions>(new()));

        await store.SaveAsync(new AlertOptions
        {
            MutedChannels =
            {
                new MutedChannel { Id = "C1", Name = "general", Source = "slack" },
                new MutedChannel { Id = "c1", Name = "general-dupe", Source = "slack" }, // same source+id, dropped
                new MutedChannel { Id = "", Name = "blank", Source = "slack" },          // no id, dropped
                new MutedChannel { Id = "C1", Name = "teams-c1", Source = "teams" },      // same id, other source: kept
                new MutedChannel { Id = "C2", Name = "random", Source = "slack" },
            }
        });

        var muted = ReadLocalSettings()["Alerts"]!["MutedChannels"]!.AsArray();
        Assert.Equal(
            new[] { ("C1", "slack"), ("C1", "teams"), ("C2", "slack") },
            muted.Select(m => (m!["Id"]!.GetValue<string>(), m["Source"]!.GetValue<string>())));
    }

    [Fact]
    public async Task Alert_store_preserves_every_other_setting()
    {
        await File.WriteAllTextAsync(LocalSettingsPath, """
            {
              "SlackClient": { "ClientToken": "xoxc-secret" },
              "Priority": { "Users": [ { "Source": "slack", "Id": "U1" } ] }
            }
            """);
        var store = new JsonAlertSettingsStore(new StaticMonitor<AlertOptions>(new()));

        await store.SaveAsync(new AlertOptions());

        var root = ReadLocalSettings();
        Assert.Equal("xoxc-secret", root["SlackClient"]!["ClientToken"]!.GetValue<string>());
        Assert.Equal("U1", root["Priority"]!["Users"]!.AsArray()[0]!["Id"]!.GetValue<string>());
        Assert.NotNull(root["Alerts"]);
    }
}
