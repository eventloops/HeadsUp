# What Slack actually sends — observed, not documented

Captured from a live Socket Mode connection against a free test workspace, July 2026. These
are payloads we saw, not shapes inferred from docs. Credentials removed.

Recorded because several of these contradict the obvious reading of Slack's documentation, and
one of them silently breaks three product features at once.

## 1. `huddle_thread` IS delivered over the Events API ✅

The question this test existed to answer. Slack notes that not every message subtype is
deliverable via the Events API, and Heads-Up's working huddle detection reads the subtype off
the **client RTM stream** — a different mechanism. It was entirely possible the sanctioned
route couldn't see huddles at all.

It can. Both variants arrive:

| Huddle started in | `subtype` | `channel_type` | `room.is_dm_call` |
|---|---|---|---|
| A DM | `huddle_thread` | `im` | `true` |
| A channel | `huddle_thread` | `channel` | `false` |

**The sanctioned path keeps the differentiating feature.** This was the finding that could have
invalidated the whole architecture.

## 2. The huddle initiator is NOT the `user` field ⚠️

The single most dangerous detail here.

```json
{
  "subtype": "huddle_thread",
  "user": "USLACKBOT",          ← NOT the initiator
  "room": { "created_by": "U0BK42E575E" }   ← the actual person
}
```

Every huddle event's `user` is `USLACKBOT`. Reading it breaks three things at once, none of
which look like the same bug:

1. Alerts say **"Slackbot started a huddle"** instead of naming the person
2. **Priority-user matching never fires** — it compares the initiator against your list
3. **Own-huddle suppression never fires** — you get alerted for huddles you started

`SlackWebHuddleSource` (the unofficial connector) already handles this, with a comment saying
so — evidently learned the hard way. `SlackHuddleMonitor` (the sanctioned connector) did not.

**This is unfixable through SlackNet's typed API**: `MessageEvent` has no `room` property, so
the initiator is unreachable. Huddle attribution requires raw JSON. `SlackHuddleParser` now
does this, and is shared.

## 3. `no_notifications: true` on every huddle event

Slack explicitly marks huddle messages as non-notifying — a machine-readable statement of the
exact gap Heads-Up exists to fill.

## 4. `room.huddle_link` is provided

```
https://app.slack.com/huddle/{team}/{channel}
```

Prefer it over constructing a link ourselves; it's what Slack's own client uses.

## 5. Huddle *end* arrives as edit + delete, not a distinct event

When a huddle ends, Slack sends `message_changed` and then `message_deleted`. Both wrap the
original message, whose **nested** subtype is still `huddle_thread`, with `has_ended: true` and
often `was_missed: true`.

A parser matching `huddle_thread` anywhere in the payload would fire an alert **when the huddle
is already over**. Only a *top-level* `huddle_thread` is a start.

## 6. `apps.event.authorizations.list` rejects user tokens

```
SlackException: not_allowed_token_type
```

It needs the **app-level** token, not the user token. The relay's resolver degraded gracefully
to the inline list, which is the designed behaviour and meant events still routed — under-
delivery rather than misdelivery — but the call was failing on every single event.

## 7. `authorizations` names the *recipient*, not the sender

For a DM sent by `U0BK42E575E`, the array contained only `U0BJ7NBPHHU` — the person who can
see it. This is the routing model working as intended.

**Caveat:** only one account had authorized the app when this was captured, so we have *not*
verified what a DM between two installed users looks like. It should list both, and the relay
would deliver to both — correct, since both parties can see their own DM — but that is
currently reasoned, not observed.

## 8. Rotating tokens are real

`token_rotation_enabled` in the manifest produces `xoxe.…` access tokens with a ~12 hour
expiry and a refresh token. The desktop handles renewal; **the relay currently does not** — it
treats `ApiToken` as static config, so it will stop expanding authorizations after ~12 hours.
Outstanding.

## 9. Every event payload contains a credential

Slack includes the app's verification token in the `token` field of every event. Logging frames
verbatim writes a credential to disk on every message — which our raw-frame diagnostic did
until it was fixed to redact.

## Still unverified

- **Two installed users on one DM** (see 7)
- **`authorizations` truncation** — needs many users; free tier can't produce it
- **Enterprise Grid** — `is_enterprise_install` was `false` throughout
- **Group huddles** — paid plans only; may differ in shape
