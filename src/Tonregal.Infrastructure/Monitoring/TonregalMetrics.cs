// SPDX-License-Identifier: AGPL-3.0-or-later
using Prometheus;

namespace Tonregal.Infrastructure.Monitoring;

/// <summary>
/// Messwerte für Prometheus (<c>GET /metrics</c>), alle Namen an einer Stelle. Labels haben nur wenige feste
/// Werte; <c>/metrics</c> hat keine Anmeldung, also stehen hier keine Titel, Pfade oder Keys. Zähler laufen
/// immer mit, die Ausgabe schaltet <c>Monitoring:Metrics</c> ein.
/// </summary>
public static class TonregalMetrics
{
    public static readonly Counter InboxFiles = Metrics.CreateCounter(
        "tonregal_inbox_files_total",
        "Dateien der Inbox nach Ergebnis (matched, unmatched, duplicate, imported, failed).", "result");

    public static readonly Gauge InboxWaiting = Metrics.CreateGauge(
        "tonregal_inbox_waiting", "Jobs der Inbox, die noch nicht übernommen oder verworfen sind, nach Stand (new, matched, review).", "state");

    public static readonly Counter Ingest = Metrics.CreateCounter(
        "tonregal_ingest_total", "Übergaben über die Ingest-API (Tonwerk-AI) nach Ergebnis (accepted, known, refused).", "result");

    public static readonly Counter MusicBrainzRequests = Metrics.CreateCounter(
        "tonregal_musicbrainz_requests_total",
        "Anfragen an MusicBrainz, ohne Treffer aus dem Cache, nach Ergebnis (match, none, error, rate_limited).", "result");

    public static readonly Counter ScanRuns = Metrics.CreateCounter(
        "tonregal_scan_runs_total", "Library-Scans nach Ergebnis (success, skipped, failure).", "result");

    public static readonly Histogram ScanDuration = Metrics.CreateHistogram(
        "tonregal_scan_duration_seconds", "Dauer eines erfolgreichen Library-Scans samt Dublettensuche.",
        new HistogramConfiguration { Buckets = [1, 5, 15, 30, 60, 120, 300, 600, 1200, 3600] });

    public static readonly Counter Analysis = Metrics.CreateCounter(
        "tonregal_analysis_total", "Analysierte Dateien (BPM, Tonart, ReplayGain) nach Ergebnis (done, failed).", "result");

    public static readonly Gauge AnalysisWaiting = Metrics.CreateGauge(
        "tonregal_analysis_waiting", "Dateien, die noch auf die Analyse warten.",
        new GaugeConfiguration { SuppressInitialValue = true });

    public static readonly Gauge Tracks = Metrics.CreateGauge("tonregal_tracks", "Titel im Index.");

    public static readonly Gauge Duplicates = Metrics.CreateGauge("tonregal_duplicates", "Erkannte Dublettengruppen.");

    // Fehlt, bis die Library einmal gezählt ist: 0 hieße sonst „leer“.
    public static readonly Gauge LibraryBytes = Metrics.CreateGauge(
        "tonregal_library_bytes", "Größe der Library in Bytes, höchstens stündlich gezählt.",
        new GaugeConfiguration { SuppressInitialValue = true });

    public static readonly Counter BackupRuns = Metrics.CreateCounter(
        "tonregal_backup_runs_total", "Backup-Läufe nach Ergebnis (succeeded, failed).", "result");

    // Fehlt bis zum ersten erfolgreichen Lauf, damit der Alarm nicht „seit 1970“ meldet.
    public static readonly Gauge BackupLastSuccess = Metrics.CreateGauge(
        "tonregal_backup_last_success_timestamp_seconds", "Unix-Zeit des letzten erfolgreichen Backups.",
        new GaugeConfiguration { SuppressInitialValue = true });

    public static readonly Gauge MusicRootMounted = Metrics.CreateGauge(
        "tonregal_music_root_mounted", "1, wenn der Mount-Marker der SSD da ist.");

    public static readonly string[] InboxResults = ["matched", "unmatched", "duplicate", "imported", "failed"];
    public static readonly string[] InboxStates = ["new", "matched", "review"];

    /// <summary>Legt die Reihen mit festen Labels an, damit <c>increase()</c> schon den ersten Zähler sieht.</summary>
    public static void Register()
    {
        foreach (var result in InboxResults)
            InboxFiles.WithLabels(result);
        foreach (var state in InboxStates)
            InboxWaiting.WithLabels(state);
        foreach (var result in new[] { "accepted", "known", "refused" })
            Ingest.WithLabels(result);
        foreach (var result in new[] { "match", "none", "error", "rate_limited" })
            MusicBrainzRequests.WithLabels(result);
        foreach (var result in new[] { "success", "skipped", "failure" })
            ScanRuns.WithLabels(result);
        foreach (var result in new[] { "done", "failed" })
            Analysis.WithLabels(result);
        foreach (var result in new[] { "succeeded", "failed" })
            BackupRuns.WithLabels(result);
    }
}
