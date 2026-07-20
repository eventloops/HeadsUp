using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SlackNet;

namespace HeadsUp.Relay.Routing;

using HeadsUp.Services;

/// <summary>
/// Adds display names to events before they are forwarded.
///
/// Necessary because in relay mode the desktop holds no Slack credential at all — that is the
/// point of the mode — so it cannot turn <c>U0BK42E575E</c> into a person or
/// <c>C0BK42DHNU8</c> into a channel. Without this, every relay-mode popup reads "U0BK42E575E
/// started a huddle", which is not a shippable alert.
///
/// The relay is the right place for it: one lookup here serves every desktop, whereas each
/// desktop resolving independently would multiply the same calls by the number of people
/// online. It needs a bot token — the app-level token that opens Socket Mode cannot call
/// <c>users.info</c>. Without one configured, enrichment is skipped and the desktop falls back
/// to ids rather than failing.
///
/// Names are added under a <c>_headsup</c> key rather than rewritten into Slack's own fields,
/// so the forwarded payload stays a faithful Slack event and older desktops keep working.
/// </summary>
public sealed class EventEnricher
{
    /// <summary>Names change rarely; this is about not re-asking within a burst of messages.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// Failures are cached far more briefly than successes: long enough to stop a per-message
    /// retry storm, short enough that a rate limit or a transient error recovers on its own.
    /// </summary>
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(2);

    private readonly ISlackApiClient? _api;
    private readonly ILogger<EventEnricher> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry<UserName>> _users = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<string>> _channels = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public EventEnricher(ISlackApiClient? api, ILogger<EventEnricher> logger, TimeProvider? clock = null)
    {
        _api = api;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True when a bot token was configured and lookups are possible.</summary>
    public bool IsEnabled => _api is not null;

    /// <summary>
    /// Returns the event with names attached, or unchanged when enrichment is off or fails.
    /// A failure here must never cost the alert — an alert naming an id still interrupts the
    /// right person at the right moment.
    /// </summary>
    public async Task<string> EnrichAsync(string eventJson, CancellationToken ct)
    {
        if (_api is null)
            return eventJson;

        try
        {
            var node = JsonNode.Parse(eventJson);
            if (node is not JsonObject message)
                return eventJson;

            var enrichment = new JsonObject();

            // The huddle initiator is room.created_by, not user — user is USLACKBOT on every
            // huddle event.
            var userId = message["room"]?["created_by"]?.GetValue<string>()
                         ?? message["user"]?.GetValue<string>();
            var channelId = message["channel"]?.GetValue<string>();

            // Concurrently: these are independent lookups on the delivery path, and running
            // them in series doubled the latency added to any alert whose ids aren't cached.
            var userTask = string.IsNullOrEmpty(userId)
                ? Task.FromResult<UserName?>(null)
                : ResolveUserAsync(userId, ct);

            var channelTask = string.IsNullOrEmpty(channelId)
                ? Task.FromResult<string?>(null)
                : ResolveChannelAsync(channelId, ct);

            await Task.WhenAll(userTask, channelTask);

            if (await userTask is { } who)
            {
                enrichment["user_name"] = who.DisplayName;
                if (who.AvatarUrl is not null)
                    enrichment["user_avatar"] = who.AvatarUrl;
            }

            if (await channelTask is { } channelName)
                enrichment["channel_name"] = channelName;

            if (enrichment.Count == 0)
                return eventJson;

            message["_headsup"] = enrichment;
            return message.ToJsonString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not enrich an event; forwarding it with ids.");
            return eventJson;
        }
    }

    private async Task<UserName?> ResolveUserAsync(string userId, CancellationToken ct)
    {
        if (TryGet(_users, userId, out var cached))
            return cached;

        try
        {
            var user = await _api!.Users.Info(userId, cancellationToken: ct);
            var name = FirstNonEmpty(user?.Profile?.DisplayName, user?.Profile?.RealName, user?.Name) ?? userId;
            var avatar = FirstNonEmpty(user?.Profile?.Image72, user?.Profile?.Image192);

            var resolved = new UserName(name, avatar);
            _users[userId] = new CacheEntry<UserName>(resolved, _clock.GetUtcNow() + CacheTtl);
            return resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cached as a failure, briefly. An id that consistently fails — a deactivated
            // account, or a rate limit — otherwise costs an API call on every single message
            // from that source, which is exactly when the calls are least affordable.
            _users[userId] = new CacheEntry<UserName>(null, _clock.GetUtcNow() + FailureTtl);
            LogLookupFailure(ex, "user", userId);
            return null;
        }
    }

    private async Task<string?> ResolveChannelAsync(string channelId, CancellationToken ct)
    {
        // A DM has no name worth showing, and conversations.info on one costs a call for nothing.
        if (channelId.StartsWith('D'))
            return null;

        if (TryGet(_channels, channelId, out var cached))
            return cached;

        try
        {
            var conversation = await _api!.Conversations.Info(channelId, cancellationToken: ct);
            var name = conversation?.Name;
            if (string.IsNullOrEmpty(name))
                return null;

            _channels[channelId] = new CacheEntry<string>(name, _clock.GetUtcNow() + CacheTtl);
            return name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _channels[channelId] = new CacheEntry<string>(null, _clock.GetUtcNow() + FailureTtl);
            LogLookupFailure(ex, "channel", channelId);
            return null;
        }
    }

    /// <summary>
    /// Rate limiting is worth saying out loud: it degrades every alert to raw ids, and a Debug
    /// line indistinguishable from "no such user" is how that goes unnoticed.
    /// </summary>
    private void LogLookupFailure(Exception ex, string kind, string id)
    {
        if (ex.Message.Contains("ratelimited", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("rate_limited", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Slack rate-limited a {Kind} lookup; alerts will name raw ids until it clears.", kind);
            return;
        }

        _logger.LogDebug(ex, "Could not resolve {Kind} {Id}.", kind, id);
    }

    /// <summary>
    /// A hit, including a cached failure (null value). Distinguishing "cached as failed" from
    /// "absent" is what makes negative caching work.
    /// </summary>
    private bool TryGet<T>(ConcurrentDictionary<string, CacheEntry<T>> cache, string key, out T? value)
        where T : class
    {
        if (cache.TryGetValue(key, out var entry) && entry.ExpiresAt > _clock.GetUtcNow())
        {
            value = entry.Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Drops expired entries. Expiry is enforced on read, so this exists only to stop the maps
    /// growing without bound: a relay running for months would otherwise retain every id it has
    /// ever seen, including people and channels long gone.
    /// </summary>
    public int Prune()
    {
        var now = _clock.GetUtcNow();
        var removed = 0;

        foreach (var (key, entry) in _users)
            if (entry.ExpiresAt <= now && _users.TryRemove(key, out _)) removed++;

        foreach (var (key, entry) in _channels)
            if (entry.ExpiresAt <= now && _channels.TryRemove(key, out _)) removed++;

        return removed;
    }

    internal int CachedCount => _users.Count + _channels.Count;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private sealed record UserName(string DisplayName, string? AvatarUrl);

    /// <summary>A null <paramref name="Value"/> is a cached failure, not an absent entry.</summary>
    private sealed record CacheEntry<T>(T? Value, DateTimeOffset ExpiresAt) where T : class;
}
