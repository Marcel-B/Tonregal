# Tonregal

Tonregal verwaltet die eigene Musikbibliothek auf oi-o-mat: Inbox überwachen, Fingerprint,
MusicBrainz-Abgleich, Cover, Tags schreiben, Dateien in eine feste Ordnerstruktur einsortieren,
Dubletten erkennen, BPM/Tonart/ReplayGain analysieren und alles nach Nextcloud sichern.
Gestreamt wird die fertige Library von [Navidrome](https://www.navidrome.org/) über die
Subsonic-API ins Tailnet.

**Leitprinzip:** Die Dateien sind die Wahrheit. Alle Metadaten stehen in den Tags der Audiodatei,
die Datenbank ist nur ein Index und lässt sich jederzeit aus der Library neu aufbauen.

## Aufbau

| Projekt | Inhalt |
|---|---|
| `src/Tonregal.Domain` | Entities, Value Objects (`TrackKey`, `TrackIdentity`, `LibraryPath`), keine Abhängigkeiten |
| `src/Tonregal.Application` | Use Cases und Schnittstellen für Tagging, Fingerprint, Metadaten-Lookup |
| `src/Tonregal.Infrastructure` | EF Core + SQLite, später TagLib-, fpcalc-, MusicBrainz-, rclone-Adapter |
| `src/Tonregal.Web` | ASP.NET-Core-Host: Minimal-API unter `/api`, SignalR-Hub unter `/hub`, liefert die Vue-SPA aus |
| `src/Tonregal.Web/ClientApp` | Vue 3, TypeScript, Vite, Pinia, Vue Router, Tailwind CSS, PrimeVue 4 |
| `tests/Tonregal.Tests` | xUnit v3 (Domain, Datenbank, API); UI-Tests mit Vitest in der ClientApp |
| `deploy/` | Setup- und Deploy-Scripts, launchd-Vorlagen, Shell-Funktionen für oi-o-mat |

**Library-Scan:** Beim Start und per Knopf in der Bibliothek liest Tonregal alle Audiodateien unter
`Library/` (MP3, FLAC, M4A, Ogg/Opus, WAV) per TagLibSharp ein und gleicht den Index ab. Unveränderte
Dateien (Größe und Änderungszeit) werden übersprungen, verschwundene entfernt; ohne Mount-Marker
scannt die App nicht. Punkt-Ordner wie `.trash` und `._*`-Dateien gehören nicht zur Library.

Ordnerregel der Library: `Library/{AlbumArtist}/{Year} - {Album}/{Disc-}{NN} - {Title}.{ext}`,
Compilations unter `Library/Various Artists/…`.

## Entwicklung

Voraussetzungen: .NET 10 SDK, Node.js 22.

```bash
# Backend (Development-Profil, Musikordner unter .dev/Musik im Repo)
dotnet run --project src/Tonregal.Web          # http://localhost:5100

# Frontend mit Hot Reload, /api und /hub gehen per Proxy an das Backend
cd src/Tonregal.Web/ClientApp
npm install
npm run dev                                    # http://localhost:5173

# Tests
dotnet test
(cd src/Tonregal.Web/ClientApp && npm test)
```

Neue Migration:

```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/Tonregal.Infrastructure -s src/Tonregal.Infrastructure -o Persistence/Migrations
```

Die App migriert die Datenbank beim Start selbst.

## Betrieb auf oi-o-mat

Alles liegt unter `/Volumes/T7/Musik` und ist nur über die Tailscale-IP erreichbar.

| Pfad | Inhalt |
|---|---|
| `Inbox/` | neue Käufe und alte MP3-Kopien (Unterordner = Album-Kandidat) |
| `Library/` | fertige Bibliothek, schreibt nur Tonregal |
| `app/` | veröffentlichte App und `tonregal.db` |
| `navidrome/` | `navidrome.toml`, `navidrome.db`, Cache |
| `logs/` | Logs von Tonregal und Navidrome |

### Stufe 0: Navidrome

```bash
./deploy/navidrome/setup-navidrome.sh
```

Installiert Navidrome und ffmpeg per Homebrew, legt die Ordner an, schreibt `navidrome.toml`
(Library-Ordner, Bind an die Tailscale-IP, Port 4533, Scan alle 15 Minuten) und richtet den
launchd-Dienst `org.navidrome.server` ein. Danach im Browser den Admin-Benutzer anlegen und
Amperfy (iPhone) bzw. play:Sub (Mac) mit `http://oi-o-mat.tail6105c0.ts.net:4533` verbinden.

### Tonregal

```bash
./deploy/deploy-tonregal.sh
```

Baut die App inklusive SPA self-contained für `osx-arm64`, kopiert sie nach `app/` (Datenbank und
`appsettings.Production.json` bleiben erhalten) und richtet den launchd-Dienst
`de.bvelop.tonregal` auf Port 5100 ein. Eigene Einstellungen und API-Keys gehören in
`/Volumes/T7/Musik/app/appsettings.Production.json`, nie ins Repo.

**Zugriff auf die T7:** launchd selbst darf keine Dateien auf externen Volumes öffnen, deshalb
liegen nur die Konsolen-Logs der Dienste (`navidrome.log`, `tonregal.stdout.log`) intern unter
`~/Library/Logs/Tonregal`. Die Programme brauchen für die T7 Festplattenvollzugriff: Startet ein
Dienst nicht, nennt das Script den Programmpfad; diesen unter Systemeinstellungen → Datenschutz &
Sicherheit → Festplattenvollzugriff hinzufügen und das Script erneut ausführen. Nach `brew upgrade navidrome`
ändert sich der Pfad, dann die Freigabe erneuern.

**Metadaten-Abgleich:** Fingerprints berechnet `fpcalc` (Chromaprint, installiert das Deploy-Script).
Für AcoustID braucht es einen kostenlosen Client-Key von https://acoustid.org/new-application, der in
`/Volumes/T7/Musik/app/appsettings.Production.json` eingetragen wird:

```json
{ "Tonregal": { "Metadata": { "AcoustIdApiKey": "…" } } }
```

MusicBrainz-Antworten werden 30 Tage in der Datenbank gecacht; Anfragen sind auf 1/s (MusicBrainz)
bzw. 3/s (AcoustID) begrenzt.

**Inbox:** Tonregal überwacht `/Volumes/T7/Musik/Inbox` (plus Vollscan beim Start und alle 5 Minuten).
Eine Datei wird eingelesen, sobald ihre Größe 10 s stabil ist; ein Unterordner gilt als ein Album.
Jede Datei bekommt einen Import-Job: Tags, Hash und Fingerprint, dann AcoustID und MusicBrainz.
Ein eindeutiger Treffer ab 90 % ohne Widerspruch zu den Tags ist *Erkannt*, alles andere landet mit
Begründung unter *Prüfen*. Stimmt die Mehrheit der Dateien eines Ordners auf ein Release, bekommen alle
dieses Release. Die Dateien in der Inbox werden dabei nicht verändert.

**Übernehmen:** Auf der Inbox-Seite lässt sich ein erkannter Treffer einzeln, pro Ordner oder für alle
übernehmen; bei *Prüfen* wählt man zuerst in der Kandidatenliste das richtige Release. Tonregal schreibt
dann Tags (MP3 als ID3v2.4, Feldnamen wie Picard) und Cover in eine Kopie, prüft sie, benennt sie atomar
nach der Ordnerregel `Library/{AlbumArtist}/{Jahr} - {Album}/{Disc-}{NN} - {Titel}.{ext}` um und löscht
erst dann das Original aus der Inbox. `cover.jpg` im Albumordner kommt aus der Datei oder dem Cover Art
Archive (hat die Ausgabe dort keins, das Standard-Cover des Albums); fehlt es, holt *Cover suchen* auf
der Albumseite es nach. *Verwerfen* lässt die Datei in der Inbox liegen, ohne sie erneut abzugleichen.
Dateien und Ordner lassen sich auch per Drag & Drop auf die Inbox-Seite hochladen; lose Dateien landen
gemeinsam in einem Ordner `Upload {Datum Uhrzeit}`.

**Dubletten:** Nach jedem Library-Scan sucht Tonregal Dateien mit gleichem Hash, gleicher Aufnahme
(MusicBrainz-Recording-ID oder AcoustID) oder gleichem Interpret, Titel und Dauer (±2 s). MP3 und FLAC
desselben Tracks gelten nicht als Dublette. Auf der Seite *Dubletten* hört man jede Datei 10 Sekunden an
und wählt, was bleibt; der Vorschlag ist verlustfrei vor verlustbehaftet, dann die höhere Bitrate. Der
Rest wandert nach `Library/.trash/{Datum}/` und wird nach 30 Tagen gelöscht. Eine Inbox-Datei, die
byte-gleich schon in der Library liegt, geht direkt in den Papierkorb; ist nur der Zielplatz belegt,
landet sie als `… (2)` daneben und erscheint als Dublette.

Beide Dienste laufen nur, solange die T7 gemountet ist (`KeepAlive`/`PathState` auf
`/Volumes/T7/Musik/.tonregal-mount`), und starten nach einem Remount von selbst neu.

Shell-Funktionen für beide Macs:

```bash
source deploy/shell/tonregal.zsh   # in ~/.zshrc
tonregal-status
tonregal-log [tonregal|navidrome]
tonregal-restart [tonregal|navidrome]
```

## Lizenz

[GNU Affero General Public License v3.0 oder später](LICENSE). Navidrome, Chromaprint, ffmpeg,
rsgain und rclone werden nur als eigenständige Prozesse aufgerufen, nicht gelinkt.
