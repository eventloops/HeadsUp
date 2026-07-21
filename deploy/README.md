# Deploying the relay

CD ([../.github/workflows/ci.yml](../.github/workflows/ci.yml)) publishes the relay and ships
it to a Linux box on every push to `main` — but only after a one-time setup on the box and a
set of GitHub secrets. This is that setup.

The pipeline does the repeatable part (build, copy, restart). It deliberately does **not** do
the one-time bootstrap (creating the user, installing the service, obtaining TLS), because that
touches the box's root and is done once by hand.

## What runs where

```
Slack ──Socket Mode (outbound)──▶ relay ──▶ desktops
                                    ▲
Desktop browser / WebSocket ──HTTPS──┘ (via Caddy on 443 → relay on 127.0.0.1:5285)
```

Only OAuth and the event WebSocket need to be reachable; Socket Mode dials out. So the box
needs one open port (443) and a domain.

## One-time box bootstrap

You need a domain pointing at the box (an A record) before TLS will work.

```bash
# 1. A dedicated, unprivileged user to run the relay.
sudo useradd --system --home /opt/headsup-relay --shell /usr/sbin/nologin headsup
sudo mkdir -p /opt/headsup-relay /etc/headsup-relay
sudo chown headsup:headsup /opt/headsup-relay

# 2. The service (from deploy/headsup-relay.service in this repo).
sudo cp headsup-relay.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable headsup-relay      # start on boot; CD starts it for real

# 3. TLS + reverse proxy. Caddy gets a certificate automatically.
sudo apt install -y caddy
sudo cp Caddyfile /etc/caddy/Caddyfile    # edit the domain first
sudo systemctl restart caddy

# 4. Let the deploy user restart the service and place the env file without a password.
#    Replace `deployer` with the DEPLOY_SSH_USER account.
echo 'deployer ALL=(root) NOPASSWD: /usr/bin/systemctl restart headsup-relay, /usr/bin/systemctl is-active headsup-relay, /usr/bin/install -m 600 -o root -g root /tmp/relay.env /etc/headsup-relay/relay.env, /usr/bin/chmod +x /opt/headsup-relay/HeadsUp.Relay' | sudo tee /etc/sudoers.d/headsup-relay
sudo chmod 440 /etc/sudoers.d/headsup-relay
```

The sudoers entry is scoped to exactly the four commands CD runs — the deploy account can
restart the relay and drop the env file, and nothing else.

## GitHub secrets

Repo → Settings → Secrets and variables → Actions. The relay values come from the **Heads-Up
Relay** Slack app (`A0BJ9SNJCBF`), not the per-user app.

| Secret | What |
|---|---|
| `DEPLOY_SSH_HOST` | The box's hostname or IP |
| `DEPLOY_SSH_USER` | The deploy account (the `deployer` above) |
| `DEPLOY_SSH_PORT` | SSH port, if not 22 (optional) |
| `DEPLOY_SSH_KEY` | Private key whose public half is in that account's `authorized_keys` |
| `RELAY_APP_TOKEN` | `xapp-…` (connections:write + authorizations:read) |
| `RELAY_BOT_TOKEN` | `xoxb-…` for name resolution |
| `RELAY_CLIENT_ID` | App client id |
| `RELAY_CLIENT_SECRET` | App client secret |
| `RELAY_PUBLIC_URL` | `https://headsup.example.com` — must match the Caddy domain **and** the app's registered redirect URL |
| `RELAY_SESSION_SIGNING_KEY` | A long random string. **Set this explicitly** — unset, the relay generates one per start and every desktop is signed out on each deploy. Generate with `openssl rand -base64 48`. |

Use a dedicated deploy SSH key, not a personal one.

## After the first deploy

- **Register the redirect URL.** The relay app's OAuth redirect URLs must include
  `https://headsup.example.com/auth/callback`. The manifest ships with a localhost one for
  local testing; production needs the real host added.
- **Check it's up:** `curl https://headsup.example.com/health` → `{"status":"ok"}`.
- **Logs:** `sudo journalctl -u headsup-relay -f`.

## Known limitations (see [../docs/relay-follow-ups.md](../docs/relay-follow-ups.md))

- **Single instance only.** Two relays sharing the app-level token get events distributed
  arbitrarily between them. Do not scale this horizontally without a shared bus. CD restarts in
  place rather than running two copies, which is correct.
- **A deploy drops in-flight events.** Socket Mode has no replay, so events arriving during the
  ~seconds of restart are lost. Deploy at quiet times.
