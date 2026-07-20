using HeadsUp.Relay.Ingress;
using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Relay.Tests;

/// <summary>
/// Raw-frame logging is a useful diagnostic and a liability: Slack includes the app's
/// verification token in the payload of every event, so logging frames verbatim writes a
/// credential to disk on every message that arrives.
/// </summary>
public class RedactionTests
{
    [Fact]
    public void The_verification_token_slack_sends_on_every_event_is_removed()
    {
        var frame = """{"payload":{"token":"YXiP7p3Xdtt","team_id":"T1","event":{"text":"hi"}}}""";

        var redacted = SlackEnvelopeParser.Redact(frame);

        Assert.DoesNotContain("YXiP7p3Xdtt", redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Theory]
    [InlineData("access_token", "xoxe.xoxp-1-secret")]
    [InlineData("refresh_token", "xoxe-1-secret")]
    [InlineData("client_secret", "7cd34101d6c45")]
    [InlineData("signing_secret", "abc123def")]
    [InlineData("app_token", "xapp-1-secret")]
    public void Other_credential_fields_are_removed_too(string field, string secret)
    {
        var redacted = SlackEnvelopeParser.Redact($$"""{"{{field}}":"{{secret}}"}""");

        Assert.DoesNotContain(secret, redacted);
    }

    // ------------------------------------------------------------------
    // Message content
    // ------------------------------------------------------------------

    [Fact]
    public void What_people_said_is_not_written_to_disk()
    {
        // The relay is designed to hold no message history. A debug log quietly accumulating
        // message text on the server contradicts that, whatever the database does.
        var frame = """{"event":{"type":"message","user":"U1","text":"the merger closes friday"}}""";

        var redacted = SlackEnvelopeParser.Redact(frame);

        Assert.DoesNotContain("merger", redacted);
        Assert.DoesNotContain("friday", redacted);
    }

    [Fact]
    public void The_length_survives_so_was_there_text_stays_answerable()
    {
        var redacted = SlackEnvelopeParser.Redact("""{"text":"hello"}""");

        Assert.Contains("5 chars redacted", redacted);
    }

    [Fact]
    public void Rich_text_copies_of_the_message_are_redacted_too()
    {
        // Slack repeats the body inside blocks; redacting only the top-level text would leave
        // a verbatim copy sitting right beside it.
        var frame = """
            {"event":{"text":"secret plan","blocks":[{"type":"rich_text","elements":[
              {"type":"rich_text_section","elements":[{"type":"text","text":"secret plan"}]}]}]}}
            """;

        Assert.DoesNotContain("secret plan", SlackEnvelopeParser.Redact(frame));
    }

    [Fact]
    public void An_empty_message_body_is_handled()
    {
        // Huddle events carry "text":"" — the redaction must not mangle them.
        var redacted = SlackEnvelopeParser.Redact("""{"subtype":"huddle_thread","text":""}""");

        Assert.Contains("huddle_thread", redacted);
        Assert.Contains("0 chars redacted", redacted);
    }

    [Fact]
    public void Escaped_quotes_inside_a_message_do_not_break_redaction()
    {
        var frame = """{"text":"he said \"ship it\" on friday"}""";

        var redacted = SlackEnvelopeParser.Redact(frame);

        Assert.DoesNotContain("ship it", redacted);
        Assert.DoesNotContain("friday", redacted);
    }

    [Fact]
    public void Everything_diagnostically_useful_survives()
    {
        // Redaction is worthless if it destroys the reason for logging in the first place.
        var frame = """
            {"payload":{"token":"secret","event":{"type":"message","subtype":"huddle_thread",
            "user":"U1","channel":"C1"},"authorizations":[{"user_id":"U2"}],"event_context":"ctx"}}
            """;

        var redacted = SlackEnvelopeParser.Redact(frame);

        Assert.DoesNotContain("secret", redacted);
        Assert.Contains("huddle_thread", redacted);
        Assert.Contains("U1", redacted);
        Assert.Contains("authorizations", redacted);
        Assert.Contains("ctx", redacted);
    }

    [Fact]
    public void A_field_merely_containing_token_in_its_name_is_left_alone()
    {
        // "token_count" is not a credential; over-redacting would blind the diagnostic.
        var redacted = SlackEnvelopeParser.Redact("""{"token_count":42}""");

        Assert.Contains("42", redacted);
    }

    [Fact]
    public void Redaction_survives_whitespace_and_casing_variations()
    {
        var redacted = SlackEnvelopeParser.Redact("""{"Token" : "secret-value"}""");

        Assert.DoesNotContain("secret-value", redacted);
    }
}
