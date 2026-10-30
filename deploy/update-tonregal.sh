#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Hält Tonregal auf oi-o-mat aktuell: Neue Commits auf main werden mit deploy-tonregal.sh
# ausgerollt, sobald Tonregal nichts tut, was ein Neustart abschneiden würde (Import, Inbox,
# Scan, Analyse, Backup; GET /api/busy). Vorlage ist Tonwerk-AIs deploy/update.sh.
#
#   ./deploy/update-tonregal.sh              eine Runde, so wie der LaunchAgent sie ausführt
#   ./deploy/update-tonregal.sh on [min]     alle paar Minuten (Standard 5) per LaunchAgent prüfen
#   ./deploy/update-tonregal.sh off          das abschalten
#   ./deploy/update-tonregal.sh status       läuft es, was ist ausgerollt, Ende des Logs
#
# Der Checkout muss auf main stehen; auf einem anderen Branch tut die Runde nichts und sagt das.
# Ein Commit, dessen Deploy scheitert, wird nicht alle paar Minuten neu versucht, erst der nächste
# (oder deploy-tonregal.sh von Hand).
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

repo="$(cd "$DEPLOY_DIR/.." && pwd)"
cd "$repo"

LABEL=de.bvelop.tonregal-update
# Zustand und Log intern (zusammen wenige KB): Fehlt dem Agent der Zugriff auf die T7, muss er das
# trotzdem festhalten und melden können. Früher lagen beide auf der T7; dann las der Agent ohne
# Zugriff keinen ausgerollten Commit, nahm HEAD und hielt einen gescheiterten Deploy für erledigt.
STATE="$UPDATE_STATE"
OLD_STATE="$MUSIC_ROOT/update"
LOG="$SERVICE_LOGS/update.log"
AGENT_LOG="$SERVICE_LOGS/update.stdout.log"
DB_BACKUPS="$MUSIC_ROOT/backups/deploy"
DB_BACKUP_KEEP=10

log() {
  local line
  line="$(date '+%Y-%m-%d %H:%M:%S') $*"
  printf '%s\n' "$line" >> "$LOG" 2>/dev/null || printf '%s\n' "$line" >&2
}

short() { printf '%s' "${1:0:7}"; }

# Die Datenbank vor jedem Deploy, da der Start neue EF-Migrationen anwendet.
backup_db() {
  local db="$MUSIC_ROOT/app/tonregal.db" target
  [[ -f "$db" ]] || return 0
  target="$DB_BACKUPS/tonregal-$(date +%Y-%m-%d_%H%M%S).db"
  mkdir -p "$DB_BACKUPS"
  # .backup statt cp: Tonregal kann gerade schreiben (WAL).
  sqlite3 "$db" ".backup '$target'"
  # shellcheck disable=SC2012 # die Namen sind unsere eigenen Zeitstempel
  ls -1 "$DB_BACKUPS"/tonregal-20*.db 2>/dev/null | sort -r | tail -n +$((DB_BACKUP_KEEP + 1)) | while read -r old; do rm -f "$old"; done
}

# Einmalig den Zustand von der T7 übernehmen (Versionen vor dem internen Zustand).
migrate_state() {
  [[ -f "$STATE/deployed" ]] && return 0
  mkdir -p "$STATE"
  cp "$OLD_STATE/deployed" "$STATE/deployed" 2>/dev/null || true
}

round() {
  if [[ ! -d "$MUSIC_ROOT" ]]; then
    echo "$MUSIC_ROOT nicht gemountet; nichts getan." >&2
    return 0
  fi
  # Ein bestehender Ordner sagt nichts über die Freigabe (mkdir -p schreibt dann nicht); touch schon.
  if ! touch "$MUSIC_ROOT/.tonregal-mount" 2>/dev/null; then
    if [[ ! -f "$STATE/no-access" ]]; then
      log "Kein Zugriff auf $MUSIC_ROOT: /bin/bash fehlt der Festplattenvollzugriff (Systemeinstellungen → Datenschutz & Sicherheit). Nichts ausgerollt."
      touch "$STATE/no-access"
    fi
    return 1
  fi
  if [[ -f "$STATE/no-access" ]]; then
    log "Zugriff auf $MUSIC_ROOT ist wieder da."
    rm -f "$STATE/no-access"
  fi
  export GIT_TERMINAL_PROMPT=0

  local branch
  branch="$(git symbolic-ref --short HEAD 2>/dev/null || true)"
  if [[ "$branch" != main ]]; then
    # Nur einmal je Branch melden, nicht alle fünf Minuten.
    if [[ "$(cat "$STATE/off-main" 2>/dev/null)" != "${branch:-detached}" ]]; then
      log "Der Checkout steht auf ${branch:-keinem Branch}, nicht auf main; nichts ausgerollt. Zurück mit: git -C $repo switch main"
      echo "${branch:-detached}" > "$STATE/off-main"
    fi
    return 1
  fi
  rm -f "$STATE/off-main"

  git fetch -q origin main || { log "git fetch fehlgeschlagen."; return 1; }
  # Der zuletzt ausgerollte Commit, nie HEAD: Nach einem gescheiterten Deploy ist der Checkout schon
  # weiter. Ohne Eintrag (vor dem ersten Deploy) wird ausgerollt.
  local deployed remote
  deployed="$(cat "$STATE/deployed" 2>/dev/null || true)"
  remote="$(git rev-parse origin/main)"
  if [[ -n "$deployed" ]] && { [[ "$deployed" == "$remote" ]] || git merge-base --is-ancestor "$remote" "$deployed" 2>/dev/null; }; then
    rm -f "$STATE/waiting"
    return 0
  fi
  [[ "$(cat "$STATE/failed" 2>/dev/null)" == "$remote" ]] && return 0

  # Antwortet Tonregal nicht, gibt es nichts abzuschneiden. Eine alte Version ohne /api/busy
  # (404) zählt ebenso als frei, das betrifft nur den ersten automatischen Deploy.
  local url busy
  url="$TONREGAL_LOCAL_URL"
  if busy="$(curl -fsS --max-time 5 "$url/api/busy" 2>/dev/null)" && grep -q '"busy":true' <<<"$busy"; then
    if [[ "$(cat "$STATE/waiting" 2>/dev/null)" != "$remote" ]]; then
      log "$(short "$remote") wartet: Tonregal ist beschäftigt ($busy)."
      echo "$remote" > "$STATE/waiting"
    fi
    return 0
  fi
  rm -f "$STATE/waiting"

  log "Rolle $(short "${deployed:-?}") -> $(short "$remote") aus:"
  [[ -n "$deployed" ]] && { git log --format='    %h %s' "$deployed..$remote" >> "$LOG" 2>/dev/null || true; }
  backup_db || log "Sicherung der Datenbank fehlgeschlagen; rolle trotzdem aus."
  if git merge -q --ff-only origin/main && "$DEPLOY_DIR/deploy-tonregal.sh" >> "$LOG" 2>&1; then
    rm -f "$STATE/failed"
    # deploy-tonregal.sh hat "deployed" schon geschrieben; hier nur zur Sicherheit.
    echo "$remote" > "$STATE/deployed"
    log "$(short "$remote") ausgerollt."
  else
    echo "$remote" > "$STATE/failed"
    log "Deploy von $(short "$remote") fehlgeschlagen (Ausgabe oben). Der nächste Commit versucht es erneut, oder von Hand: ./deploy/deploy-tonregal.sh"
    return 1
  fi
}

case "${1:-run}" in
  run)
    if [[ -f "$LOG" ]] && [[ "$(wc -c < "$LOG")" -gt 1048576 ]]; then
      tail -n 2000 "$LOG" > "$LOG.tmp" && mv "$LOG.tmp" "$LOG"
    fi
    # Gegen eine Runde von Hand, während die des Agents ausrollt. Liegt intern, damit auch
    # eine fehlende T7 die Runde nicht hängen lässt.
    lock="$SERVICE_LOGS/update.lock"
    mkdir -p "$SERVICE_LOGS" "$STATE"
    migrate_state
    mkdir "$lock" 2>/dev/null || { echo "Eine andere Runde läuft gerade."; exit 0; }
    trap 'rmdir "$lock"' EXIT
    round
    ;;
  on)
    minutes="${2:-5}"
    [[ "$minutes" =~ ^[0-9]+$ ]] || { echo "Aufruf: $0 on [Minuten]" >&2; exit 2; }
    (( minutes >= 1 )) || minutes=1
    plist="$LAUNCH_AGENTS/$LABEL.plist"
    mkdir -p "$LAUNCH_AGENTS" "$SERVICE_LOGS"
    # PATH aus der aufrufenden Shell: brew, dotnet, npm, git und tailscale fehlen in launchds PATH.
    # Direkt /bin/bash statt einer Login-Shell, damit genau dieses Programm die Freigabe für die T7 braucht.
    cat > "$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>/bin/bash</string>
    <string>$repo/deploy/update-tonregal.sh</string>
    <string>run</string>
  </array>
  <key>EnvironmentVariables</key>
  <dict>
    <key>PATH</key>
    <string>$PATH</string>
    <key>MUSIC_ROOT</key>
    <string>$MUSIC_ROOT</string>
  </dict>
  <key>StartInterval</key>
  <integer>$((minutes * 60))</integer>
  <key>RunAtLoad</key>
  <true/>
  <key>StandardOutPath</key>
  <string>$AGENT_LOG</string>
  <key>StandardErrorPath</key>
  <string>$AGENT_LOG</string>
</dict>
</plist>
EOF
    plutil -lint "$plist" >/dev/null
    domain="gui/$(id -u)"
    launchctl bootout "$domain/$LABEL" 2>/dev/null || true
    for _ in {1..20}; do
      launchctl print "$domain/$LABEL" >/dev/null 2>&1 || break
      sleep 0.5
    done
    # Vor dem Start, denn RunAtLoad rollt sofort aus; aus dem Terminal ist die T7 sicher lesbar.
    migrate_state
    launchctl bootstrap "$domain" "$plist"
    echo "Automatische Deploys an: main wird alle $minutes Minute(n) geprüft und ausgerollt, sobald Tonregal frei ist."
    echo "Log: $LOG (status zeigt, ob der Agent die T7 erreicht)"
    echo "Einmalig nötig: /bin/bash unter Systemeinstellungen → Datenschutz & Sicherheit → Festplattenvollzugriff"
    echo "hinzufügen (Cmd+Shift+G, /bin/bash), sonst darf der Agent nicht auf $MUSIC_ROOT schreiben."
    ;;
  off)
    launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
    rm -f "$LAUNCH_AGENTS/$LABEL.plist"
    echo "Automatische Deploys aus."
    ;;
  status)
    if launchctl print "gui/$(id -u)/$LABEL" >/dev/null 2>&1; then
      echo "Automatische Deploys an."
    else
      echo "Automatische Deploys aus ($0 on)."
    fi
    echo "Branch: $(git symbolic-ref --short HEAD 2>/dev/null || echo 'kein Branch')"
    echo "Ausgerollt: $(cut -c1-7 "$STATE/deployed" 2>/dev/null || echo unbekannt)"
    if [[ -f "$STATE/waiting" ]]; then echo "Wartet: $(cut -c1-7 "$STATE/waiting")"; fi
    if [[ -f "$STATE/failed" ]]; then echo "Fehlgeschlagen: $(cut -c1-7 "$STATE/failed")"; fi
    if [[ -f "$STATE/no-access" ]]; then echo "Kein Zugriff auf $MUSIC_ROOT: /bin/bash braucht Festplattenvollzugriff."; fi
    if [[ -s "$AGENT_LOG" ]]; then echo "--- $AGENT_LOG"; tail -n 5 "$AGENT_LOG"; fi
    if [[ -f "$LOG" ]]; then echo "--- $LOG"; tail -n 15 "$LOG"; fi
    ;;
  -h|--help) sed -n '4,15p' "$0" | sed 's/^# \{0,1\}//' ;;
  *) echo "Aufruf: $0 [run|on [Minuten]|off|status]" >&2; exit 2 ;;
esac
