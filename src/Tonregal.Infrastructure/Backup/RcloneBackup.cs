// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Analysis;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Infrastructure.Backup;

/// <summary>
/// Backup per rclone (WebDAV zu Nextcloud): <c>rclone sync Library {Remote}/Library</c>, danach
/// konsistente Kopien von tonregal.db und navidrome.db (<c>VACUUM INTO</c>) nach
/// <c>{Remote}/_state/{Datum}/</c>; Stände älter als <see cref="BackupOptions.KeepStateDays"/> fliegen raus.
/// Hält dabei das <see cref="LibraryWriteLock"/>, damit kein Import oder Analyse-Schreibvorgang dazwischenkommt.
/// </summary>
public sealed class RcloneBackup(
    TonregalDbContext db,
    LibraryWriteLock writeLock,
    IOptions<BackupOptions> backup,
    IOptions<TonregalOptions> tonregal,
    TimeProvider time,
    ILogger<RcloneBackup> logger) : IBackupRunner
{
    private const string InstallHint = "brew install rclone";
    private const int MaxLogLength = 8000;

    public async Task<BackupRun> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var run = new BackupRun { StartedAt = time.GetUtcNow() };
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var log = new StringBuilder();
        try
        {
            if (!File.Exists(tonregal.Value.MountMarker))
                throw new LibraryUnavailableException($"Mount-Marker {tonregal.Value.MountMarker} fehlt – ist die SSD gemountet?");
            progress?.Report("Wartet auf laufende Importe");
            using (await writeLock.AcquireAsync("Backup", ct))
                await SyncAsync(run, log, progress, ct);
            run.Status = BackupStatus.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Backup abgebrochen");
            log.AppendLine("Abgebrochen");
            run.Status = BackupStatus.Failed;
        }
        catch (Exception e)
        {
            logger.LogWarning("Backup fehlgeschlagen: {Message}", e.Message);
            log.AppendLine(e.Message);
            run.Status = BackupStatus.Failed;
        }

        run.FinishedAt = time.GetUtcNow();
        run.Log = Truncate(log.ToString().Trim());
        await db.SaveChangesAsync(CancellationToken.None);
        return run;
    }

    private async Task SyncAsync(BackupRun run, StringBuilder log, IProgress<string>? progress, CancellationToken ct)
    {
        var o = backup.Value;
        progress?.Report("Prüft den rclone-Remote");
        var remotes = await RcloneAsync(["listremotes"], ct);
        if (!remotes.Stdout.Split('\n', StringSplitOptions.TrimEntries).Contains(o.RemoteName + ":"))
            throw new InvalidOperationException($"rclone-Remote „{o.RemoteName}“ fehlt – einmal „rclone config“ ausführen (WebDAV, Nextcloud)");

        progress?.Report("Spiegelt die Library");
        List<string> sync =
        [
            "sync", Path.GetFullPath(tonregal.Value.Library), $"{o.Remote}/Library",
            // Papierkorb, macOS-Ressourcendateien und halb geschriebene .tonregal-*-Kopien bleiben lokal.
            "--exclude", ".*", "--exclude", ".*/**",
            "--max-delete", o.MaxDelete.ToString(CultureInfo.InvariantCulture),
            "--use-json-log", "--log-level", "INFO", "--stats", "30s", "--stats-log-level", "NOTICE",
        ];
        if (!string.IsNullOrWhiteSpace(o.BandwidthLimit))
            sync.AddRange(["--bwlimit", o.BandwidthLimit]);
        var result = await RcloneAsync(sync, ct, line =>
        {
            if (progress is not null && ProgressText(line) is { } text)
                progress.Report(text);
        });
        var summary = ParseLog(result.Stderr);
        (run.FilesSynced, run.BytesSynced) = (summary.Transfers, summary.Bytes);
        log.AppendLine($"Library: {summary.Transfers} Datei(en) übertragen, {summary.Deletes} gelöscht, {summary.Bytes} Bytes");
        foreach (var error in summary.Errors)
            log.AppendLine(error);
        if (result.ExitCode != 0)
            throw new InvalidDataException($"rclone sync fehlgeschlagen (Exit {result.ExitCode}){(summary.Errors.Count > 0 ? ": " + summary.Errors[^1] : "")}");

        progress?.Report("Sichert die Datenbanken");
        var date = time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var staging = Path.Combine(tonregal.Value.MusicRoot, ".backup-state");
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            await SnapshotAsync(db.Database.GetDbConnection().DataSource, Path.Combine(staging, "tonregal.db"), ct);
            var navidrome = string.IsNullOrWhiteSpace(o.NavidromeDatabase)
                ? Path.Combine(tonregal.Value.MusicRoot, "navidrome", "navidrome.db")
                : o.NavidromeDatabase;
            if (File.Exists(navidrome))
                await SnapshotAsync(navidrome, Path.Combine(staging, "navidrome.db"), ct);
            else
                log.AppendLine($"navidrome.db nicht gefunden ({navidrome}), nur tonregal.db gesichert");

            var copy = await RcloneAsync(["copy", staging, $"{o.Remote}/_state/{date}"], ct);
            if (copy.ExitCode != 0)
                throw new InvalidDataException($"Datenbank-Stand ließ sich nicht hochladen: {LastLine(copy.Stderr)}");
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }

        progress?.Report("Räumt alte Stände auf");
        await PruneStatesAsync(log, ct);
    }

    /// <summary>Löscht <c>_state/{Datum}</c>-Ordner, die älter als die Aufbewahrungsfrist sind.</summary>
    private async Task PruneStatesAsync(StringBuilder log, CancellationToken ct)
    {
        var o = backup.Value;
        var listing = await RcloneAsync(["lsf", "--dirs-only", $"{o.Remote}/_state"], ct);
        if (listing.ExitCode != 0)
            return;
        var oldest = DateOnly.FromDateTime(time.GetLocalNow().DateTime).AddDays(-(o.KeepStateDays - 1));
        foreach (var name in listing.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = name.TrimEnd('/');
            if (!DateOnly.TryParseExact(folder, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day >= oldest)
                continue;
            var purge = await RcloneAsync(["purge", $"{o.Remote}/_state/{folder}"], ct);
            log.AppendLine(purge.ExitCode == 0 ? $"Stand {folder} entfernt" : $"Stand {folder} ließ sich nicht entfernen: {LastLine(purge.Stderr)}");
        }
    }

    /// <summary>Konsistente Kopie einer SQLite-Datenbank, auch während ein anderer Prozess sie benutzt.</summary>
    private static async Task SnapshotAsync(string source, string target, CancellationToken ct)
    {
        if (File.Exists(target))
            File.Delete(target);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source,
            // Nicht ReadOnly: bei WAL-Datenbanken (navidrome.db) muss SQLite die -shm-Datei anfassen dürfen.
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target";
        command.Parameters.AddWithValue("$target", target);
        await command.ExecuteNonQueryAsync(ct);
    }

    private Task<ProcessResult> RcloneAsync(IEnumerable<string> arguments, CancellationToken ct, Action<string>? onLogLine = null) =>
        ExternalProcess.RunAsync(backup.Value.RclonePath, arguments, TimeSpan.FromHours(backup.Value.TimeoutHours), InstallHint, ct, onLogLine);

    /// <summary>Aus einer Statistik-Zeile von rclone ein Fortschrittstext, z. B. „Lädt hoch: 120 von 4.800 Dateien, 1,2 von 38,5 GB“.</summary>
    internal static string? ProgressText(string jsonLine)
    {
        try
        {
            var entry = JsonDocument.Parse(jsonLine).RootElement;
            if (!entry.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object)
                return null;
            var de = CultureInfo.GetCultureInfo("de-DE");
            var transfers = Int(stats, "transfers");
            var totalTransfers = Int(stats, "totalTransfers");
            var bytes = Long(stats, "bytes");
            var totalBytes = Long(stats, "totalBytes");
            var files = totalTransfers > transfers
                ? $"{transfers.ToString("N0", de)} von {totalTransfers.ToString("N0", de)} Dateien"
                : $"{transfers.ToString("N0", de)} Dateien";
            var size = totalBytes > bytes ? $"{Gigabytes(bytes, de)} von {Gigabytes(totalBytes, de)} GB" : $"{Gigabytes(bytes, de)} GB";
            return $"Lädt hoch: {files}, {size}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Gigabytes(long bytes, CultureInfo culture) => (bytes / 1e9).ToString("0.0", culture);

    private static long Long(JsonElement stats, string name) =>
        stats.TryGetProperty(name, out var value) && value.TryGetInt64(out var l) ? l : 0;

    internal sealed record LogSummary(int Transfers, int Deletes, long Bytes, List<string> Errors);

    /// <summary>Wertet das JSON-Log von rclone aus: die letzte Statistik und alle Fehlermeldungen.</summary>
    internal static LogSummary ParseLog(string jsonLines)
    {
        var summary = new LogSummary(0, 0, 0, []);
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonElement entry;
            try
            {
                entry = JsonDocument.Parse(line).RootElement;
            }
            catch (JsonException)
            {
                summary.Errors.Add(line);
                continue;
            }

            if (entry.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            {
                summary = summary with
                {
                    Transfers = Int(stats, "transfers"),
                    Deletes = Int(stats, "deletes"),
                    Bytes = stats.TryGetProperty("bytes", out var bytes) && bytes.TryGetInt64(out var b) ? b : 0,
                };
            }
            else if (entry.TryGetProperty("level", out var level) && level.GetString() is "error" or "critical"
                && entry.TryGetProperty("msg", out var msg))
            {
                var obj = entry.TryGetProperty("object", out var o) ? o.GetString() + ": " : "";
                summary.Errors.Add(obj + msg.GetString());
            }
        }

        return summary;
    }

    private static int Int(JsonElement stats, string name) =>
        stats.TryGetProperty(name, out var value) && value.TryGetInt32(out var i) ? i : 0;

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private static string Truncate(string text) => text.Length <= MaxLogLength ? text : text[..MaxLogLength] + " …";
}
