using Microsoft.Extensions.Options;
using HeadsUp.Configuration;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace HeadsUp.Services;

/// <summary>Reads and persists the "Notify me about" alert settings.</summary>
public interface IAlertSettingsStore
{
    /// <summary>Current alert settings (live, from options).</summary>
    AlertOptions Current { get; }

    /// <summary>Persists the alert settings to the local config file.</summary>
    Task SaveAsync(AlertOptions options, CancellationToken ct = default);
}

/// <summary>
/// Persists the <c>Alerts</c> section into the git-ignored <c>appsettings.Local.json</c> beside
/// the app, preserving every other setting. Lands in the watched config file, so
/// <see cref="IOptionsMonitor{T}"/> hot-reloads it - toggles apply with no restart.
/// </summary>
public sealed class JsonAlertSettingsStore : IAlertSettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly IOptionsMonitor<AlertOptions> _options;

    public JsonAlertSettingsStore(IOptionsMonitor<AlertOptions> options) => _options = options;

    public AlertOptions Current => _options.CurrentValue;

    public async Task SaveAsync(AlertOptions options, CancellationToken ct = default)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");

        var root = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        var keywords = new JsonArray();
        foreach (var k in options.KeywordList
                     .Select(k => k?.Trim())
                     .Where(k => !string.IsNullOrEmpty(k))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            keywords.Add(k);

        var muted = new JsonArray();
        foreach (var channel in options.MutedChannels
                     .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                     .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.First()))
            muted.Add(new JsonObject { ["Id"] = channel.Id, ["Name"] = channel.Name });

        root["Alerts"] = new JsonObject
        {
            ["Huddles"] = options.Huddles,
            ["Mentions"] = options.Mentions,
            ["DirectMessages"] = options.DirectMessages,
            ["Keywords"] = options.Keywords,
            ["KeywordList"] = keywords,
            ["MutedChannels"] = muted,
        };

        await File.WriteAllTextAsync(path, root.ToJsonString(WriteOptions), ct);
    }
}
