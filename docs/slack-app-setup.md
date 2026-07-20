# Connecting Heads-Up with a Slack app (the sanctioned path)

This is the `SlackApi` connector: a real Slack app, a Socket Mode connection, and a user OAuth
token obtained with PKCE. It's the supported, documented route — as opposed to `SlackClientApi`,
which borrows your browser session and relies on undocumented endpoints.

Setup takes about a minute, once. Everything after that is automatic.

## Why you create your own app

This is the part that surprises people, so it's worth stating plainly.

Socket Mode authenticates with an **app-level token** (`xapp-…`). That token belongs to the
*app*, not to your installation of it, and Slack only ever mints one in the app configuration
UI — there is **no API and no OAuth scope** that will produce one.

So a single shared "Heads-Up" Slack app can't work for a team: every desktop would open a
Socket Mode connection with the same token, and Slack **load-balances events across connected
sockets**. You'd receive a random subset of your colleagues' events and miss most of your own.
(It's also why Socket Mode apps are barred from the public Slack Marketplace.)

One app per person is what makes the sanctioned path work locally and in real time, with no
server in the middle that could read your messages.

## Setup

### 1. Create your app

Open the prefilled creation link — it carries [the manifest](slack-app-manifest.json), so
there's no JSON to copy:

```
https://api.slack.com/apps?new_app=1&manifest_json=<url-encoded manifest>
```

Heads-Up's Connect screen has a **Create your Heads-Up Slack app** button that opens exactly
this. Pick your workspace and confirm.

> If your workspace requires admin approval for new apps, the request goes to your Slack admin
> at this point. The manifest asks only for read scopes and posts nothing.

### 2. Generate the app-level token

In the new app's settings → **Basic Information** → **App-Level Tokens** → **Generate Token
and Scopes**:

- Name it anything (`heads-up-socket` works)
- Add the **`connections:write`** scope — Socket Mode won't open without it
- For a **relay** deployment also add **`authorizations:read`**, which the relay needs to
  expand a truncated authorizations list. (This per-user path doesn't: every event it receives
  is already its own user's.)
- Generate, then copy the `xapp-…` value

### 3. Enter the two values

Back in Heads-Up's Connect screen:

| Field | Where it comes from |
|---|---|
| **Client ID** | Basic Information → App Credentials → Client ID |
| **App-level token** | The `xapp-…` you just generated |

### 4. Sign in

Click **Sign in with Slack**. Your browser opens Slack's authorization page; approving it
redirects to `http://localhost:32741/callback`, which Heads-Up is listening on. The browser tab
can be closed immediately afterwards.

## What's stored, and where

| Value | Stored | How |
|---|---|---|
| Client ID, app-level token | `appsettings.Local.json` | Plain — neither is a user credential |
| User token, refresh token | `%LOCALAPPDATA%\HeadsUp\slack-app-tokens.dat` | Encrypted with DPAPI |

DPAPI is scoped to your Windows user on this machine, so the token file is useless if copied
elsewhere — Heads-Up treats an undecryptable file as "not signed in" and re-prompts.

No client secret is involved anywhere. PKCE lets Heads-Up authenticate as a public client, so
there's nothing sensitive to embed in the app or host on a server.

## Token rotation

Loopback and custom-scheme redirects always issue **rotating** tokens. Heads-Up renews the
access token automatically (5 minutes before expiry, and on any auth failure) and persists the
replacement refresh token, since Slack invalidates the previous one on each use.

**Refresh tokens expire after 30 days.** If Heads-Up doesn't run for a month, the stored
credentials lapse and you'll be asked to sign in again — step 4 only; the app itself stays.

## Enabling the connector

```json
{
  "Detection": {
    "Connectors": [
      { "Type": "SlackApi" }
    ]
  }
}
```

`SlackClientApi` remains the default. Both can run side by side, but pointing them at the same
workspace will double every alert — pick one per workspace.

## Troubleshooting

**"Could not listen on port 32741"** — something else holds the port. The port is fixed because
it has to match the redirect URL registered in the manifest, so free it and reconnect.

**Nothing happens after approving in the browser** — the redirect URL in your app must be
exactly `http://localhost:32741/callback`. Check OAuth & Permissions → Redirect URLs if you
edited the manifest.

**Connects, but no alerts** — confirm **`connections:write`** is on the app-level token, and
that Event Subscriptions lists the four `message.*` **user** events. Bot events won't fire:
desktop OAuth redirects can't request bot scopes, so Heads-Up subscribes as you, not as a bot.
