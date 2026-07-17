using Microsoft.Extensions.Options;
using SlackHuddleNotifier.Configuration;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace SlackHuddleNotifier.Services;

/// <summary>Reads and persists the priority-user list.</summary>
public interface IPrioritySettingsStore
{
    /// <summary>The current priority user ids (live, from options).</summary>
    IReadOnlyList<string> CurrentUserIds { get; }

    /// <summary>Persists the full priority-user id list.</summary>
    Task SetAsync(IEnumerable<string> userIds, CancellationToken ct = default);
}

/// <summary>
/// Persists the priority-user list into the git-ignored <c>appsettings.Local.json</c> beside
/// the app (the file config is loaded from), preserving every other setting. Because it lands
/// in the watched config file, <see cref="IOptionsMonitor{T}"/> hot-reloads it and the change
/// applies with no restart.
/// </summary>
public sealed class JsonPrioritySettingsStore : IPrioritySettingsStore
{
    // A resolver is required to serialize the CLR string values we add to the JsonArray;
    // without it ToJsonString throws "must specify a TypeInfoResolver".
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly IOptionsMonitor<SlackOptions> _slackOptions;

    public JsonPrioritySettingsStore(IOptionsMonitor<SlackOptions> slackOptions) => _slackOptions = slackOptions;

    public IReadOnlyList<string> CurrentUserIds => _slackOptions.CurrentValue.PriorityUsers;

    public async Task SetAsync(IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");

        var root = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        if (root["Slack"] is not JsonObject slack)
        {
            slack = new JsonObject();
            root["Slack"] = slack;
        }

        var array = new JsonArray();
        foreach (var id in userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
            array.Add(id);
        slack["PriorityUsers"] = array;

        await File.WriteAllTextAsync(path, root.ToJsonString(WriteOptions), ct);
    }
}
