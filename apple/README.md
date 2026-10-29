# Tonregal fürs iPhone

Ein nativer Player für die Library auf oi-o-mat. Er spricht Tonregals API über Tailscale, streamt wie der Web-Player
über `/api/player/files/{id}/stream` (also Navidrome dahinter, mit Play-Counts) und spielt im Hintergrund weiter:
Sperrbildschirm, Kontrollzentrum, AirPods, AirPlay, Unterbrechung durch Anrufe.

Stand 0.1: Alben (zuletzt übernommene zuerst) und Interpreten mit Suche, Albumseite mit BPM/Tonart, Abspielen und
Zufällig, „Als Nächstes“ / „Hinten anhängen“ (Wischen oder langes Drücken), Warteschlange mit Verschieben und Löschen,
die über App-Neustarts erhalten bleibt, Scrobble an Navidrome nach der Hälfte bzw. 4 Minuten. Noch nicht: Offline-Cache,
CarPlay, Playlists.

## Verbinden

Tailscale muss auf dem iPhone laufen. Tonregal lauscht auf oi-o-mat nur auf `localhost:5100`, Tailscale Serve
veröffentlicht es im Tailnet unter `https://oi-o-mat.tail6105c0.ts.net`. Diese Adresse steht beim ersten Start schon
im Feld. Eine vollständige URL bleibt, wie sie ist; nur ein bloßer Hostname oder eine IP bekommt `http://` und Port
`5100` (für einen Entwicklungsserver). Für solche http-Adressen erlaubt die App weiterhin Klartext
(`Support/Info.plist`). Wer noch die alte Adresse `http://<Tailscale-IP>:5100` gespeichert hat, trennt die Verbindung
und trägt die neue ein.

## Aufbau

- `Tonregal/` – die App: Seiten (`RootView`, `LibraryViews`, `PlayerViews`), `AppModel`, `Player` (AVPlayer, Now Playing,
  Remote Commands), Cover-Cache. Der Ordner ist mit dem Projekt synchronisiert, neue Dateien brauchen keine Projektänderung.
- `TonregalKit/` – Swift-Paket ohne UIKit: API-Typen (Spiegel von `LibraryApi.cs`), Client, Adresse, Warteschlange,
  Scrobble-Regel. `swift test` dort läuft auf jedem Mac und in der CI.
- `Support/` – Info.plist-Ergänzungen und `Signing.xcconfig`.

## Starten (iPhone, aus Xcode)

1. `apple/Tonregal.xcodeproj` öffnen.
2. Team-ID in `Support/Local.xcconfig` eintragen (von git ignoriert, das Repo ist öffentlich):
   ```
   DEVELOPMENT_TEAM = ABCDE12345
   ```
   In *Signing & Capabilities* das Team leer lassen, sonst schreibt Xcode es in `project.pbxproj`.
3. iPhone als Ziel, ⌘R.

## TestFlight

Einmalig:

1. In App Store Connect unter *Apps → +* eine iOS-App mit der Bundle-ID `de.bvelop.tonregal` anlegen (falls die
   Bundle-ID dort fehlt: developer.apple.com → Identifiers → +, App IDs, `de.bvelop.tonregal`).
2. Im Repo unter *Settings → Secrets and variables → Actions* dieselben vier Secrets wie bei Tonwerk-AI anlegen:
   `APPLE_TEAM_ID`, `ASC_KEY_ID`, `ASC_ISSUER_ID`, `ASC_KEY_P8` (derselbe API-Key geht). Am einfachsten mit
   `apple/set-testflight-secrets.sh`: fragt die Werte im Terminal ab (die `.p8` als Pfad) und setzt sie mit `gh`.

Dann *Actions → TestFlight → Run workflow*. Der Workflow archiviert, signiert und lädt hoch (Build-Nummer 1000 plus die
Laufnummer). Nach der Verarbeitung in App Store Connect unter *TestFlight* sich selbst als internen Tester eintragen und
die App über die TestFlight-App installieren. Interne Builds brauchen keine Prüfung und laufen 90 Tage.

Verschlüsselung (`ITSAppUsesNonExemptEncryption = NO`) und das Privacy-Manifest (`Tonregal/PrivacyInfo.xcprivacy`) sind
gesetzt.
