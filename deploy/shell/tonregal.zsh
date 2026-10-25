# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Shell-Funktionen für Tonregal und Navidrome, analog zu comfy-*.
# In ~/.zshrc einbinden:  source /pfad/zu/Tonregal/deploy/shell/tonregal.zsh
# Am zweiten Mac laufen dieselben Befehle per SSH auf oi-o-mat (TONREGAL_HOST setzen).

: "${TONREGAL_HOST:=oi-o-mat}"
: "${TONREGAL_MUSIC_ROOT:=/Volumes/T7/Musik}"

_tonregal_run() {
  if [[ "$(hostname -s)" == "$TONREGAL_HOST" ]]; then
    zsh -c "$1"
  else
    ssh -t "$TONREGAL_HOST" "$1"
  fi
}

tonregal-status() {
  _tonregal_run "launchctl print gui/\$(id -u)/de.bvelop.tonregal 2>/dev/null | grep -E 'state|pid' | head -2;
    launchctl print gui/\$(id -u)/org.navidrome.server 2>/dev/null | grep -E 'state|pid' | head -2;
    curl -fsS http://\$(tailscale ip -4 | head -n1):5100/api/health && echo"
}

tonregal-log() {
  local service="${1:-tonregal}"
  case "$service" in
    tonregal) _tonregal_run "tail -n 200 -f $TONREGAL_MUSIC_ROOT/logs/tonregal-\$(date +%Y%m%d).log" ;;
    navidrome) _tonregal_run "tail -n 200 -f \$HOME/Library/Logs/Tonregal/navidrome.log" ;;
    *) echo "Aufruf: tonregal-log [tonregal|navidrome]" >&2; return 1 ;;
  esac
}

tonregal-restart() {
  local label
  case "${1:-tonregal}" in
    tonregal) label=de.bvelop.tonregal ;;
    navidrome) label=org.navidrome.server ;;
    *) echo "Aufruf: tonregal-restart [tonregal|navidrome]" >&2; return 1 ;;
  esac
  _tonregal_run "launchctl kickstart -k gui/\$(id -u)/$label"
}

# Backup-Endpunkt kommt mit Stufe 2 (Backup-Scheduler).
tonregal-backup-now() {
  _tonregal_run "curl -fsS -X POST http://\$(tailscale ip -4 | head -n1):5100/api/backup/run && echo"
}
