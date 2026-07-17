using Microsoft.Extensions.Options;
using HeadsUp.Configuration;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace HeadsUp.Services;

/// <summary>Reads and persists the priority-user list, namespaced by connector.</summary>
public interface IPrioritySettingsStore
{
    /// <summary>The current priority user ids for a source (live, from options).</summary>
    IReadOnlyList<string> CurrentUserIds(string sourceId);

    /// <summary>Persists the priority-user id list for a single source, leaving other sources intact.</summary>
    Task SetAsync(string sourceId, IEnumerable<string> userIds, CancellationToken ct = default);
}

/// <summary>
/// Persists priority users into the git-ignored <c>appsettings.Local.json</c> beside the
/// app, under the namespaced <c>Priority:Users</c> section (each entry carries its source).
/// Because it lands in the watched config file, <see cref="IOptionsMonitor{T}"/> hot-reloads
/// it and the change applies with no restart.
/// </summary>
public sealed class JsonPrioritySettingsStore : IPrioritySettingsStore
{
    // A resolver is required to serialize the CLR values we add; without it ToJsonString
    // throws "must specify a TypeInfoResolver".
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly IOptionsMonitor<PriorityOptions> _priorityOptions;

    public JsonPrioritySettingsStore(IOptionsMonitor<PriorityOptions> priorityOptions) =>
        _priorityOptions = priorityOptions;

    public IReadOnlyList<string> CurrentUserIds(string sourceId) =>
        _priorityOptions.CurrentValue.Users
            .Where(u => string.Equals(u.Source, sourceId, StringComparison.OrdinalIgnoreCase))
            .Select(u => u.Id)
            .ToList();

    public async Task SetAsync(string sourceId, IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");

        var root = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        // Keep every other source's priority users untouched; replace only this source's.
        var kept = _priorityOptions.CurrentValue.Users
            .Where(u => !string.Equals(u.Source, sourceId, StringComparison.OrdinalIgnoreCase))
            .Select(u => (u.Source, u.Id));
        var updated = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => (Source: sourceId, Id: id));

        var array = new JsonArray();
        foreach (var (source, id) in kept.Concat(updated))
            array.Add(new JsonObject { ["Source"] = source, ["Id"] = id });

        if (root["Priority"] is not JsonObject priority)
        {
            priority = new JsonObject();
            root["Priority"] = priority;
        }
        priority["Users"] = array;

        await File.WriteAllTextAsync(path, root.ToJsonString(WriteOptions), ct);
    }
}
