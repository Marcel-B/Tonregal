#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Baut Tonregal (inkl. Vue-SPA) self-contained für osx-arm64, kopiert es nach $MUSIC_ROOT/app
# und (re)startet den launchd-Dienst de.bvelop.tonregal. Datenbank und eigene Konfiguration
# (appsettings.Production.json) im Zielordner bleiben unangetastet.
#
#   ./deploy/deploy-tonregal.sh
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

require_mount
repo="$(cd "$DEPLOY_DIR/.." && pwd)"
app="$MUSIC_ROOT/app"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

echo "==> dotnet publish"
dotnet publish "$repo/src/Tonregal.Web" -c Release -r osx-arm64 --self-contained -o "$staging"

echo "==> Kopieren nach $app"
mkdir -p "$app" "$MUSIC_ROOT/logs"
rsync -a --delete \
  --exclude 'tonregal.db*' \
  --exclude 'appsettings.Production.json' \
  "$staging/" "$app/"

ip="$(tailscale_ip)"
brew_prefix="$(brew --prefix)"
plist="$staging/de.bvelop.tonregal.plist"
render_template "$DEPLOY_DIR/launchd/de.bvelop.tonregal.plist.template" "$plist" \
  MUSIC_ROOT="$MUSIC_ROOT" TAILSCALE_IP="$ip" BREW_PREFIX="$brew_prefix"
plutil -lint "$plist" >/dev/null
install_agent de.bvelop.tonregal "$plist"

echo
echo "Tonregal läuft unter http://$ip:5100"
