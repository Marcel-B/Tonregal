#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Meldet Tonregal und Navidrome beim Monitoring-Stack an (Tonwerk-AI, deploy/monitoring/):
#   1. schaltet in $MUSIC_ROOT/app/appsettings.Production.json /metrics und den Versand an Loki ein
#      (andere Einträge bleiben),
#   2. rollt den neuesten Stand von main aus, falls noch nicht geschehen, sonst startet es Tonregal neu,
#   3. schreibt Navidromes Konfiguration mit [Prometheus] neu (setup-navidrome.sh),
#   4. prüft beide /metrics und ob Loki antwortet,
#   5. nennt den Aufruf für den Proxmox-Host, der die Ziele in Prometheus einträgt.
# Idempotent, kann jederzeit erneut laufen.
#
#   ./deploy/monitoring-einrichten.sh
#
# Optional: LOKI_URL (http://monitoring:3100), LOKI_HOST (Name in Loki; sonst der Rechnername).
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

LOKI_URL="${LOKI_URL:-http://monitoring:3100}"
LOKI_HOST="${LOKI_HOST:-}"
repo="$(cd "$DEPLOY_DIR/.." && pwd)"
settings="$MUSIC_ROOT/app/appsettings.Production.json"
domain="gui/$(id -u)"

require_mount

busy() {
  local answer
  answer="$(curl -fsS --max-time 5 "$TONREGAL_LOCAL_URL/api/busy" 2>/dev/null)" && grep -q '"busy":true' <<<"$answer"
}

echo "==> Monitoring in $settings"
mkdir -p "$(dirname "$settings")"
# /usr/bin/python3 statt jq: auf jedem Mac da. Nur der Abschnitt Monitoring ändert sich.
/usr/bin/python3 - "$settings" "$LOKI_URL" "$LOKI_HOST" <<'PY'
import json, os, sys
path, url, host = sys.argv[1:4]
settings = {}
if os.path.exists(path) and os.path.getsize(path) > 0:
    with open(path, encoding="utf-8-sig") as f:
        settings = json.load(f)
monitoring = settings.setdefault("Monitoring", {})
monitoring["Metrics"] = True
loki = monitoring.setdefault("Loki", {})
loki["Url"] = url
if host:
    loki["Host"] = host
with open(path + ".tmp", "w", encoding="utf-8") as f:
    json.dump(settings, f, indent=2, ensure_ascii=False)
    f.write("\n")
os.replace(path + ".tmp", path)
print(json.dumps(monitoring, ensure_ascii=False))
PY

echo "==> Neuester Stand"
before="$(cat "$UPDATE_STATE/deployed" 2>/dev/null || true)"
# Eine Runde wie der LaunchAgent: rollt nur aus, wenn main weiter ist und Tonregal nichts tut.
"$DEPLOY_DIR/update-tonregal.sh" run || true
after="$(cat "$UPDATE_STATE/deployed" 2>/dev/null || true)"
git -C "$repo" fetch -q origin main 2>/dev/null || true
if [[ -n "$after" ]] && ! git -C "$repo" merge-base --is-ancestor origin/main "$after" 2>/dev/null; then
  echo "Fehler: Ausgerollt ist $(printf '%.7s' "$after"), main ist weiter, und die Runde hat nicht ausgerollt." >&2
  echo "Tonregal ist beschäftigt oder der Deploy scheiterte; siehe $SERVICE_LOGS/update.log, dann erneut ausführen." >&2
  exit 1
fi

if [[ "$before" == "$after" ]]; then
  # Nichts ausgerollt: Die neue Konfiguration gilt erst nach einem Neustart. Kein Neustart mitten im Import.
  if busy; then
    echo "Fehler: Tonregal ist beschäftigt (Import, Scan, Analyse oder Backup). Später erneut ausführen." >&2
    exit 1
  fi
  echo "==> Tonregal neu starten"
  launchctl kickstart -k "$domain/de.bvelop.tonregal"
fi

echo "==> Warten auf $TONREGAL_LOCAL_URL/metrics"
metrics=""
for _ in {1..30}; do
  metrics="$(curl -fsS --max-time 5 "$TONREGAL_LOCAL_URL/metrics" 2>/dev/null || true)"
  grep -q '^tonregal_tracks ' <<<"$metrics" && break
  sleep 2
done
if ! grep -q '^tonregal_tracks ' <<<"$metrics"; then
  echo "Fehler: $TONREGAL_LOCAL_URL/metrics liefert keine Messwerte." >&2
  tail -n 20 "$SERVICE_LOGS/tonregal.stdout.log" >&2 2>/dev/null || true
  exit 1
fi
grep -E '^tonregal_(tracks|music_root_mounted) ' <<<"$metrics" | sed 's/^/    /' || true
grep -h "Monitoring:" "$SERVICE_LOGS/tonregal.stdout.log" 2>/dev/null | tail -n 1 | sed 's/^/    /' || true

echo "==> Navidrome"
"$DEPLOY_DIR/navidrome/setup-navidrome.sh"
ip="$(tailscale_ip)"
navidrome=""
for _ in {1..15}; do
  navidrome="$(curl -fsS --max-time 5 "http://$ip:4533/metrics" 2>/dev/null || true)"
  grep -q '^navidrome_' <<<"$navidrome" && break
  sleep 2
done
if ! grep -q '^navidrome_' <<<"$navidrome"; then
  echo "Fehler: http://$ip:4533/metrics liefert keine Messwerte von Navidrome." >&2
  exit 1
fi
grep -E '^navidrome_(info|db_model_totals)' <<<"$navidrome" | sed 's/^/    /' || true

echo "==> Loki"
if curl -fsS --max-time 5 "${LOKI_URL%/}/ready" >/dev/null 2>&1; then
  echo "    $LOKI_URL antwortet."
else
  echo "Warnung: $LOKI_URL antwortet nicht. Läuft der Stack, und löst oi-o-mat den Namen im Tailnet auf?" >&2
fi

cli="$(command -v tailscale || echo /Applications/Tailscale.app/Contents/MacOS/Tailscale)"
name="$("$cli" status --json | /usr/bin/python3 -c 'import json, sys; print(json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))')"
cat <<NEXT

Fertig auf $(hostname -s). Jetzt auf dem Proxmox-Host als root die Ziele eintragen (holt den Stack frisch von
GitHub, samt Dashboard „Tonregal“, und startet ihn neu; .env und die anderen Ziele bleiben):

  curl -fsSL https://codeload.github.com/Marcel-B/Tonwerk-AI/tar.gz/main | tar -xz --strip-components=2 -C /root Tonwerk-AI-main/deploy/monitoring
  APP_TARGETS="tonregal=$name:443 navidrome=$name:4533" /root/monitoring/proxmox-lxc.sh

Danach zeigt http://monitoring:9090/targets beide Ziele, Grafana das Dashboard „Tonregal“.
NEXT
