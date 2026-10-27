# Tonregal fürs iPhone

Ein nativer Player für die Library auf oi-o-mat. Er spricht Tonregals API über Tailscale, streamt wie der Web-Player
über `/api/player/files/{id}/stream` (also Navidrome dahinter, mit Play-Counts) und spielt im Hintergrund weiter:
Sperrbildschirm, Kontrollzentrum, AirPods, AirPlay, Unterbrechung durch Anrufe.

Stand 0.1: Alben (zuletzt übernommene zuerst) und Interpreten mit Suche, Albumseite mit BPM/Tonart, Abspielen und
Zufällig, „Als Nächstes“ / „Hinten anhängen“ (Wischen oder langes Drücken), Warteschlange mit Verschieben und Löschen,
die über App-Neustarts erhalten bleibt, Scrobble an Navidrome nach der Hälfte bzw. 4 Minuten. Noch nicht: Offline-Cache,
CarPlay, Playlists.

## Verbinden

Tailscale muss auf dem iPhone laufen. Beim ersten Start die Adresse von Tonregal eintragen, wie im Browser:
`oi-o-mat` (MagicDNS) oder die Tailscale-IP `100.x.y.z`. Ohne Angabe ergänzt die App `http://` und Port `5100`.
Weil Tonregal ohne TLS läuft (Tailscale verschlüsselt die Strecke), erlaubt die App Klartext-HTTP
(`Support/Info.plist`); für TestFlight intern ist das kein Problem.

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
   `APPLE_TEAM_ID`, `ASC_KEY_ID`, `ASC_ISSUER_ID`, `ASC_KEY_P8` (derselbe API-Key geht).

Dann *Actions → TestFlight → Run workflow*. Der Workflow archiviert, signiert und lädt hoch (Build-Nummer 1000 plus die
Laufnummer). Nach der Verarbeitung in App Store Connect unter *TestFlight* sich selbst als internen Tester eintragen und
die App über die TestFlight-App installieren. Interne Builds brauchen keine Prüfung und laufen 90 Tage.

Verschlüsselung (`ITSAppUsesNonExemptEncryption = NO`) und das Privacy-Manifest (`Tonregal/PrivacyInfo.xcprivacy`) sind
gesetzt.
