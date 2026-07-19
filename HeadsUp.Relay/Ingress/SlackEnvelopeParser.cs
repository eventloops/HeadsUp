using System.Text.Json;

namespace HeadsUp.Relay.Ingress;

using HeadsUp.Relay.Routing;

/// <summary>One parsed Socket Mode envelope: what to acknowledge, and what (if anything) to route.</summary>
/// <param name="EnvelopeId">Must be acknowledged, whether or not the event is routable.</param>
/// <param name="Event">The routable event, or null for envelopes we don't forward (hello, disconnect, non-message events).</param>
public sealed record ParsedEnvelope(string? EnvelopeId, SlackRelayEvent? Event);

/// <summary>
/// Turns a raw Socket Mode frame into something routable.
///
/// Kept pure and separate from the socket so the part that decides <i>who may see an event</i>
/// can be tested exhaustively against real payload shapes, rather than only against a live
/// connection. Everything security-relevant about ingress happens here: if this extracted
/// authorizations wrongly, the router downstream would faithfully deliver to the wrong people.
/// </summary>
public static class SlackEnvelopeParser
{
    /// <summary>
    /// Parses one frame. Malformed input yields an envelope with no event rather than throwing —
    /// a frame we can't understand must not take the ingress down, and unacknowledged envelopes
    /// make Slack redeliver.
    /// </summary>
    public static ParsedEnvelope Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new ParsedEnvelope(null, null);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var envelopeId = GetString(root, "envelope_id");

            // Only events_api envelopes carry user events; hello/disconnect are lifecycle frames.
            if (GetString(root, "type") != "events_api" ||
                !root.TryGetProperty("payload", out var payload))
                return new ParsedEnvelope(envelopeId, null);

            var authorizations = ReadAuthorizations(payload);
            var eventContext = GetString(payload, "event_context");

            // No recipients and no way to look them up: forwarding this could only ever be a
            // guess, so drop it. Slack still gets its acknowledgement.
            if (authorizations.Count == 0 && string.IsNullOrEmpty(eventContext))
                return new ParsedEnvelope(envelopeId, null);

            if (!payload.TryGetProperty("event", out var innerEvent))
                return new ParsedEnvelope(envelopeId, null);

            // Forward the inner event only. The envelope's own metadata (other people's
            // authorizations, tokens) is routing machinery and has no business on a desktop.
            var relayEvent = new SlackRelayEvent(innerEvent.GetRawText(), authorizations, eventContext);
            return new ParsedEnvelope(envelopeId, relayEvent);
        }
    }

    /// <summary>
    /// Reads the user ids from <c>authorizations</c>, skipping bot installations. Slack
    /// truncates this list for widely-visible events, which is why <c>event_context</c> is
    /// carried alongside it.
    /// </summary>
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

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Blanks credential-bearing fields so a frame can be logged safely.
    ///
    /// Slack puts the app's verification token in the <c>token</c> field of every single event
    /// payload, so logging frames verbatim writes a credential to disk on every message. The
    /// diagnostic is genuinely useful, so redact rather than drop it.
    /// </summary>
    public static string Redact(string json) =>
        SecretFields.Replace(json, @"""$1"":""[redacted]""");

    private static readonly System.Text.RegularExpressions.Regex SecretFields = new(
        @"""(token|access_token|refresh_token|bot_token|app_token|verification_token|client_secret|signing_secret)""\s*:\s*""[^""]*""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
}
