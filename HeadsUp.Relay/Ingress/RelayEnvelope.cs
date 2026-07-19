using HeadsUp.Relay.Routing;
using HeadsUp.Services;

namespace HeadsUp.Relay.Ingress;

/// <summary>What the relay needs from one frame: what to acknowledge, and what to route.</summary>
/// <param name="EnvelopeId">Acknowledged regardless of whether the event is routable.</param>
/// <param name="Event">The routable event, or null for frames the relay doesn't forward.</param>
public sealed record ParsedEnvelope(string? EnvelopeId, SlackRelayEvent? Event);

/// <summary>
/// Adapts a parsed Socket Mode envelope into something routable.
///
/// The envelope parsing itself is shared with the desktop connector; what's relay-specific is
/// the decision about recipients. An event with neither authorizations nor an event_context is
/// dropped: forwarding it could only ever be a guess about who may see it.
/// </summary>
public static class RelayEnvelope
{
    public static ParsedEnvelope Parse(string json)
    {
        var envelope = SlackEnvelopeParser.Parse(json);

        if (envelope.EventJson is null)
            return new ParsedEnvelope(envelope.EnvelopeId, null);

        // No recipients and no way to look them up.
        if (envelope.Authorizations.Count == 0 && string.IsNullOrEmpty(envelope.EventContext))
            return new ParsedEnvelope(envelope.EnvelopeId, null);

        // Only the inner event is forwarded. The envelope's own metadata — other people's
        // authorizations, the app's token — is routing machinery with no business on a desktop.
        return new ParsedEnvelope(
            envelope.EnvelopeId,
            new SlackRelayEvent(envelope.EventJson, envelope.Authorizations, envelope.EventContext));
    }
}
