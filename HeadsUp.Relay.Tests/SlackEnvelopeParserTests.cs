using HeadsUp.Relay.Ingress;
using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Relay.Tests;

/// <summary>
/// Ingress decides who an event is <i>for</i>. Get it wrong and the router downstream will
/// faithfully deliver someone's direct messages to the wrong desktop, so these tests work
/// through real Socket Mode payload shapes rather than trusting the happy path.
/// </summary>
public class SlackEnvelopeParserTests
{
    private const string DmEnvelope = """
        {
          "envelope_id": "env-1",
          "type": "events_api",
          "payload": {
            "type": "event_callback",
            "event_context": "ctx-1",
            "authorizations": [ { "user_id": "U_ALICE", "is_bot": false } ],
            "event": { "type": "message", "user": "U_BOB", "channel": "D123", "text": "hi" }
          }
        }
        """;

    // ------------------------------------------------------------------
    // Recipients
    // ------------------------------------------------------------------

    [Fact]
    public void A_direct_message_is_addressed_to_its_authorized_user()
    {
        var parsed = RelayEnvelope.Parse(DmEnvelope);

        Assert.Equal("env-1", parsed.EnvelopeId);
        Assert.NotNull(parsed.Event);
        Assert.Equal(["U_ALICE"], parsed.Event.Authorizations);
        Assert.Equal("ctx-1", parsed.Event.EventContext);
    }

    [Fact]
    public void Bot_authorizations_are_not_treated_as_recipients()
    {
        var json = """
            {"envelope_id":"e","type":"events_api","payload":{
              "event_context":"ctx",
              "authorizations":[{"user_id":"U_ALICE","is_bot":false},{"user_id":"B_BOT","is_bot":true}],
              "event":{"type":"message"}}}
            """;

        var parsed = RelayEnvelope.Parse(json);

        Assert.Equal(["U_ALICE"], parsed.Event!.Authorizations);
    }

    [Fact]
    public void An_envelope_with_neither_authorizations_nor_context_is_dropped()
    {
        // Nobody to deliver to and no way to find out: forwarding could only be a guess.
        var json = """
            {"envelope_id":"e","type":"events_api","payload":{"event":{"type":"message"}}}
            """;

        var parsed = RelayEnvelope.Parse(json);

        Assert.Null(parsed.Event);
        Assert.Equal("e", parsed.EnvelopeId); // still acknowledged
    }

    [Fact]
    public void An_event_context_alone_is_enough_to_route()
    {
        // Truncation can leave the inline list empty; the context is how it gets expanded.
        var json = """
            {"envelope_id":"e","type":"events_api","payload":{
              "event_context":"ctx","authorizations":[],"event":{"type":"message"}}}
            """;

        var parsed = RelayEnvelope.Parse(json);

        Assert.NotNull(parsed.Event);
        Assert.Equal("ctx", parsed.Event.EventContext);
    }

    // ------------------------------------------------------------------
    // What gets forwarded
    // ------------------------------------------------------------------

    [Fact]
    public void Only_the_inner_event_is_forwarded()
    {
        var parsed = RelayEnvelope.Parse(DmEnvelope);

        // Envelope metadata is routing machinery; a desktop has no business seeing other
        // people's authorizations or the app's tokens.
        Assert.DoesNotContain("authorizations", parsed.Event!.Payload);
        Assert.DoesNotContain("envelope_id", parsed.Event.Payload);
        Assert.Contains("\"user\": \"U_BOB\"", parsed.Event.Payload);
    }

    [Fact]
    public void A_huddle_event_survives_intact()
    {
        var json = """
            {"envelope_id":"e","type":"events_api","payload":{
              "event_context":"ctx","authorizations":[{"user_id":"U_ALICE"}],
              "event":{"type":"message","subtype":"huddle_thread","user":"U_BOB","channel":"C1"}}}
            """;

        var parsed = RelayEnvelope.Parse(json);

        Assert.Contains("huddle_thread", parsed.Event!.Payload);
    }

    // ------------------------------------------------------------------
    // Frames we don't route, but must still acknowledge
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""{"type":"hello","num_connections":1}""")]
    [InlineData("""{"envelope_id":"e","type":"disconnect","reason":"refresh_requested"}""")]
    public void Lifecycle_frames_yield_no_event(string json)
    {
        var parsed = RelayEnvelope.Parse(json);

        Assert.Null(parsed.Event);
    }

    [Fact]
    public void A_disconnect_frame_still_reports_its_envelope_id()
    {
        var parsed = RelayEnvelope.Parse("""{"envelope_id":"e9","type":"disconnect"}""");

        Assert.Equal("e9", parsed.EnvelopeId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"type\":\"events_api\"}")]                       // no payload
    [InlineData("{\"type\":\"events_api\",\"payload\":{}}")]        // no event
    public void Malformed_frames_are_survivable(string json)
    {
        // An unparseable frame must not take ingress down for everyone else.
        var parsed = RelayEnvelope.Parse(json);

        Assert.Null(parsed.Event);
    }

    [Fact]
    public void A_malformed_authorization_entry_is_skipped_not_fatal()
    {
        var json = """
            {"envelope_id":"e","type":"events_api","payload":{
              "event_context":"ctx",
              "authorizations":["not-an-object",{"user_id":"U_ALICE"},{"no_user_id":true}],
              "event":{"type":"message"}}}
            """;

        var parsed = RelayEnvelope.Parse(json);

        Assert.Equal(["U_ALICE"], parsed.Event!.Authorizations);
    }
}
