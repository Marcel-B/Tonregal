# CLAUDE.md

Hinweise für Claude Code in diesem Repository. Aufbau, Entwicklung und Betrieb stehen im [README](README.md), die
iPhone-App in [apple/README.md](apple/README.md).

## Dokumentation auf marcelbenders.com

Tonregal wird zusätzlich öffentlich im Repository [Marcel-B/com](https://github.com/Marcel-B/com) dokumentiert, nach
dessen `CONTRIBUTING.md` (Projektseite `content/projekte/tonregal/projekt.md`, Wasserstandsmeldungen unter
`content/projekte/tonregal/stand/`, Anleitungen unter `content/projekte/tonregal/anleitungen/`).

- Jedes neue Feature und jede nennenswerte Änderung muss dort auftauchen und für Besucher erklärt werden: die
  Projektseite ergänzen und, wenn sich der Stand spürbar ändert, eine neue Wasserstandsmeldung schreiben.
- Das geschieht als eigener Pull Request in `Marcel-B/com`, zusammen mit dem Feature-PR hier.
- Dort keine internen Hostnamen, Tailnet-Adressen, IPs oder Schlüssel; was nur in einer Testumgebung lief, steht unter
  „in Arbeit“, nicht unter „fertig“.

## Konventionen

- Commit-Nachrichten und Branch-Namen sind deutsch.
- Messwerte stehen in `src/Tonregal.Infrastructure/Monitoring/TonregalMetrics.cs` (Präfix `tonregal_`, Labels nur mit
  wenigen festen Werten, nie Titel, Pfade oder Keys; Tabelle im README-Abschnitt „Überwachung“). Das Dashboard liegt in
  Tonwerk-AI unter `deploy/monitoring/` (`tonregal.json`); keinen Namen umbenennen, ohne es mitzuziehen.
