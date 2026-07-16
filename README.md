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
2. **A Slack app** for your workspace (one-time setup, shareable) — see below.

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

## Slack app setup (one-time)

1. Create an app at <https://api.slack.com/apps> ("From scratch").
2. **Socket Mode** → enable, and create an **app-level token** with the
   `connections:write` scope → this is your `xapp-...` token.
3. **OAuth & Permissions** → add bot token scopes:
   `users:read`, `channels:read`, `groups:read`, `im:read`, `mpim:read`,
   `channels:history`, `groups:history`, `im:history`, `mpim:history`.
4. **Event Subscriptions** → enable, subscribe to bot events:
   `message.channels`, `message.groups`, `message.im`, `message.mpim`.
5. **Install App** to the workspace → this gives you the `xoxb-...` bot token.
6. Invite the bot to the channels you want huddle alerts for
   (`/invite @YourApp`).

## Configure

Edit `appsettings.json` in the install directory:

```json
{
  "Slack": {
    "AppToken": "xapp-...",
    "BotToken": "xoxb-...",
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
