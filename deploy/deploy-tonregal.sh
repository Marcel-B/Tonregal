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

echo "==> Homebrew-Pakete"
for package in chromaprint rsgain uv rclone; do
  brew list "$package" >/dev/null 2>&1 || brew install "$package"
done

# Analyse-venv (librosa) samt Python und uv-Cache auf der T7, nicht auf der internen SSD.
echo "==> Analyse-venv"
tools="$MUSIC_ROOT/tools"
export UV_CACHE_DIR="$tools/uv-cache" UV_PYTHON_INSTALL_DIR="$tools/uv-python"
[[ -x "$tools/analysis-venv/bin/python" ]] || uv venv --python 3.12 "$tools/analysis-venv"
uv pip install --python "$tools/analysis-venv/bin/python" -q -r "$repo/src/Tonregal.Web/analysis/requirements.txt"

echo "==> dotnet publish"
dotnet publish "$repo/src/Tonregal.Web" -c Release -r osx-arm64 --self-contained -o "$staging"

echo "==> Kopieren nach $app"
mkdir -p "$app" "$MUSIC_ROOT/logs"
rsync -a --delete \
  --exclude 'tonregal.db*' \
  --exclude 'appsettings.Production.json' \
  "$staging/" "$app/"

# Die Tailscale-IP nur noch für Navidromes Adresse; Tonregal selbst lauscht auf localhost.
navidrome_ip="$(tailscale_ip)"
brew_prefix="$(brew --prefix)"
plist="$staging/de.bvelop.tonregal.plist"
render_template "$DEPLOY_DIR/launchd/de.bvelop.tonregal.plist.template" "$plist" \
  MUSIC_ROOT="$MUSIC_ROOT" TAILSCALE_IP="$navidrome_ip" BREW_PREFIX="$brew_prefix" \
  HOME="$HOME" SERVICE_LOGS="$SERVICE_LOGS"
plutil -lint "$plist" >/dev/null
install_agent de.bvelop.tonregal "$plist" "$app/Tonregal.Web" "$SERVICE_LOGS/tonregal.stdout.log"

# Erst fertig, wenn Tonregal antwortet; der Start wendet Migrationen an und braucht ein paar Sekunden.
echo "==> Health-Check $TONREGAL_LOCAL_URL/api/health"
healthy=false
for _ in {1..30}; do
  if curl -fsS --max-time 5 "$TONREGAL_LOCAL_URL/api/health" >/dev/null 2>&1; then
    healthy=true
    break
  fi
  sleep 2
done
if [[ "$healthy" != true ]]; then
  echo "Fehler: $TONREGAL_LOCAL_URL/api/health antwortet nicht." >&2
  tail -n 20 "$SERVICE_LOGS/tonregal.stdout.log" >&2 2>/dev/null || true
  exit 1
fi

# Für update-tonregal.sh: Was hier ausgerollt wurde, muss es nicht noch einmal tun. Erst nach dem
# Health-Check, damit ein Deploy, nach dem Tonregal nicht antwortet, nicht als ausgerollt gilt.
mkdir -p "$MUSIC_ROOT/update"
git -C "$repo" rev-parse HEAD > "$MUSIC_ROOT/update/deployed"

echo
echo "Tonregal läuft unter $TONREGAL_PUBLIC_URL (Tailscale Serve → $TONREGAL_LOCAL_URL)"
