# Setting up a test Slack workspace

Everything built for the sanctioned path so far is verified against stubs and documented
payload shapes — never against Slack itself. This is how to close that gap cheaply.

A free workspace is enough. Two accounts are required, because the single most important thing
to prove is that one person's messages **don't** reach the other's client, and that needs two
people.

## The question to answer first

**Does Slack deliver `huddle_thread` over the Events API?**

Heads-Up's working huddle detection reads that subtype off the **client RTM stream** — the
unofficial `xoxc` path. The Events API is a different delivery mechanism, and Slack notes that
not all message subtypes are deliverable through it. Nothing built so far proves huddle starts
arrive over Socket Mode.

This matters more than any architectural decision: huddle alerting is the feature Heads-Up
exists for, since Slack itself doesn't notify you. If the subtype never arrives, the sanctioned
route can still do mentions, DMs and keywords, but loses the differentiator — and that's worth
knowing before building anything else on top.

Answer it before the desktop connector, not after.

## What free tier gives you

| Need | Free tier | Enough? |
|---|---|---|
| Slack app + app-level token | Yes | Yes |
| Socket Mode | Yes | Yes |
| PKCE / Sign in with Slack | Yes | Yes |
| Huddles | 1:1 only, 30 min | **Yes** — two accounts is what we need anyway |
| Two users, DMs, private channels | Yes | Yes |
| `apps.event.authorizations.list` truncation | Only ever 2 users | Partially — see below |
| Enterprise Grid / org-wide install | No | No, but single-workspace install proves the mechanism |

The truncation path is the weak spot. Slack only truncates `authorizations` when an event is
visible to many people, so with two accounts the inline list is unlikely to truncate and the
expansion code won't be genuinely exercised. Unit tests cover the logic; just don't read a
green test workspace as proof that path works at scale.

## Setup

1. **Create a free workspace** at `slack.com/get-started`. Any email.
2. **Invite a second account** (a second address, or a `+alias`). Both need to be real
   sign-ins, since huddles need two live participants.
3. **Create the app** from [the manifest](slack-app-manifest.json) — the prefilled link in
   `SlackAppSetupViewModel.CreateAppUrl` works here too.
4. **Generate an app-level token**: Basic Information → App-Level Tokens → Generate, with
   **both** of these scopes:
   - **`connections:write`** — Socket Mode won't open without it
   - **`authorizations:read`** — needed to expand a truncated authorizations list

   Scopes can't be added to an existing token and the manifest can't set them, so getting this
   wrong means generating a new one. Missing the second scope fails *partially*: the relay
   connects and routes, but events visible to many people reach only the one user Slack names
   inline.
5. **Install to the workspace** and have *both* accounts authorize, so there are two
   installations to route between.

## Running the probe

Put the values in `HeadsUp.Relay/appsettings.Local.json` (git-ignored):

```json
{
  "Relay": {
    "AppToken": "xapp-...",
    "ClientId": "1234567890.1234567890",
    "ApiToken": "xoxb-... or xoxp-...",
    "LogRawFrames": true
  }
}
```

```
dotnet run --project HeadsUp.Relay
```

`LogRawFrames` logs every Socket Mode frame verbatim, so the relay doubles as a probe — the
same trick `SlackWebHuddleSource` uses for the unofficial path.

Then, in the test workspace:

| Do this | Watch for |
|---|---|
| Send a channel message | `message` event, `authorizations` naming who can see it |
| DM the other account | `message` in a `D…` channel, authorized for **one** user only |
| **Start a 1:1 huddle** | **a `huddle_thread` subtype — or its absence, which is the finding** |
| Mention the other user | `message` containing `<@U…>` |

## What to check in the output

1. **Does `huddle_thread` appear at all?** The whole point.
2. **Is a DM authorized for exactly one user?** If a DM between A and B lists both, the
   isolation model needs rethinking — though per Slack's docs it shouldn't.
3. **Is `event_context` present?** The expansion path depends on it.
4. **Is `authorizations` ever absent?** Such events are dropped as unroutable, by design.

## Things this can't tell you

- **Behaviour at scale.** Two users won't trigger truncation, rate limits or fan-out cost.
- **Enterprise Grid.** `is_enterprise_install` and org-wide install are Grid-only.
- **Group huddles.** Paid-only; if group huddles emit a different shape, free tier won't show it.

## Note on the account

Create the workspace and accounts yourself — sign-up needs credentials, which is not something
to hand off. Once `appsettings.Local.json` exists, everything above is runnable.
