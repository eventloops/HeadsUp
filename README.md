# Slack Huddle Notifier

Teams-style popup notifications when someone starts a Slack Huddle — bigger,
louder, and harder to miss than Slack's own notification. Runs quietly in the
system tray (Windows) or menu bar (macOS).

- Shows who started the huddle, in which channel or DM, with their avatar
- One-click **Join huddle** button
- Optional alert sound, and extra-prominent styling for configured priority users
- Runs entirely per-user — **no admin rights needed** to install or run

| Project | Purpose |
|---|---|
| `SlackHuddleNotifier.Core` | Slack connection + huddle detection (cross-platform) |
| `SlackHuddleNotifier` | Windows app (WPF) |
| `SlackHuddleNotifier.Avalonia` | macOS / Linux app (also runs on Windows) |

---

## Prerequisites

1. **.NET 8 SDK** on the machine running the install script (the installed app
   is self-contained, so the SDK is only needed to build):
   - Windows: `winget install Microsoft.DotNet.SDK.8`
   - macOS: `brew install dotnet-sdk` or the installer from dotnet.microsoft.com
2. **Detection credentials**, which depend on the mode you pick (see *Three
   detection modes* below): a Slack app for `SlackApi`, your browser session for
   `SlackClientApi`, or nothing at all for `WindowsNotifications`.

## Install

### Windows

```powershell
git clone https://procode.ghe.com/Procode/SlackHuddleNotifier
cd SlackHuddleNotifier
.\install.ps1              # install or upgrade
.\install.ps1 -Autostart   # also run at logon
.\install.ps1 -Uninstall   # remove everything
```

Installs to `%LocalAppData%\Programs\SlackHuddleNotifier` with a Start Menu
shortcut. Your `appsettings.json` is preserved when you re-run the script.

#### "Running scripts is disabled on this system"

Corporate Windows images usually ship with PowerShell's execution policy set
to `Restricted`. Two per-user fixes, no admin required:

```powershell
# Option A - permanent for your account (recommended):
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
.\install.ps1

# Option B - one-off, changes nothing on the machine:
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

`RemoteSigned` still blocks unsigned scripts downloaded from the internet;
locally cloned files like this one run fine.

> If you instead see *"...set by your organization"*, the policy is enforced
> by Group Policy and neither option will work — talk to IT, or ask for a
> prebuilt release binary (a plain `.exe` isn't subject to execution policy).

### macOS (and Linux)

```bash
git clone https://procode.ghe.com/Procode/SlackHuddleNotifier
cd SlackHuddleNotifier
./install.sh               # install or upgrade
./install.sh --autostart   # also run at login (macOS LaunchAgent)
./install.sh --uninstall   # remove everything
```

Installs to `~/Applications/SlackHuddleNotifier`. The build RID is
auto-detected (Apple Silicon vs Intel).

## Three detection modes

| | `SlackApi` (default) | `SlackClientApi` | `WindowsNotifications` |
|---|---|---|---|
| Setup | Create a Slack app + tokens (below) | Paste your browser session token + cookie (below) | None |
| Admin approval | Needed to install the app | **None** | None |
| Platforms | Windows + macOS/Linux | Windows + macOS/Linux | Windows only |
| Detects | Every huddle in your conversations, reliably | Every huddle in your conversations | Only huddles Slack shows a toast for (muted channels/paused notifications = nothing) |
| Details | Real names, avatar, working join link | Real names, avatar, working join link | Parsed from notification text; no avatar; join opens Slack |
| Caveats | Official, stable | Unofficial API; can break when Slack changes | Stable, but misses muted/paused huddles |

Set the mode in `appsettings.json`:

```json
{ "Detection": { "Mode": "SlackApi" } }
```

With `WindowsNotifications`, no Slack tokens are needed at all — the app
watches the toasts Slack itself posts and re-raises them as big popups. On
first run, allow notification access if prompted (Windows Settings → Privacy
& security → Notifications → "Let apps access notifications").

## Slack app setup (one-time, per person — `SlackApi` mode only)

The app connects **as you** (a user token), so huddles are detected in exactly
the channels, group DMs, and DMs you are a member of — including DM huddles —
with no bot to invite anywhere. Huddles you start yourself are ignored.

> **Each person creates their own Slack app.** Don't share one app between
> teammates: Slack load-balances Socket Mode events across all connected
> clients of an app, so two people on one app would each receive a random
> half of the events — and a shared app authorized by several users would
> deliver everyone's DM events to everyone.

1. Create an app at <https://api.slack.com/apps> ("From scratch").
2. **Socket Mode** → enable, and create an **app-level token** with the
   `connections:write` scope → this is your `xapp-...` token.
3. **OAuth & Permissions** → under **User Token Scopes** add:
   `users:read`, `channels:read`, `groups:read`, `im:read`, `mpim:read`,
   `channels:history`, `groups:history`, `im:history`, `mpim:history`.
4. **Event Subscriptions** → enable, and under **Subscribe to events on
   behalf of users** add:
   `message.channels`, `message.groups`, `message.im`, `message.mpim`.
5. **Install App** to the workspace → copy the **User OAuth Token**
   (`xoxp-...`).

## Slack client-session setup (`SlackClientApi` mode)

This mode borrows the session your browser (or the Slack desktop app) is
already logged into — **no Slack app to create and no admin approval**. It's a
prototype: the endpoints are undocumented and can change without warning. Fine
for a personal notifier while you wait for app approval; know what it is before
you lean on it.

You need two values from a Slack session **in your browser** (the desktop app
holds the same two, but the browser is the easiest place to read them):

1. **Client token (`xoxc-…`)** — open Slack in your browser, open DevTools
   (F12) → **Console**, and run:

   ```js
   JSON.parse(localStorage.localConfig_v2).teams[
     Object.keys(JSON.parse(localStorage.localConfig_v2).teams)[0]
   ].token
   ```

   Copy the `xoxc-…` string it prints. (In more than one workspace? Pick the
   right team key instead of the first one.)

2. **`d` cookie (`xoxd-…`)** — it's HttpOnly, so the console can't read it. In
   DevTools → **Application** → **Cookies** → your Slack domain, copy the
   **Value** of the cookie named `d`.

Put both in `appsettings.json` and set the mode:

```json
{
  "Detection": { "Mode": "SlackClientApi" },
  "SlackClient": {
    "ClientToken": "xoxc-...",
    "Cookie": "xoxd-...",
    "WorkspaceUrl": "https://yourworkspace.slack.com"
  }
}
```

`WorkspaceUrl` is optional (defaults to `https://slack.com`); set it if calls
come back with auth errors. Priority users are shared with `Slack:PriorityUsers`.

**Confirm it works before relying on it.** Tokens expire when your session
rotates (SSO especially), so verify end-to-end. Start the app from a terminal
and watch the log:

```powershell
Get-Content -Wait "$env:LocalAppData\SlackHuddleNotifier\logs\log-*.txt" |
  Select-String "session OK","WebSocket","Huddle","rtm","auth.test"
```

You want `Slack client session OK` followed by `Connected to Slack real-time
WebSocket`. Then have someone start a huddle — or start one yourself: your own
huddles log a `Huddle signal received` line (confirming the signal) but are
deliberately suppressed from popups. If you see `auth.test failed` or
`rtm.connect failed` instead, re-extract both values and try again.

## Configure

Edit `appsettings.json` in the install directory:

```json
{
  "Slack": {
    "AppToken": "xapp-...",
    "UserToken": "xoxp-...",
    "PriorityUsers": [ "U0123456789" ]
  },
  "Notification": {
    "PlaySound": true,
    "SoundFilePath": "",
    "ToastDurationSeconds": 15
  }
}
```

- `PriorityUsers` — Slack user IDs whose huddles get the red header, green
  join button, and incoming-call styling.
- `SoundFilePath` — optional custom sound (`.wav` on Windows, `.aiff`/`.wav`
  on macOS); leave empty for the system default.

## Running in the background

The app is tray-only: no window opens at launch — look for the headset icon
in the system tray (Windows) or menu bar (macOS). Popups appear only when a
huddle starts; right-click the icon for status, test notification, and exit.

Starting automatically is **opt-in**: install with `-Autostart` (Windows) or
`--autostart` (macOS) and it will launch when you sign in. Already installed
without it? Just re-run the install script with the flag — settings are
preserved. It starts at *sign-in*, not machine boot, as befits a per-user app.

## Try it without Slack

Preview the popup without any tokens configured:

```
SlackHuddleNotifier.exe --test-popup              # standard
SlackHuddleNotifier.exe --test-popup --priority   # priority variant
```

On macOS the tray menu also has a **Test notification** item.

## Logs

`%LocalAppData%\SlackHuddleNotifier\logs` (Windows) or
`~/.local/share/SlackHuddleNotifier/logs` (macOS/Linux) —
rolling daily files, 7 kept.
