#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Stufe 0: Navidrome auf oi-o-mat einrichten und als launchd-Dienst ins Tailnet stellen.
# Idempotent – kann nach Updates oder geänderter Tailscale-IP erneut laufen.
#
#   ./deploy/navidrome/setup-navidrome.sh
#
# Optional: MUSIC_ROOT=/pfad, TAILSCALE_IP=100.x.y.z
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/common.sh"

require_mount

echo "==> Homebrew-Pakete"
brew list navidrome >/dev/null 2>&1 || brew install navidrome
brew list ffmpeg >/dev/null 2>&1 || brew install ffmpeg

echo "==> Ordnerstruktur unter $MUSIC_ROOT"
mkdir -p "$MUSIC_ROOT"/{Inbox,Library,navidrome/cache,logs}

ip="$(tailscale_ip)"
brew_prefix="$(brew --prefix)"
echo "==> Tailscale-IP $ip"

render_template "$DEPLOY_DIR/navidrome/navidrome.toml.template" "$MUSIC_ROOT/navidrome/navidrome.toml" \
  MUSIC_ROOT="$MUSIC_ROOT" TAILSCALE_IP="$ip"

plist="$(mktemp)"
render_template "$DEPLOY_DIR/launchd/org.navidrome.server.plist.template" "$plist" \
  MUSIC_ROOT="$MUSIC_ROOT" NAVIDROME_BIN="$brew_prefix/bin/navidrome" BREW_PREFIX="$brew_prefix"
plutil -lint "$plist" >/dev/null
install_agent org.navidrome.server "$plist"
rm -f "$plist"

echo
echo "Navidrome läuft unter http://$ip:4533"
echo "Beim ersten Aufruf den Admin-Benutzer anlegen, dann Amperfy (iPhone) bzw. play:Sub (Mac) mit dieser URL verbinden."
