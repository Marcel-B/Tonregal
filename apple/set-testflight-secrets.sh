#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Fragt die vier Secrets für .github/workflows/testflight.yml im Terminal ab und setzt sie mit der GitHub-CLI im Repo.
# GitHub gibt gesetzte Secrets nie wieder heraus; die Werte stehen in App Store Connect (Benutzer und Zugriff →
# Integrationen → App Store Connect API) und unter developer.apple.com → Account → Membership.
#
#   brew install gh && gh auth login     # einmalig
#   apple/set-testflight-secrets.sh      # optional: --repo Besitzer/Name
set -euo pipefail

repo="Marcel-B/Tonregal"
if [[ "${1:-}" == "--repo" && -n "${2:-}" ]]; then
  repo="$2"
fi

command -v gh >/dev/null || { echo "GitHub-CLI fehlt: brew install gh" >&2; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "Nicht angemeldet: gh auth login" >&2; exit 1; }

echo "Secrets für $repo (TestFlight-Upload der iPhone-App)"
echo "Leere Eingabe lässt ein Secret unverändert."
echo

ask() {
  local name="$1" prompt="$2" pattern="$3" value
  while true; do
    read -r -p "$prompt: " value
    value="${value//[[:space:]]/}"
    [[ -z "$value" ]] && { echo "  $name bleibt, wie es ist."; return; }
    [[ "$value" =~ $pattern ]] && break
    echo "  Das sieht nicht richtig aus, nochmal."
  done
  printf '%s' "$value" | gh secret set "$name" --repo "$repo"
  echo "  $name gesetzt."
}

ask APPLE_TEAM_ID "Team-ID (10 Zeichen, developer.apple.com → Membership)" '^[A-Z0-9]{10}$'
ask ASC_KEY_ID "Key-ID des App-Store-Connect-API-Keys (10 Zeichen)" '^[A-Z0-9]{10}$'
ask ASC_ISSUER_ID "Issuer-ID (UUID über der Key-Liste)" '^[0-9a-fA-F-]{36}$'

while true; do
  read -r -e -p "Pfad zur AuthKey_<id>.p8 (z. B. ~/Downloads/AuthKey_XXXX.p8): " key
  # Reingezogene Dateien kommen mit Anführungszeichen, maskierten Leerzeichen oder einem Leerzeichen am Ende.
  key="${key#"${key%%[![:space:]]*}"}"; key="${key%"${key##*[![:space:]]}"}"
  key="${key#[\"\']}"; key="${key%[\"\']}"; key="${key//\\ / }"
  [[ -z "$key" ]] && { echo "  ASC_KEY_P8 bleibt, wie es ist."; break; }
  key="${key/#\~/$HOME}"
  if [[ ! -e "$key" ]]; then
    echo "  Datei nicht gefunden: $key"
    continue
  fi
  # macOS verweigert dem Terminal ohne Freigabe den Zugriff auf Downloads, Dokumente und Schreibtisch.
  if ! head -c 1 "$key" >/dev/null 2>&1; then
    echo "  Die Datei ist da, aber nicht lesbar. Vermutlich fehlt dem Terminal die Freigabe für diesen Ordner"
    echo "  (Systemeinstellungen → Datenschutz & Sicherheit → Dateien und Ordner). Oder die Datei vorher kopieren:"
    echo "  cp \"$key\" ~/"
    continue
  fi
  if ! grep -q "PRIVATE KEY" "$key"; then
    echo "  Die Datei enthält keinen privaten Schlüssel. Erste Zeile: $(head -n 1 "$key" | cut -c 1-60)"
    continue
  fi
  gh secret set ASC_KEY_P8 --repo "$repo" < "$key"
  echo "  ASC_KEY_P8 gesetzt."
  break
done

echo
echo "Im Repo vorhanden:"
gh secret list --repo "$repo"
echo
echo "Fehlt noch die App in App Store Connect (Bundle-ID de.bvelop.tonregal)? Dann dort anlegen,"
echo "danach: gh workflow run testflight.yml --repo $repo   (geht erst, wenn der Workflow auf main liegt)"
