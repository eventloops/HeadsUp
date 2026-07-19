using System.Text.Json;

namespace HeadsUp.Services;

/// <summary>Facts about a huddle-start event, read from the raw Slack message JSON.</summary>
/// <param name="InitiatorUserId">Who actually started it — <c>room.created_by</c>, not <c>user</c>.</param>
/// <param name="ChannelId">The conversation the huddle belongs to.</param>
/// <param name="IsDirectMessage">True for a 1:1 huddle (<c>room.is_dm_call</c>).</param>
/// <param name="HuddleLink">Slack's own deep link, when present.</param>
/// <param name="HasEnded">True once the huddle is over; such events are not alertable.</param>
public sealed record HuddleFacts(
    string? InitiatorUserId,
    string? ChannelId,
    bool IsDirectMessage,
    string? HuddleLink,
    bool HasEnded);

/// <summary>
/// Reads huddle-start details out of a raw Slack message event.
///
/// This exists because of one wire-format trap, confirmed against live Socket Mode traffic:
/// a <c>huddle_thread</c> message's <c>user</c> field is <b>USLACKBOT</b>, not the person who
/// started the huddle. The initiator is only in <c>room.created_by</c>. Reading <c>user</c>
/// instead would attribute every huddle to Slackbot, which in turn breaks priority-user
/// matching and the suppression of your own huddles — three product failures from one field.
///
/// It works on raw JSON rather than a typed event deliberately: SlackNet's <c>MessageEvent</c>
/// has no <c>room</c> property, so the initiator is simply unreachable through the typed API.
/// </summary>
public static class SlackHuddleParser
{
    /// <summary>
    /// Extracts the facts, or null when this isn't an alertable huddle start.
    /// </summary>
    public static HuddleFacts? Parse(JsonElement message)
    {
        // Only a top-level huddle_thread is a *start*. Slack also emits message_changed and
        // message_deleted wrappers when a huddle ends, whose nested message still carries the
        // huddle_thread subtype — alerting on those would fire when a huddle is over.
        var subtype = GetString(message, "subtype");
        if (!SlackMessageParser.IsHuddleSubtype(subtype))
            return null;

        var channelId = GetString(message, "channel");

        if (!message.TryGetProperty("room", out var room) || room.ValueKind != JsonValueKind.Object)
        {
            // Legacy calls (sh_room_created) may not carry a room object; fall back to user,
            // accepting that attribution may be wrong rather than dropping the alert.
            return new HuddleFacts(GetString(message, "user"), channelId, IsIm(message), null, false);
        }

        var hasEnded = room.TryGetProperty("has_ended", out var ended) && ended.ValueKind == JsonValueKind.True;

        return new HuddleFacts(
            InitiatorUserId: GetString(room, "created_by") ?? GetString(message, "user"),
            ChannelId: channelId,
            IsDirectMessage: room.TryGetProperty("is_dm_call", out var dm) && dm.ValueKind == JsonValueKind.True
                             || IsIm(message),
            HuddleLink: GetString(room, "huddle_link"),
            HasEnded: hasEnded);
    }

    private static bool IsIm(JsonElement message) =>
        GetString(message, "channel_type") == "im";

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
