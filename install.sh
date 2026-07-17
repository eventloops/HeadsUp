#!/usr/bin/env bash
# Per-user install of Heads-Up (macOS/Linux). No sudo required.
#
# Publishes the Avalonia head as a self-contained build (no .NET runtime
# install needed) into ~/Applications/HeadsUp and optionally
# registers a LaunchAgent so it starts at login (macOS).
#
# Your configuration is preserved on upgrade: appsettings.json and the
# git-ignored appsettings.Local.json (Slack session, priority users,
# alert toggles).
#
# Usage:
#   ./install.sh                # install/upgrade
#   ./install.sh --autostart    # install/upgrade + run at login (macOS)
#   ./install.sh --uninstall    # remove everything
set -euo pipefail

APP_NAME="HeadsUp"
INSTALL_DIR="$HOME/Applications/$APP_NAME"
EXE="$INSTALL_DIR/HeadsUp.Avalonia"
PLIST_LABEL="com.procode.headsup"
PLIST_PATH="$HOME/Library/LaunchAgents/$PLIST_LABEL.plist"
# Pre-rename LaunchAgent, migrated/cleaned up on install and uninstall.
LEGACY_PLIST_LABEL="com.procode.slackhuddlenotifier"
LEGACY_PLIST_PATH="$HOME/Library/LaunchAgents/$LEGACY_PLIST_LABEL.plist"
REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$REPO_ROOT/HeadsUp.Avalonia/HeadsUp.Avalonia.csproj"

AUTOSTART=false
UNINSTALL=false
for arg in "$@"; do
    case "$arg" in
        --autostart) AUTOSTART=true ;;
        --uninstall) UNINSTALL=true ;;
        *) echo "Unknown option: $arg" >&2; exit 1 ;;
    esac
done

if $UNINSTALL; then
    pkill -f "$EXE" 2>/dev/null || true
    if [[ "$OSTYPE" == darwin* ]]; then
        for plist in "$PLIST_PATH" "$LEGACY_PLIST_PATH"; do
            if [[ -f "$plist" ]]; then
                launchctl unload "$plist" 2>/dev/null || true
                rm -f "$plist"
                echo "Removed LaunchAgent $(basename "$plist")."
            fi
        done
    fi
    rm -rf "$INSTALL_DIR"
    echo "$APP_NAME uninstalled."
    exit 0
fi

# Pick the runtime id for this machine.
case "$OSTYPE" in
    darwin*) [[ "$(uname -m)" == "arm64" ]] && RID="osx-arm64" || RID="osx-x64" ;;
    *)       [[ "$(uname -m)" == "aarch64" ]] && RID="linux-arm64" || RID="linux-x64" ;;
esac

echo "Publishing self-contained build for $RID (this can take a minute)..."
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained -o "$STAGING" -v q -nologo

# Preserve the user's configuration across upgrades - both appsettings.json and the
# local override holding their Slack session, priority users, and alert settings.
KEEP_SETTINGS=""
KEEP_LOCAL_SETTINGS=""
if [[ -f "$INSTALL_DIR/appsettings.json" ]]; then
    KEEP_SETTINGS="$(cat "$INSTALL_DIR/appsettings.json")"
    echo "Preserving existing appsettings.json."
fi
if [[ -f "$INSTALL_DIR/appsettings.Local.json" ]]; then
    KEEP_LOCAL_SETTINGS="$(cat "$INSTALL_DIR/appsettings.Local.json")"
    echo "Preserving existing appsettings.Local.json."
fi

pkill -f "$EXE" 2>/dev/null || true
mkdir -p "$INSTALL_DIR"
rm -rf "${INSTALL_DIR:?}"/*
cp -R "$STAGING"/. "$INSTALL_DIR"/
chmod +x "$EXE"

if [[ -n "$KEEP_SETTINGS" ]]; then
    printf '%s' "$KEEP_SETTINGS" > "$INSTALL_DIR/appsettings.json"
fi
if [[ -n "$KEEP_LOCAL_SETTINGS" ]]; then
    printf '%s' "$KEEP_LOCAL_SETTINGS" > "$INSTALL_DIR/appsettings.Local.json"
fi

# Migrate from installs under the old product name: keep their autostart choice,
# then retire the old-label LaunchAgent.
if [[ "$OSTYPE" == darwin* && -f "$LEGACY_PLIST_PATH" ]]; then
    AUTOSTART=true
    launchctl unload "$LEGACY_PLIST_PATH" 2>/dev/null || true
    rm -f "$LEGACY_PLIST_PATH"
    echo "Migrated LaunchAgent from previous install."
fi

if $AUTOSTART && [[ "$OSTYPE" == darwin* ]]; then
    mkdir -p "$(dirname "$PLIST_PATH")"
    cat > "$PLIST_PATH" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$PLIST_LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>$EXE</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <false/>
</dict>
</plist>
EOF
    launchctl unload "$PLIST_PATH" 2>/dev/null || true
    launchctl load "$PLIST_PATH"
    echo "LaunchAgent registered (runs at login)."
fi

echo ""
echo "$APP_NAME installed to $INSTALL_DIR"
if [[ -z "$KEEP_LOCAL_SETTINGS" ]]; then
    echo "Next step: add your Slack session (xoxc- token + d cookie) to"
    echo "  $INSTALL_DIR/appsettings.Local.json  - see the README for how to read both values."
fi
echo "Run it with: \"$EXE\""
