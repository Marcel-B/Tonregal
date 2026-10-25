# SPDX-License-Identifier: AGPL-3.0-or-later
# Gemeinsame Einstellungen für die Deploy-Scripts auf oi-o-mat.

MUSIC_ROOT="${MUSIC_ROOT:-/Volumes/T7/Musik}"
LAUNCH_AGENTS="$HOME/Library/LaunchAgents"
DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

require_mount() {
  if [[ ! -d "$MUSIC_ROOT" ]]; then
    echo "Fehler: $MUSIC_ROOT nicht gefunden – ist die T7 gemountet?" >&2
    exit 1
  fi
  # Marker-Datei: Die App prüft sie vor jedem Scan, damit eine fehlende SSD nicht als leere Library gilt.
  touch "$MUSIC_ROOT/.tonregal-mount"
}

tailscale_ip() {
  if [[ -n "${TAILSCALE_IP:-}" ]]; then
    echo "$TAILSCALE_IP"
    return
  fi
  local cli
  cli="$(command -v tailscale || echo /Applications/Tailscale.app/Contents/MacOS/Tailscale)"
  "$cli" ip -4 | head -n1
}

# render_template <vorlage> <ziel> KEY=WERT...
render_template() {
  local src="$1" dst="$2"
  shift 2
  local content
  content="$(<"$src")"
  for pair in "$@"; do
    content="${content//@@${pair%%=*}@@/${pair#*=}}"
  done
  printf '%s\n' "$content" > "$dst"
}

# install_agent <label> <plist-datei>
install_agent() {
  local label="$1" plist="$2" domain="gui/$(id -u)"
  mkdir -p "$LAUNCH_AGENTS"
  cp "$plist" "$LAUNCH_AGENTS/$label.plist"
  launchctl bootout "$domain/$label" 2>/dev/null || true
  launchctl bootstrap "$domain" "$LAUNCH_AGENTS/$label.plist"
  launchctl kickstart -k "$domain/$label"
}
