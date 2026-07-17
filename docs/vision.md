# Product direction — rule-based, project-centric categorisation

> ⚠️ **Thinking out loud, not a committed roadmap.** This is a sketch of where Heads-Up
> could grow, captured while the idea is fresh. Nothing here is scheduled, and the
> market/prior-art notes are from general knowledge, not a current investigation — verify
> before betting on any of it. The point is to write down the shape of the idea and its
> honest costs, not to commit to building it.

## The shift, in one line

From *"big, unmissable desktop alerts for one service"* to *"categorise all my messages,
emails and alerts for a **project** — across Teams, Slack, Outlook, … — using clear,
user-defined rules."*

Today's Heads-Up is the beachhead. This doc is about the larger thing it could become.

## Two axes (they compose)

The vision is really two separable ideas that reinforce each other:

1. **Categorisation** — *which project/category does this item belong to?* A message, email
   or event gets assigned to *Project Atlas* whether it came from a Slack channel, a Teams
   chat, or an Outlook thread. This is the new, bigger idea.
2. **Surfacing** — *how loudly do I get told?* This is what Heads-Up already does: priority
   people, unmissable popups, snooze/DND.

Rules drive both, and they stack: project rules decide *membership*; surfacing rules decide
*presentation* for a category or project.

## Rule sets (the mechanism)

A **rule** is `match(conditions) → outcome`:

- **Conditions** (combinable): source (Slack/Teams/Outlook), kind (huddle/mention/DM/
  message/email), from-user ∈ {set}, channel/folder ∈ {set}, text/subject contains
  {keywords} or matches a pattern, is-a-DM, later maybe time-of-day / focus window.
- **Outcome**: a **category/project** assignment, plus a **disposition** — *suppress*,
  *alert*, or *escalate* (the louder, incoming-call treatment) — and optional presentation
  (sound, pop vs log-only).
- **Ordering**: evaluated top-down, first match wins — the precedence logic already living
  in the classifier (mention beats DM beats keyword).

**Every feature Heads-Up has today is already a hardcoded rule** — generalising just makes
them user-editable and composable:

| Today's feature | As a rule |
|---|---|
| Priority users | `from user ∈ {…}` → escalate |
| Muted channels | `in channel ∈ {…}` → suppress |
| Keyword alerts | `text contains {…}` → alert (category "keyword") |
| Alert-kind toggles | `kind is mention` → alert / don't |

Two guardrails that keep "good idea" from becoming "overbuilt":

- **Defaults-first.** The out-of-the-box behavior *is* the built-in rules above. Someone who
  never opens the rules UI gets exactly today's experience. Rules are the power-user layer on
  top, not a prerequisite for value.
- **Surfacing, not routing.** If rules become "when X, send to a webhook / email / anywhere,"
  we've reinvented Zapier and walked into a commoditised middle. Rules should decide *how
  loudly to surface an item to the user* (and which project it's in) — not act as a generic
  event router. Holding this line is what preserves the differentiation.

## Projects — the bigger bet

A **project** is a bucket defined by rules that span connectors. Example — *Project Atlas*:

> Slack `#atlas` **OR** the Teams chat named "Atlas" **OR** Outlook mail whose subject
> contains "atlas" or is from anyone on the Atlas team **OR** any message containing "atlas".

Everything matching lands in one place: a project view that accumulates the relevant
threads, emails, huddles and mentions from everywhere, instead of the user manually
stitching four tools together. Projects can carry their own surfacing preferences ("escalate
anything in Atlas from Sarah"; "mute Project Bravo entirely").

Migration is graceful: the existing `Priority:Users` and `MutedChannels` become seed rules,
so nothing breaks and the rules view simply *shows* people what was already implicit.

## What this costs — the honest scope jump

This is a materially **larger product** than the alerter, and the jump is real:

- **Event detection → content ingestion.** Today connectors watch for *signals* (a huddle
  started, you were mentioned) and pull minimal context; history is ephemeral and in-memory.
  A project view needs to ingest and **persist** message/email *content* over time. Different
  problem.
- **Data access gets heavy.** Reading full channel history, all your email, Teams chats means
  broad read scopes on every provider. The "no-admin client-session" trick gets riskier,
  slower, and more likely to hit rate limits or security policy when pulling *everything*
  rather than watching for a signal.
- **Privacy & retention.** You'd be storing a corpus of the user's messages and email
  locally — a storage/sync problem and a privacy responsibility the alerter doesn't carry.
- **Rule authoring UX is the make-or-break.** "Really clear rules" is the crux *and* the
  hardest part — most rule-based tools end up muddy. Rules must be powerful but obvious, and
  **debuggable** ("why did this land in Atlas?"). This is the core design challenge, not a
  detail.

## Positioning / prior art

The "pool alerts from many providers and categorise them" pattern is **well-established** —
so pooling itself is not a moat:

- **Ops/incident:** PagerDuty, Opsgenie, Splunk On-Call — pool, dedupe, categorise, route,
  escalate. SIEM/SOAR (Splunk, Sentinel) do it for security at enterprise scale.
- **Notification infrastructure (for developers):** Novu, Courier, Knock.
- **Automation / DIY:** Zapier, IFTTT, n8n, Make; ntfy, Pushover.
- **The OS itself:** macOS/Windows/Android notification centers already pool + group by app
  with per-app priority and DND.

The nearest neighbors to the *project/inbox* framing are the universal-inbox / daily-triage
tools — **Akiflow** and **Sunsama** (pull tasks/messages from many tools into one place to
triage) and **Missive** (shared inbox pooling email + some chat with rules/labels); plus
chat aggregators **Beeper / Texts**. None of them nail *"define a project by rules that span
Slack + Teams + Outlook and gather everything about it."* So the specific combination is
under-served — but it's a **more crowded neighborhood** than the huddle-alerter wedge, and it
competes with people's existing folders and channels.

**The moat is not pooling or categorising** (commoditised). It's the parts nobody packages
for a knowledge worker: **unmissable, priority-aware surfacing**; **huddle/call-start
detection** (a genuine gap — Slack doesn't even notify you); and **per-user, no-admin
install**. Rules and projects are valuable *in service of* that, not as a generic router.

## Sequencing — alerter as beachhead

Grow into this; don't pivot the whole thing at once.

1. **Ship the alerter.** The differentiated, no-admin wedge. Largely done.
2. **Generalise into rule sets (surfacing axis).** Turn priority users / muted channels /
   toggles into user-editable rules, seeded from today's config. Ships real value without the
   data-ingestion jump. Lower risk; validates the rule UX.
3. **Add project categorisation (categorisation axis).** The big bet — gated on the
   data-access and privacy realities above. Only pursue once the rule UX is proven and the
   ingestion/auth story is worked out per connector.

## How it fits the architecture we have

Nothing gets thrown away:

- `IAlertSource` generalises to an **item source** that yields messages/emails/events, not
  just alerts.
- The **rule engine** sits above the sources, assigning items to projects/categories and
  dispositions — it's where `AlertClassifier.Classify(...)` grows into.
- **Alerting becomes one consumer** of that pipeline (loud surfacing); a **project browser**
  is another consumer of the same categorised stream.
- The connector seam, per-source identity, and source-namespaced settings all still hold.

## Open questions (to decide before betting)

- Is this an **evolution of Heads-Up** or a **new product** with the alerter as one feature?
- How much content do we actually need to ingest/persist for a project view to be useful vs.
  just deep-linking out to each tool? (Less ingestion = less risk.)
- Which connectors can realistically grant the read access this needs without an admin?
- What's the minimum rule model that's genuinely *clear* to non-technical users?
