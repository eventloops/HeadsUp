# Connectors — future options

Heads-Up's detection pipeline is now connector-based: each connector watches one chat
service and feeds the same alert pipeline. Slack is the first and, today, the only one.
This doc sketches what a **second** connector would take and the candidates worth
considering.

> ⚠️ **Not researched.** Everything below is from general knowledge of these platforms,
> not a current investigation. Treat effort/feasibility notes as starting hypotheses to
> verify before committing — vendor APIs (especially Microsoft Graph) move, and the
> details that decide "easy vs painful" are exactly the ones that change. Nothing here is
> a promise that a given connector is viable.

## What a connector has to implement

The seam is small and deliberately mechanical. A new connector is a self-contained set of
pieces, wired the same way the Slack connector is:

- **`IAlertSource`** — a background service that connects to the platform and raises
  `AlertRaised` with an `Alert`. It exposes a stable `SourceId` (e.g. `"teams"`) and a
  display `SourceName` (e.g. `"Teams"`). The coordinator fans in over every registered
  source and stamps each alert with that identity.
- **A message parser** — the platform-specific bit that turns a raw message into the
  normalized facts the shared `AlertClassifier.Classify(...)` needs: *is this a mention of
  me*, *is this a DM*, and a plain-text preview. See `SlackMessageParser` as the template;
  the classifier's mention→DM→keyword policy and muted/keyword rules are already shared.
- **`IAlertLinkResolver`** — builds deep links to open an alert in the platform's app
  (Slack's `slack://…`). `AlertLinks` dispatches by `SourceId` and always falls back to the
  raw link, so this is optional-but-nice.
- **Per-source DND (optional)** — call `SnoozeService.SetSourceDnd(sourceId, name, until)`
  if the platform exposes a "do not disturb / focus" state, so Heads-Up honors it.
- **Status** — update the connector's `SourceStatus` (via `AppStatus.GetOrAdd`) for the
  dashboard's per-source card.
- **Registration** — an `Add<Name>Detection` DI extension that registers the source as both
  `IAlertSource` and `IHostedService` (+ its link resolver), then a case in
  `ServiceCollectionExtensions.AddConnector` keyed on `ConnectorOptions.Type`. Users opt in
  by adding `{ "Type": "<Name>" }` to `Detection:Connectors`.

Settings are already namespaced by source (`MutedChannel.Source`, `Priority:Users` as
`{ Source, Id }`), so ids from different connectors can't collide.

The two things a connector must answer for its platform:

1. **How does it see the user's activity in real time?** A pushed stream (WebSocket/gateway)
   is ideal; polling is a fallback that costs latency and rate limit.
2. **What are the auth mechanics, and do they need an admin?** This usually decides whether
   a connector is shippable at all.

## Candidates

At-a-glance (all unverified):

| Connector | Real-time? | Auth model | "Call/huddle" analog | Rough effort | Main risk |
|---|---|---|---|---|---|
| **Microsoft Teams** | Poll (likely) | Graph, delegated + **admin consent** | Weak / unclear | High | Admin gate + throttling; meeting-start visibility |
| **Discord** | Yes (Gateway) | Bot token, or user token (**ToS risk**) | Voice-channel joins | Medium | DM/user-scope limits; self-bot bans |
| **Telegram** | Yes | Bot API (easy) or MTProto user client | Calls via user client only | Low–Medium | Bots can't read arbitrary user DMs |
| **Zoom Team Chat** | Webhook (public endpoint) | Zoom app / OAuth | Meeting starts | Medium–High | Needs a reachable webhook; app review |
| **Google Chat** | Pub/Sub or webhook | Workspace app | Meet links, not great | Medium–High | Workspace admin + infra |
| **Email / Outlook / IMAP** | IMAP IDLE / Graph | Per-account creds or OAuth | None | Low–Medium | No huddle concept; noisy |
| **Generic local webhook** | Yes (localhost) | None (local only) | Whatever you POST | Low | You build the producer |

### Microsoft Teams — the headline ask

The sanctioned path is **Microsoft Graph** with delegated permissions (roughly
`Chat.Read`, `ChannelMessage.Read.All`, `Presence.Read`). Realities to expect:

- **Admin consent.** Most tenants require an admin to approve the app registration — a
  different admin than the Slack one, but the same kind of gate we're already waiting on for
  the sanctioned Slack app.
- **No good unofficial route.** The Slack "borrow the browser session" trick has no clean
  Teams equivalent; the desktop client's tokens are brokered through Windows auth and far
  more hostile to reuse, and corporate Conditional Access makes it worse. Don't plan a
  "TeamsClientApi" prototype mode.
- **Polling, not push (for a tray app).** Graph change notifications want a public HTTPS
  webhook, which a desktop app doesn't have. That pushes you to delta/polling on some of
  Graph's more aggressively throttled endpoints — expect tens-of-seconds latency, not the
  near-instant popups the Slack WebSocket gives.
- **The huddle equivalent is the weak spot.** Call/"meet now" start detection — the thing
  Slack itself doesn't notify and Heads-Up exists for — has no clean personal-delegated
  signal. Mentions, chats (DMs), and keywords translate fine; the call alert may be reduced
  or absent. Note Teams already notifies on meeting starts and mentions fairly loudly, so
  the value-add is more "bigger, unmissable, priority-people styling" than "fills a gap."

**Verdict to test first:** (1) will a tenant admin consent, and (2) is ~30s mention/DM/
keyword alerting without call detection still worth shipping? If the call-equivalent is the
must-have, Teams may not deliver it through any sanctioned door.

### Discord — likely the easiest real-time second connector

A proper **bot** over the Gateway WebSocket gives real-time message and **voice-channel-join**
events (a natural huddle analog) with low effort. The catch is scope: a bot only sees
servers ("guilds") it's added to and doesn't see a user's private DMs. Reading everything a
*user* sees requires a **user token / self-bot**, which is against Discord's ToS and risks a
ban. Good candidate if the use case is "alert me when a voice channel spins up in my
server," less so for "watch all my DMs." Worth doing early purely to prove the connector
seam cheaply.

### Telegram

Two very different routes. The **Bot API** is trivially easy (long-poll or webhook,
real-time), but bots can't read arbitrary user DMs — only chats they're in. A **user client**
via MTProto (e.g. TDLib) can act as the person and see everything, including calls, but is a
heavier dependency and auth flow. Low effort for the bot route, medium for the user-client
route.

### Zoom Team Chat

Zoom exposes chat and **meeting-start** events via its API + webhooks, which is appealing
(meeting starts are a real huddle analog). The friction is that webhooks need a
publicly-reachable endpoint and the app goes through Zoom's review. Medium–high effort;
better suited to a hosted component than a pure tray app.

### Google Chat

Workspace-native; a Chat app can receive messages and spaces events, typically via Pub/Sub
or webhooks. Real-time is possible but wants cloud infra and Workspace-admin setup. Meet has
no strong "call started in your space" signal. Medium–high effort.

### Email / Outlook / IMAP

Not chat, but the same "don't miss this" job. **IMAP IDLE** gives near-real-time new-mail
pushes against most providers with just account creds; **Graph** covers Outlook with OAuth.
Map "@mention" → to/cc you, keywords → subject/body, and there's no huddle concept. Low–
medium effort and a genuinely useful "urgent email, unmissable" connector — but noisy, so it
would want good filtering.

### Generic local webhook / bridge

A catch-all `IAlertSource` that listens on `localhost` for POSTed alerts. Any external script
— a cron job, a CI hook, a platform we don't natively support — can push a Heads-Up alert
without us writing a bespoke connector. Lowest effort and a useful escape hatch; it moves the
integration work to whoever produces the events.

## Suggested order, if we pursue this

1. **Generic local webhook** — trivial, unlocks everything indirectly, and exercises the seam.
2. **Discord** — cheapest *real* platform connector with genuine real-time events.
3. **Teams** — highest demand but gated on admin consent and the call-detection question;
   do the feasibility check (see verdict above) before building.

Everything else is opportunistic and depends on who actually asks for it.

## UI implications of multiple connectors

The core can run N connectors; the UI is still shaped for one. Almost none of this
matters until a **second** connector actually lands — with Slack-only, today's UI is
correct, and the data model is already source-aware, so these can be built alongside the
connector that forces them rather than up front.

**Already adapted**

- The dashboard **Overview** status card is an `ItemsControl` over `Status.Sources`, so it
  renders one card per connector automatically.
- Settings are namespaced by source (`MutedChannel.Source`, `Priority:Users` by source), so
  the model is ready even where the UI doesn't surface the source yet.

**Needs real design when connector #2 arrives**

- **Connect / sign-in flow — the big one.** The first-run welcome ("Connect to Slack"), the
  tray *Connect* action, and `ConnectionGate` + the WebView2 sign-in are all Slack-specific.
  A second connector brings a *different* auth flow (e.g. Teams OAuth consent looks nothing
  like Slack's embedded sign-in), so this needs a real rethink — per-connector connect state
  and per-connector sign-in — not a tweak.
- **Priority-people directory.** The editor searches exactly one workspace directory
  (hardcoded to `slack`). Two connectors need a connector picker or per-connector sections,
  since each connector is a separate address book.
- **Source badges.** Recent-alert rows and muted-channel chips don't show which connector
  they belong to. Fine at N=1, confusing at N≥2. `AlertHistoryItem` already exposes
  `SourceId`, so a small badge/icon is cheap when wanted.

**Cosmetic / terminology (also only matters at N≥2)**

- Strings assume Slack: "Open in Slack" tooltips, the "Huddles" toggle (Teams has no
  huddles), "workspace." The alert popup also doesn't indicate its source — a small
  connector glyph would help disambiguate.

**Missing entirely**

- There's no in-app way to enable/disable connectors — it's `Detection:Connectors` in the
  config file only. A **connectors settings page** is the natural home for that, and where
  the per-connector connect/sign-in flow would live.

**Recommendation:** don't build speculative multi-connector UI while N=1 — you'd be guessing
against a connector whose auth and terminology aren't pinned down yet, and likely rework it.
The one arguable early win is source badges on alert rows and the popup, purely so the
"it's multi-source" story reads honestly — but even that can wait for a real second source
to style against.
