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
`de.bvelop.tonregal` auf Port 5100 ein. Das Backend lauscht nur auf `localhost:5100`; nach außen
veröffentlicht es Tailscale Serve im Tailnet unter `https://oi-o-mat.tail6105c0.ts.net` (Port 443, TLS),
das ist der einzige Zugang, auch für die iPhone-App. Am Ende prüft das Script `http://localhost:5100/api/health`
und bricht mit Fehler ab, wenn Tonregal nicht antwortet. Eigene Einstellungen und API-Keys gehören in
`/Volumes/T7/Musik/app/appsettings.Production.json`, nie ins Repo.

**Zugriff auf die T7:** launchd selbst darf keine Dateien auf externen Volumes öffnen, deshalb
liegen nur die Konsolen-Logs der Dienste (`navidrome.log`, `tonregal.stdout.log`) intern unter
`~/Library/Logs/Tonregal`. Die Programme brauchen für die T7 Festplattenvollzugriff: Startet ein
Dienst nicht, nennt das Script den Programmpfad; diesen unter Systemeinstellungen → Datenschutz &
Sicherheit → Festplattenvollzugriff hinzufügen und das Script erneut ausführen. Nach `brew upgrade navidrome`
ändert sich der Pfad, dann die Freigabe erneuern.

### Automatische Deploys

```bash
./deploy/update-tonregal.sh on      # alle 5 Minuten prüfen (on 10: alle 10)
./deploy/update-tonregal.sh status  # an/aus, Branch, ausgerollter Commit, Ende des Logs
./deploy/update-tonregal.sh off
```

Ein LaunchAgent (`de.bvelop.tonregal-update`) holt `main` und rollt neue Commits mit
`deploy-tonregal.sh` aus, sobald `GET /api/busy` meldet, dass weder Import, Inbox, Scan, Analyse
noch Backup laufen (eine Analyse in der Nacht hält ihn also bis zu ihrem Ende auf). Vorher sichert
er `tonregal.db` nach `backups/deploy/` (die letzten zehn). Der Checkout muss auf `main` stehen;
steht er auf einem anderen Branch, rollt er nichts aus und schreibt das einmal ins Log. Ein
gescheiterter Commit wird nicht wiederholt, erst der nächste oder `deploy-tonregal.sh` von Hand.
Zustand und Log liegen auf der T7 (`update/`, `logs/update.log`), nur die Konsolenausgabe des
Agents unter `~/Library/Logs/Tonregal/update.stdout.log`. Der Agent läuft als `/bin/bash` und
braucht dafür einmal Festplattenvollzugriff (Cmd+Shift+G, `/bin/bash`). `on` übernimmt den `PATH`
der aufrufenden Shell, also aus einem Terminal aufrufen, in dem `brew`, `dotnet` und `npm` gehen.

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
der Albumseite es nach. Liegen schon Tracks desselben Albums in der Library oder wurden welche aus
demselben Inbox-Ordner übernommen, bekommt jeder weitere Track deren Release, sofern er darauf steht:
Navidrome trennt Alben nach MusicBrainz-Release-ID. Tragen die Tracks eines Albums trotzdem verschiedene
Ausgaben, bringt *Ausgaben vereinheitlichen* auf der Albumseite alle auf das Release der Mehrheit.
*Verwerfen* lässt die Datei in der Inbox liegen, ohne sie erneut abzugleichen.
Dateien und Ordner lassen sich auch per Drag & Drop auf die Inbox-Seite hochladen; lose Dateien landen
gemeinsam in einem Ordner `Upload {Datum Uhrzeit}`.

**Altbestand:** MP3-Sicherungskopien liegen in `Musik/Altbestand` (anderer Ort: `Tonregal:LegacyPath` in
`appsettings.Production.json`). *Altbestand einlesen* auf der Seite *Altbestand* startet den Massenlauf:
Tonregal liest dort nur und verändert nichts. Tags mit MusicBrainz-IDs gehen vor, der Fingerprint bestätigt;
ist ein Ordner fertig abgeglichen, werden seine erkannten Dateien ohne Klick in die Library kopiert.
Byte-gleiche Dateien, die schon in der Library liegen, werden übersprungen. Konflikte landen in der Prüfliste
(neue Inbox-Dateien gehen immer vor). Dateien ohne passenden Treffer übernimmt der Ordner-Knopf in der Zeile
(oder „Ohne Treffer übernehmen“ für den ganzen Ordner) mit Titel, Interpret und Album aus den Tags; ohne
Album-Tag landen sie im Sammelalbum `{Interpret}/Unbekanntes Album`. Die
Prüflisten von Inbox und Altbestand lassen sich per Tastatur abarbeiten: `j`/`k` Zeile, `Enter` übernehmen,
`l` Kandidaten, `u` ohne Treffer, `r` neu abgleichen, `x` verwerfen.

**Dubletten:** Nach jedem Library-Scan sucht Tonregal Dateien mit gleichem Hash, gleicher Aufnahme
(MusicBrainz-Recording-ID oder AcoustID) oder gleichem Interpret, Titel und Dauer (±2 s). MP3 und FLAC
desselben Tracks gelten nicht als Dublette. Auf der Seite *Dubletten* hört man jede Datei 10 Sekunden an
und wählt, was bleibt; der Vorschlag ist verlustfrei vor verlustbehaftet, dann die höhere Bitrate. Der
Rest wandert nach `Library/.trash/{Datum}/` und wird nach 30 Tagen gelöscht. Eine Inbox-Datei, die
byte-gleich schon in der Library liegt, geht direkt in den Papierkorb; ist nur der Zielplatz belegt,
landet sie als `… (2)` daneben und erscheint als Dublette.

**Analyse:** Ein Hintergrunddienst arbeitet die Library albumweise ab: BPM und Tonart je Datei mit
librosa (`src/Tonregal.Web/analysis/analyze.py`, Python-venv unter `Musik/tools/analysis-venv`, legt das
Deploy-Script an), danach Track- und Album-Gain mit `rsgain` (Homebrew), alles direkt in die Tags.
Vorhandene BPM- und Tonart-Tags bleiben stehen. Ohne Knopf läuft die Analyse nur nachts
(`Tonregal:Analysis:NightStart`/`NightEnd`, Standard 01:00–06:00); `Tonregal:Analysis:BusyCommand`
kann einen Befehl nennen, der mit Exit-Code 0 einen laufenden KI-Job meldet, dann wartet sie. Auf der
Seite *Analyse* startet und stoppt man sie von Hand und sieht den Fortschritt.

**Backup:** Jede Nacht um 03:00 spiegelt `rclone sync` die Library nach `nextcloud:Musik/Library`
(ohne `.trash` und andere Punkt-Dateien) und legt konsistente Kopien von `tonregal.db` und
`navidrome.db` unter `nextcloud:Musik/_state/{Datum}/` ab; Stände älter als 7 Tage werden gelöscht.
Würde rclone mehr als 500 Dateien in Nextcloud löschen (`Tonregal:Backup:MaxDelete`), bricht der Lauf
ab – Schutz gegen eine leere oder falsch gemountete Library. Backup, Import und Analyse warten
aufeinander, damit nie eine halb geschriebene Datei hochgeladen wird. Die Übersicht zeigt die letzten
Läufe; zwei Fehlschläge in Folge werden rot markiert. *Jetzt sichern* oder `tonregal-backup-now` startet
sofort einen Lauf.

Einmalig einrichten (Zugang bleibt in `~/.config/rclone/rclone.conf`, nie im Repo): in Nextcloud einen
App-Nutzer mit App-Passwort anlegen, der nur den Ordner `Musik` sieht, dann auf oi-o-mat

```bash
rclone config create nextcloud webdav vendor=nextcloud \
  url=https://<nextcloud-host>/remote.php/dav/files/<nutzer>/ user=<nutzer> pass='<app-passwort>'
rclone lsd nextcloud:Musik   # Test
```

**Ingest-API:** Eigene Stücke (z. B. aus Tonwerk-AI) kommen ohne Abgleich und Review direkt in die
Library. Der Key bestimmt die Quelle. Am einfachsten erzeugt ihn die Seite **Verbindungen**: Sie zeigt den
Key für `tonwerk-ai` verdeckt an, kopiert ihn oder gleich die zwei Zeilen für `~/.config/tonwerk/tonregal.env`
auf dem Mac mit Tonwerks Server, und erzeugt auf Wunsch einen neuen (der alte gilt dann nicht mehr). Die Keys
liegen in der Datenbank. Tonregal hat keine Anmeldung, wer die Oberfläche erreicht, sieht sie also.
Alternativ geht ein fester Key in `appsettings.Production.json`; er hat für seine Quelle Vorrang und lässt
sich auf der Seite nicht ändern:

```json
{ "Tonregal": { "Ingest": { "Keys": { "tonwerk-ai": "<openssl rand -hex 32>" } } } }
```

```bash
curl -H "X-Api-Key: $KEY" https://oi-o-mat.tail6105c0.ts.net/api/ingest \
  -F file=@track.flac -F cover=@cover.jpg \
  -F 'metadata={"externalId":"song-42","title":"Nachtfahrt","artist":"Tonwerk","album":"Projekt Nacht","year":2026,"track":3,"genre":"Synthwave","bpm":128,"key":"Am"}'
```

Pflicht sind `externalId`, `title` und `artist`; derselbe `externalId` ein zweites Mal legt nichts neu an
(Antwort 200 statt 202, ist die Übernahme gescheitert, wird sie wiederholt). Die Antwort trägt die Job-ID,
den Stand liefert `GET /api/ingest/{id}` (`Imported` mit `libraryPath` = fertig); der SignalR-Hub meldet ihn
zusätzlich als `ingestStatus`. Ohne Album wird das Stück eine Single unter `{Artist}/{Titel}/`, sonst landet
es unter `{Artist}/{Jahr} - {Album}/`. Liegt dieselbe Datei byte-gleich schon in der Library, wird sie
verworfen. Fehlende BPM- oder Tonart-Werte ergänzt nachts die Analyse.

**Player:** *Abspielen* auf der Albumseite (oder ▶ an einem Track) füllt die Warteschlange; die Leiste
unten zeigt, was läuft, und lässt sich auch im Sperrbildschirm und mit Kopfhörertasten steuern. Das Audio
kommt von Navidrome, Tonregal reicht es nur durch; so zählt Navidrome die Wiedergaben (nach der Hälfte,
spätestens nach 4 Minuten) und ein dort für den Player *Tonregal* eingestelltes Transcoding greift.
Kennt Navidrome eine frisch übernommene Datei noch nicht (Scan alle 15 Minuten), spielt Tonregal sie direkt.
Die URL setzt das Deploy-Script; Benutzer und Passwort des Navidrome-Kontos gehören in
`appsettings.Production.json`:

```json
{ "Tonregal": { "Navidrome": { "User": "…", "Password": "…" } } }
```

**Songtexte:** Der Knopf mit den Zeilen in der Player-Leiste (in der iPhone-App die Sprechblase im großen Player)
zeigt den Text des laufenden Stücks; synchronisierte Texte laufen mit, ein Tipp auf eine Zeile springt dorthin.
Tonregal nimmt zuerst eine `.lrc`-Datei mit dem Namen der Audiodatei, dann den eingebetteten Text (ID3 `USLT`,
Vorbis `LYRICS`, MP4 `©lyr`, so bringt Tonwerk-AI seine Texte mit) und fragt sonst [lrclib.net](https://lrclib.net)
(frei, ohne Key). Ein synchronisierter Treffer von dort schlägt einen eingebetteten reinen Text und landet als `.lrc`
neben der Datei, übersteht also einen Neuaufbau der Datenbank und kommt mit ins Backup. Ergebnisse, auch „nichts
gefunden“, liegen in der Tabelle `Lyrics`; reine oder fehlende Texte werden nach 30 Tagen erneut gesucht, *Neu suchen*
im Songtext erzwingt es sofort (und ersetzt eine falsche `.lrc`). Eine von Hand abgelegte `.lrc` gilt immer.
`Tonregal:Lyrics:Online=false` schaltet lrclib.net ab, `WriteSidecar=false` das Schreiben der `.lrc`.

**Bewerten, Zufall, Wiederholen:** Sterne (in der Player-Leiste und in der Titelliste eines Albums; ein Klick auf
den gesetzten Stern nimmt sie zurück) gehen per Subsonic `setRating` an Navidrome und gelten für das Konto aus
`appsettings.Production.json`; gelesen werden sie aus `navidrome.db`. Tonregal speichert sie nicht selbst und schreibt
sie nicht in die Tags. Eine frisch übernommene Datei lässt sich erst bewerten, wenn Navidrome sie gescannt hat.
*Zufällig* mischt ein Album, auf einer Interpretenseite alle Stücke des Interpreten (auch Gastauftritte auf fremden
Alben) und in der Bibliothek *Alles zufällig* bis zu 2000 Stücke (`GET /api/player/shuffle`). In der Warteschlange
mischt *Rest mischen*, was nach dem laufenden Stück kommt. Der Wiederholen-Knopf schaltet aus → alle → dieses Stück.

**Smart Playlists:** Auf der Seite *Smart Playlists* legt man Regeln an (BPM-Bereich, Tonarten mit
„+ passende“ für Quinte hoch/runter und Paralleltonart, Genre, neu seit n Tagen, nie gehört, mindestens n Sterne, Sortierung,
Höchstzahl). Tonregal schreibt sie als `Library/Playlists/tonregal-{id}.nsp`; Navidrome liest sie beim
nächsten Scan ein (Besitzer ist der erste Admin) und füllt sie selbst, sie erscheinen also auch in Amperfy.
Der Dateiname hängt an der ID, ein Umbenennen ändert nur den Namen in Navidrome. Beim Löschen entfernt
Tonregal die Datei und, mit Navidrome-Zugang aus `appsettings.Production.json`, auch die Playlist in
Navidrome, die sonst stehen bliebe. „Neu (30 Tage)“ und „Nie gehört“ sind zum Start angelegt.
*Abspielen* holt die Stücke, die gerade passen, von Navidrome (`getPlaylist`, das die Liste dabei neu
auswertet) und legt sie in den Player; kennt Navidrome die Liste noch nicht, wählt Tonregal selbst aus, dann
ohne „nie gehört“ (die Sterne liest es dafür aus `navidrome.db`).

Am iPhone in Safari *Teilen → Zum Home-Bildschirm* wählen, dann startet Tonregal wie eine App.

Beide Dienste laufen nur, solange die T7 gemountet ist (`KeepAlive`/`PathState` auf
`/Volumes/T7/Musik/.tonregal-mount`), und starten nach einem Remount von selbst neu.

Shell-Funktionen für beide Macs:

```bash
source deploy/shell/tonregal.zsh   # in ~/.zshrc
tonregal-status
tonregal-log [tonregal|navidrome]
tonregal-restart [tonregal|navidrome]
```

## iPhone-App

Unter `apple/` liegt ein nativer Player (SwiftUI) für iPhone und iPad, der Hintergrund-Wiedergabe und Sperrbildschirm
zuverlässiger kann als die PWA. Einrichtung, TestFlight und Aufbau stehen in [apple/README.md](apple/README.md). Die App
holt ihre Albenliste über `GET /api/library/albums`.

## Lizenz

[GNU Affero General Public License v3.0 oder später](LICENSE). Navidrome, Chromaprint, ffmpeg,
rsgain und rclone werden nur als eigenständige Prozesse aufgerufen, nicht gelinkt.
