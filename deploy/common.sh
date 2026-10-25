# SPDX-License-Identifier: AGPL-3.0-or-later
# Gemeinsame Einstellungen für die Deploy-Scripts auf oi-o-mat.

MUSIC_ROOT="${MUSIC_ROOT:-/Volumes/T7/Musik}"
LAUNCH_AGENTS="$HOME/Library/LaunchAgents"
# launchd öffnet StandardOutPath und WorkingDirectory selbst, bevor das Programm startet, und
# darf das auf externen Volumes nicht (EX_CONFIG 78, "Operation not permitted"); eine
# Festplattenvollzugriff-Freigabe für das Programm hilft dabei nicht. Deshalb liegen nur diese
# kleinen Dienst-Logs intern, alles andere bleibt auf der T7.
SERVICE_LOGS="$HOME/Library/Logs/Tonregal"
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

# install_agent <label> <plist-datei> <programm> <logdatei>
install_agent() {
  local label="$1" plist="$2" program="$3" log="$4" domain="gui/$(id -u)"
  mkdir -p "$LAUNCH_AGENTS" "$SERVICE_LOGS"
  cp "$plist" "$LAUNCH_AGENTS/$label.plist"
  launchctl bootout "$domain/$label" 2>/dev/null || true
  # bootout kehrt zurück, bevor der alte Prozess beendet ist; ein sofortiges bootstrap
  # scheitert dann mit "5: Input/output error". Also warten, bis der Dienst weg ist.
  local i
  for i in {1..20}; do
    launchctl print "$domain/$label" >/dev/null 2>&1 || break
    sleep 0.5
  done
  launchctl bootstrap "$domain" "$LAUNCH_AGENTS/$label.plist"
  launchctl kickstart -k "$domain/$label"

  sleep 5
  if launchctl print "$domain/$label" | grep -q "state = running"; then
    return 0
  fi

  echo >&2
  echo "Warnung: $label läuft nicht." >&2
  launchctl print "$domain/$label" | grep -E "last exit" >&2 || true
  [[ -f "$log" ]] && tail -n 10 "$log" >&2
  cat >&2 <<HINT

Steht im Log "Operation not permitted", fehlt dem Programm die Freigabe für die
externe SSD. Unter
  Systemeinstellungen → Datenschutz & Sicherheit → Festplattenvollzugriff
mit „+“ dieses Programm hinzufügen (im Dateidialog Cmd+Shift+G für den Pfad):
  $(realpath "$program")
und danach das Script erneut ausführen. Nach einem brew upgrade die Freigabe erneuern.
HINT
  return 1
}
