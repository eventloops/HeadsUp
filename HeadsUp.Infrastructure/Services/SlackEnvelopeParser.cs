using System.Text.Json;

namespace HeadsUp.Services;

/// <summary>One parsed Socket Mode envelope.</summary>
/// <param name="EnvelopeId">Must be acknowledged, whether or not the event is usable.</param>
/// <param name="EventJson">The inner event, verbatim, or null for lifecycle frames.</param>
/// <param name="Authorizations">
/// User ids from <c>authorizations</c>. Slack <b>truncates</b> this to a single entry for
/// widely-visible events, so it is not safe to treat as complete — see <paramref name="EventContext"/>.
/// Only the relay cares: a per-user app receives only its own user's events.
/// </param>
/// <param name="EventContext">Handle for expanding a truncated authorizations list.</param>
public sealed record SlackEnvelope(
    string? EnvelopeId,
    string? EventJson,
    IReadOnlyList<string> Authorizations,
    string? EventContext);

/// <summary>
/// Turns a raw Socket Mode frame into its inner event.
///
/// Shared by the relay and the desktop's Slack-app connector because both consume raw frames
/// rather than SlackNet's typed events — the typed <c>MessageEvent</c> has no <c>room</c>
/// property, which makes a huddle's initiator unreachable through it.
/// </summary>
public static class SlackEnvelopeParser
{
    /// <summary>
    /// Parses one frame. Malformed input yields an envelope with no event rather than throwing:
    /// a frame we can't read must not take the connection down for everything after it.
    /// </summary>
    public static SlackEnvelope Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new SlackEnvelope(null, null, [], null);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var envelopeId = GetString(root, "envelope_id");

            // Only events_api envelopes carry user events; hello/disconnect are lifecycle frames.
            if (GetString(root, "type") != "events_api" ||
                !root.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("event", out var innerEvent))
                return new SlackEnvelope(envelopeId, null, [], null);

            return new SlackEnvelope(
                envelopeId,
                innerEvent.GetRawText(),
                ReadAuthorizations(payload),
                GetString(payload, "event_context"));
        }
    }

    /// <summary>Reads user ids from <c>authorizations</c>, skipping bot installations.</summary>
    private static IReadOnlyList<string> ReadAuthorizations(JsonElement payload)
    {
        if (!payload.TryGetProperty("authorizations", out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return [];

        var userIds = new List<string>();
        foreach (var authorization in array.EnumerateArray())
        {
            if (authorization.ValueKind != JsonValueKind.Object)
                continue;

            if (authorization.TryGetProperty("is_bot", out var isBot) &&
                isBot.ValueKind == JsonValueKind.True)
                continue;

            if (GetString(authorization, "user_id") is { Length: > 0 } userId)
                userIds.Add(userId);
        }

        return userIds;
    }

    /// <summary>
    /// Blanks credentials <b>and message content</b> so a frame can be logged safely.
    ///
    /// Two different problems, both of which end with something on disk that shouldn't be:
    ///
    /// <list type="bullet">
    /// <item>Slack puts the app's verification token in the <c>token</c> field of every event
    /// payload, so logging frames verbatim writes a credential on every message.</item>
    /// <item>The payload also carries what people actually said. The relay is designed to hold
    /// no message history — it forwards and forgets — and a debug log that quietly accumulates
    /// message text on the server contradicts that, whatever the database does.</item>
    /// </list>
    ///
    /// Everything diagnostically useful survives: types, subtypes, ids, authorizations,
    /// timestamps. Only the words are removed, and the length is kept so "was there text?"
    /// remains answerable.
    /// </summary>
    public static string Redact(string json) =>
        MessageText.Replace(
            SecretFields.Replace(json, @"""$1"":""[redacted]"""),
            m => $"\"{m.Groups[1].Value}\":\"[{m.Groups[2].Value.Length} chars redacted]\"");

    private static readonly System.Text.RegularExpressions.Regex SecretFields = new(
        @"""(token|access_token|refresh_token|bot_token|app_token|verification_token|client_secret|signing_secret)""\s*:\s*""[^""]*""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Human-authored content: the message body, and the rich-text blocks that repeat it.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex MessageText = new(
        @"""(text|preview|fallback)""\s*:\s*""((?:[^""\\]|\\.)*)""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
