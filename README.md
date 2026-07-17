# Heads-Up

Big, hard-to-miss desktop alerts for the Slack activity you can't afford to miss:
**huddles**, **@mentions**, **DMs**, and **keyword matches**. Bigger, louder, and
harder to miss than Slack's own notifications — Slack doesn't even notify you when
a huddle starts in one of your channels. Runs quietly in the system tray (Windows)
or menu bar (macOS/Linux).

- A prominent popup showing **who** and **where** — avatar, channel, a message
  preview for mentions/DMs/keywords, and a one-click **Join** button for huddles
- A tray **dashboard**: live connection status, a **recent-alerts** history (catch
  what you missed while away — click any row to open it in Slack), a **"Notify me
  about"** panel to toggle each alert kind, and a searchable **priority users**
  editor — their alerts get extra-prominent, incoming-call styling
- **Snooze** from the tray (30 min / 1 hour / until tomorrow), and it automatically
  goes quiet while your **Slack Do Not Disturb** is on — snoozed alerts still land
  in history, just without the popup or sound
- **Mute noisy channels** — one click on a recent alert stops that channel alerting
- Optional alert sound; your own huddles and messages never alert
- Runs entirely per-user — **no admin rights** needed to install or run

| Project | Purpose |
|---|---|
| `HeadsUp.Core` | Slack connection + detection (cross-platform) |
| `HeadsUp` | Windows app (WPF) — the primary experience |
| `HeadsUp.Avalonia` | macOS / Linux app (also runs on Windows) |

> **What alerts out of the box:** huddles and @mentions. DM and keyword alerts are
> built in but **off by default** (DMs can be frequent) — flip them on in the
> dashboard's *Notify me about* panel, no restart needed.

---

## How it connects to Slack

Heads-Up needs to see your Slack activity. There are two routes — and today only the
first is live.

### `SlackClientApi` — the current prototype

Uses the same session your own Slack client uses (an `xoxc` client token plus the `d`
session cookie). **No Slack app to create and no admin approval.** On Windows it obtains
these through an **embedded Slack sign-in**: you click *Connect*, sign in once (SSO and
all) in a small window, and it captures the session for you — nothing to copy or paste.
It keeps that window warm in the background so the session stays alive.

It's a prototype, honestly so: these endpoints are unofficial and undocumented, so they
can change without notice, and a locked-down workspace can rotate the session out from
under it (you'd just sign in again). Fine as a stopgap while the proper app is approved —
know what it is before you lean on it.

### `SlackApi` — the goal *(pending app approval)*

The endgame is a **proper, admin-approved Slack app** using your own OAuth tokens:
sanctioned, documented, and stable. Once your workspace admin approves it, this becomes
the default and the client-session prototype retires.

The plan when that lands: **one** registered app that each person **authorizes** once (a
normal "Add to Slack" / *Allow* click), which issues *them* a personal user token scoped
to exactly what they can already see — their channels, DMs, mentions. No session scraping,
no impersonating the official client, and the token survives rotation. Per-person setup
is just that one click; nothing is written up here yet because it's gated on the approval.

Both routes share the same detection rules — huddles, mentions, DMs, keywords, and the
alert toggles behave identically — so switching modes later changes nothing about what
you're alerted on.

Pick the route in `appsettings.json` (`SlackClientApi` is the shipped default,
since it's the only route that works today):

```json
{ "Detection": { "Mode": "SlackClientApi" } }
```

---

## Install

### Windows

```powershell
git clone https://procode.ghe.com/Procode/HeadsUp
cd HeadsUp
.\install.ps1              # install or upgrade
.\install.ps1 -Autostart   # also run at logon
.\install.ps1 -Uninstall   # remove everything
```

Installs to `%LocalAppData%\Programs\HeadsUp` with a Start Menu shortcut. Re-running
the script upgrades in place and preserves your configuration — both `appsettings.json`
and `appsettings.Local.json` (your Slack session, priority users, and alert toggles).
Needs the **.NET 8 SDK** to build (`winget install Microsoft.DotNet.SDK.8`); the
installed app is self-contained.

<details>
<summary>"Running scripts is disabled on this system"</summary>

Corporate Windows images often set PowerShell's execution policy to `Restricted`.
Two per-user fixes, no admin required:

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned   # permanent, recommended
powershell -ExecutionPolicy Bypass -File .\install.ps1 # one-off, changes nothing
```

If you instead see *"...set by your organization"*, it's enforced by Group Policy —
talk to IT, or ask for a prebuilt `.exe` (not subject to execution policy).
</details>

### macOS / Linux

```bash
git clone https://procode.ghe.com/Procode/HeadsUp
cd HeadsUp
./install.sh               # install or upgrade
./install.sh --autostart   # also run at login (macOS LaunchAgent)
./install.sh --uninstall   # remove everything
```

Installs to `~/Applications/HeadsUp`; the build RID is auto-detected (Apple Silicon vs
Intel). Upgrades preserve your configuration the same way as on Windows. Note the
embedded sign-in is Windows-only for now — on macOS/Linux use the manual credential
paste described below.

---

## Signing in (client-session prototype)

**Windows (embedded sign-in — the default).** This is how the Windows app ships, so
there's nothing to configure — just launch it. Optionally point the sign-in at your
workspace:

```json
{
  "Detection": { "Mode": "SlackClientApi" },
  "SlackClient": {
    "CredentialSource": "WebView2",
    "WorkspaceUrl": "https://yourworkspace.slack.com"
  }
}
```

On first launch you'll get a short **welcome** — pick which activity to be alerted on, then
*Connect to Slack*. A *Connecting to Slack…* window follows; if there's no live session it
shows the Slack sign-in, you log in once, and it connects itself and opens the dashboard.
`WorkspaceUrl` just points the sign-in at the right workspace.

**Manual paste (macOS/Linux, or if you prefer).** Set `"CredentialSource": "Config"` and
provide the two values yourself:

```json
{
  "Detection": { "Mode": "SlackClientApi" },
  "SlackClient": {
    "CredentialSource": "Config",
    "ClientToken": "xoxc-...",
    "Cookie": "d=xoxd-...; d-s=...",
    "WorkspaceUrl": "https://yourworkspace.slack.com"
  }
}
```

Read the two values from a signed-in Slack session in your browser:

1. **Client token (`xoxc-…`)** — DevTools (F12) → **Console**:
   ```js
   JSON.parse(localStorage.localConfig_v2).teams[
     Object.keys(JSON.parse(localStorage.localConfig_v2).teams)[0]
   ].token
   ```
2. **`d` cookie (`xoxd-…`)** — DevTools → **Application** → **Cookies** → your Slack
   domain → copy the value of the cookie named `d` (and `d-s` if present).

Session tokens rotate (SSO especially), so if detection goes quiet you'll re-grab them —
which is exactly why the Windows embedded sign-in exists.

---

## Configure

Everything is in `appsettings.json` (in the install directory), and changes are picked up
**live** — no restart needed.

```json
{
  "Alerts": {
    "Huddles": true,
    "Mentions": true,
    "DirectMessages": false,
    "Keywords": false,
    "KeywordList": [ "deploy", "prod down" ],
    "MutedChannels": [ { "Id": "C0123456789", "Name": "random" } ]
  },
  "Notification": {
    "PlaySound": true,
    "SoundFilePath": "",
    "ToastDurationSeconds": 15
  },
  "Slack": {
    "PriorityUsers": [ "U0123456789" ]
  }
}
```

- **Alerts ("Notify me about")** — which activity pops an alert. Easiest via the
  **dashboard**, which saves back here as you toggle. Defaults: huddles and @mentions
  on; DMs and keywords off. A direct @mention always wins over "it's a DM", which wins
  over a keyword match — one alert per message, never three.
- **`KeywordList`** — words or phrases (matched case-insensitively, anywhere in the
  message) that trigger a keyword alert when `Keywords` is on. Editable as a
  comma-separated list in the dashboard.
- **`MutedChannels`** — channels that never alert, whatever the kind. Easiest via the
  dashboard's *Mute channel* button on a recent alert; stored here as `{ Id, Name }`.
- **Priority users** — easiest via the **dashboard** (tray → *Open dashboard* → search by
  name → *Add*); it saves back here for you. Priority users get the red header, green join
  button, and incoming-call styling. `Slack:PriorityUsers` also accepts raw user IDs.
- **`SoundFilePath`** — optional custom sound (`.wav` on Windows; `.aiff`/`.wav` on
  macOS); leave empty for the system default.

Secrets (client token/cookie) live in a git-ignored `appsettings.Local.json` next to the
app, so they never end up in source control — dashboard edits (alert toggles, priority
users) land there too.

## The dashboard

Right-click (or **double-click**) the tray icon → **Open dashboard**. It shows live status
— connection, signed-in account, workspace, and detection mode — plus the **"Notify me
about"** alert toggles (with the keyword list) and the priority-users editor. Everything
applies immediately. It opens itself automatically the first time you connect.

Under it, two tabs:

- **Recent alerts** — a running list of what you've been alerted to this session, newest
  first, so a popup you missed while away isn't lost. Click any row to open it in Slack;
  *Mute channel* stops that channel alerting; *Clear* empties the list. (In-memory — it
  resets when the app restarts.) Muted channels are listed below with a one-click unmute.
- **Priority users** — search your workspace and add people whose alerts should get the
  louder, incoming-call treatment.

## Snooze & Do Not Disturb

Right-click the tray icon → **Snooze alerts** for 30 minutes, 1 hour, or until tomorrow
morning; **Resume alerts** ends it early. While snoozed — or whenever your **Slack Do Not
Disturb** is active (Heads-Up checks periodically) — no popups or sounds fire, but alerts
are still recorded in the dashboard's *Recent alerts* so you can catch up. The tray menu
and dashboard both show when you're muted and why.

## Running in the background

Tray-only: no window opens at launch — look for the headset icon in the system tray
(Windows) or menu bar (macOS). Popups appear only when something alert-worthy happens —
a huddle starts, or a message matches your mention/DM/keyword settings; right-click the
icon for the dashboard, About, and exit.

Autostart is opt-in: install with `-Autostart` (Windows) or `--autostart` (macOS) to
launch at sign-in. Already installed without it? Re-run the install script with the flag —
settings are preserved.

## Try it without Slack

Preview the interface with no tokens configured:

```
HeadsUp.exe --test-popup              # a sample alert popup
HeadsUp.exe --test-popup --priority   # the priority (incoming-call) variant
HeadsUp.exe --welcome                 # the first-run welcome screen
HeadsUp.exe --dashboard               # open the dashboard directly
```

## Logs

`%LocalAppData%\HeadsUp\logs` (Windows) or `~/.local/share/HeadsUp/logs`
(macOS/Linux) — rolling daily files, 7 kept.

## Tests

```
dotnet test
```

`HeadsUp.Tests` covers the core pipeline (~99% line coverage), including an in-process
fake Slack server that drives the real WebSocket path end-to-end — auth, connect, pushed
frames, DND polling, reconnects. `HeadsUp.Wpf.Tests` covers the dashboard and tray view
models plus a smoke test that renders the real dashboard window so broken XAML fails the
build. The WebView2 sign-in flow is exercised manually (`--test-popup` helps).
