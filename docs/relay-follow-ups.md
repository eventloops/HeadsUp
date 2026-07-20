# Relay — known gaps and follow-ups

The relay works end to end: one Slack app, browser sign-in, per-user routing, live huddle
alerts with resolved names. This is what's left, written down while the reasons are still fresh.

Ordered by what stands between this and handing it to a colleague.

## 1. DM huddles show a raw channel id

**Seen in testing:** `WPF popup shown for huddle in D0BJDDQSSTE`.

Channel huddles resolve correctly (`all-headsuptest`), because the relay looks up
`conversations.info`. DMs deliberately skip that lookup — a DM has no channel name worth
showing — so `ChannelName` falls back to the id.

The fix isn't to look up the DM, it's to say the right thing: a DM huddle is *with a person*,
so it should read "huddle with Sarah Whitfield". The initiator's name is already resolved and
attached; the popup just needs to prefer it over the channel for `is_dm_call` huddles.

Small, contained, and the most visible remaining rough edge.

## 2. Priority people can't be chosen in relay mode

**The headline feature is unconfigurable here.** The dashboard's picker searches an
`IUserDirectory`, and relay mode registers `UnavailableUserDirectory` — which honestly reports
`IsAvailable = false`, because the desktop holds no Slack credential and genuinely cannot
search the workspace itself.

Two pieces are needed:

- **A directory endpoint on the relay.** It already holds a bot token with `users:read`, so it
  can serve the member list. Authenticated with the same session the event socket uses, and
  cached — the member list changes rarely.
- **A relay-backed `IUserDirectory`** on the desktop that calls it.

Worth noting `IsAvailable` is currently declared but **never consumed by any UI**, so today the
picker is silently empty rather than explaining itself. Even before the endpoint exists, the
dashboard should say "not available in relay mode" rather than showing nothing.

## 3. Operational constraints to respect when deploying

None of these are bugs, but all are easy to get wrong.

**The relay must run as a single instance.** Two instances open two Socket Mode connections
with the same app-level token, Slack distributes events arbitrarily across them, and the
connection registry is per-process — so a desktop connected to instance A misses events that
landed on instance B. This is the same failure that makes per-desktop Socket Mode impossible,
reappearing one tier up. Horizontal scale needs a shared bus (Redis pub/sub or similar) between
instances; not built, and not worth building until well past a few thousand users.

**Events during a restart are lost.** Socket Mode does not queue for disconnected apps and
there is no replay. The two overlapping connections cover a connection refresh, not a process
restart. Deploy at quiet times, or build a drain-then-swap path.

**`SessionSigningKey` must be set explicitly.** Unset, it regenerates per start and every
desktop is signed out on each deploy. There's a startup warning, which is easy to miss.

**It is a single point of failure.** Relay down means nobody gets alerts, and from the user's
side it looks like "Reconnecting…" and silence. Monitor `/health`.

## 4. Verified against one workspace, not at scale

What live testing actually covered, so the gaps aren't mistaken for coverage:

| | Status |
|---|---|
| Huddle events over Events API | ✅ Observed (DM and channel) |
| Per-user routing | ✅ 16 events, 1 connection each |
| Name resolution | ✅ Channel names correct |
| Browser sign-in end to end | ✅ First try |
| **Isolation with two installed users** | ❌ Only one account ever authorized |
| **`authorizations` truncation** | ❌ Needs many users; free tier can't produce it |
| **Enterprise Grid** | ❌ `is_enterprise_install` was false throughout |
| **Rate limits under real volume** | ❌ Never approached |

The isolation one matters most. Unit tests pin it — an event authorized for Alice never reaches
Bob's connection — but it has never been *watched* with two real installs. That's a second
account running `authorize` and a DM between them.

## 5. Open question: does the per-user app mode survive?

`SlackApi` (each user creates their own Slack app) was kept as a fallback for when there's no
relay. It works and is tested, but it now carries real cost: a second manifest, a setup dialog,
a separate credential path, and a token-refresh implementation the relay doesn't need.

If the relay is the answer for this company, that mode is maintenance with no user. Worth
deciding deliberately rather than letting it rot — the honest case for keeping it is that one
or two people shouldn't have to run a server, which is a real scenario.

## Not planned

**Storing message content.** The relay forwards and forgets, holds no database, and now
redacts message text from its own logs. That property is worth defending: it makes "the relay
keeps no message history" a sentence that ends a security review rather than starting one.

The direction in [vision.md](vision.md) — project categorisation across Slack, Teams and
Outlook — does need persisted content, and explicitly names that as the scope jump. If that
gets built, this is a decision to revisit deliberately, not to drift into.
