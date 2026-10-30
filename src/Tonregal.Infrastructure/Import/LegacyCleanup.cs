// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Infrastructure.Import;

/// <summary>
/// Räumt den Altbestand auf, ohne etwas zu riskieren: Eine Datei geht nur, wenn ihr jüngster Job sie
/// als übernommen (oder byte-gleich schon vorhanden) führt, sie seitdem unverändert ist und die Datei,
/// die das belegt, jetzt in der Library liegt. Sie wandert in den Papierkorb, nicht ins Nichts.
/// </summary>
public sealed class LegacyCleanup(
    TonregalDbContext db,
    LibraryWriteLock writeLock,
    IOptions<TonregalOptions> options,
    TimeProvider time,
    ILogger<LegacyCleanup> logger) : ILegacyCleanup
{
    /// <summary>Unterordner im Tagesordner des Papierkorbs; darunter bleibt der Pfad aus dem Altbestand erhalten.</summary>
    public const string TrashSubfolder = "Altbestand";

    internal const string Changed = "Datei hat sich seit dem Abgleich geändert";
    internal const string LibraryCopyMissing = "Keine passende Datei in der Library gefunden";

    private sealed record LibraryFile(int Id, string Path, string FileHash, Guid? RecordingId);

    private sealed record Check(ImportJob Job, string FullPath, long SizeBytes, LegacyCleanupFile? File, string? Reason);

    public async Task<LegacyCleanupPreview> PreviewAsync(CancellationToken ct)
    {
        var checks = await CheckAsync(null, ct);
        return new LegacyCleanupPreview(
            TrashFolder(),
            checks.Select(c => c.File).OfType<LegacyCleanupFile>().ToList(),
            Skipped(checks));
    }

    public async Task<LegacyCleanupResult> CleanAsync(IReadOnlyCollection<int> jobIds, CancellationToken ct)
    {
        using var _ = await writeLock.AcquireAsync("Aufräumen", ct);
        var checks = await CheckAsync(jobIds.ToHashSet(), ct);
        var o = options.Value;
        var legacyRoot = Path.GetFullPath(o.Legacy);
        var now = time.GetUtcNow();
        int moved = 0;
        long size = 0;
        var skipped = Skipped(checks).ToList();

        foreach (var check in checks.Where(c => c.File is not null))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var trashed = LibraryTrash.Move(o.Trash, check.FullPath, Path.Combine(TrashSubfolder, check.Job.SourcePath), now);
                LibraryImporter.RemoveEmptyFolders(Path.GetDirectoryName(check.FullPath)!, legacyRoot);
                logger.LogInformation("Altbestand aufgeräumt: {Path} → {Trash} (liegt als {Library} in der Library)",
                    check.Job.SourcePath, trashed, check.File!.LibraryPath);
                moved++;
                size += check.SizeBytes;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Altbestand: {Path} ließ sich nicht verschieben: {Message}", check.Job.SourcePath, e.Message);
                skipped.Add(new LegacyCleanupSkipped(check.Job.Id, check.Job.SourcePath, e.Message));
            }
        }

        logger.LogInformation("Altbestand aufgeräumt: {Moved} Dateien in den Papierkorb, {Skipped} geblieben", moved, skipped.Count);
        return new LegacyCleanupResult(moved, size, TrashFolder(), skipped);
    }

    private string TrashFolder() =>
        string.Join('/', Path.GetFileName(options.Value.Trash), LibraryTrash.Day(time.GetUtcNow()), TrashSubfolder);

    private static IReadOnlyList<LegacyCleanupSkipped> Skipped(IEnumerable<Check> checks) =>
        checks.Where(c => c.Reason is not null).Select(c => new LegacyCleanupSkipped(c.Job.Id, c.Job.SourcePath, c.Reason!)).ToList();

    /// <summary>Prüft alle erledigten Altbestand-Jobs, bzw. nur die genannten.</summary>
    private async Task<List<Check>> CheckAsync(HashSet<int>? only, CancellationToken ct)
    {
        var o = options.Value;
        if (!File.Exists(o.MountMarker))
            throw new LibraryUnavailableException($"Mount-Marker {o.MountMarker} fehlt – ist die SSD gemountet?");
        var legacyRoot = Path.GetFullPath(o.Legacy);
        var libraryRoot = Path.GetFullPath(o.Library);
        if (!Directory.Exists(legacyRoot))
            return [];

        // Je Pfad zählt der jüngste Job, wie beim Stand des Massenlaufs.
        var jobs = (await db.ImportJobs.AsNoTracking().Where(j => j.Source == FileSource.Legacy).ToListAsync(ct))
            .GroupBy(j => j.SourcePath, StringComparer.Ordinal)
            .Select(g => g.MaxBy(j => j.Id)!)
            .Where(j => j.State == ImportState.Imported || IsAlreadyInLibrary(j))
            .Where(j => only is null || only.Contains(j.Id))
            .OrderBy(j => j.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (jobs.Count == 0)
            return [];

        var files = await db.AudioFiles.AsNoTracking()
            .Select(f => new LibraryFile(f.Id, f.Path, f.FileHash, f.Track.MusicBrainzRecordingId))
            .ToListAsync(ct);
        var byId = files.ToDictionary(f => f.Id);
        var byHash = files.ToLookup(f => f.FileHash, StringComparer.OrdinalIgnoreCase);
        var byRecording = files.Where(f => f.RecordingId is not null).ToLookup(f => f.RecordingId!.Value);

        string? Present(string? relative) =>
            relative is not null && Inside(libraryRoot, relative) is { } full && File.Exists(full) ? relative : null;

        var checks = new List<Check>();
        foreach (var job in jobs)
        {
            // Schon aufgeräumt oder von Hand entfernt: nichts zu tun, auch kein Hinweis.
            if (Inside(legacyRoot, job.SourcePath) is not { } full || !File.Exists(full))
                continue;

            var info = new FileInfo(full);
            if (info.Length != job.SizeBytes || new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) != job.ModifiedAt)
            {
                checks.Add(new Check(job, full, info.Length, null, Changed));
                continue;
            }

            // Belege, stärkster zuerst: die beim Import angelegte Datei, ihr Pfad, sonst dieselbe Aufnahme
            // (nach „Ausgaben vereinheitlichen“ oder einer Dubletten-Entscheidung liegt sie woanders).
            // Bei „schon da“ der Pfad aus der Meldung oder eine Datei mit gleichem Hash.
            var alreadyThere = IsAlreadyInLibrary(job);
            var evidence = alreadyThere
                ? Present(job.Error![ImportJob.AlreadyInLibrary.Length..])
                    ?? (job.FileHash is null ? null : byHash[job.FileHash].Select(f => Present(f.Path)).FirstOrDefault(p => p is not null))
                : Present(job.AudioFileId is { } id && byId.TryGetValue(id, out var linked) ? linked.Path : null)
                    ?? Present(job.LibraryPath)
                    ?? (job.SelectedRecordingId is { } recording
                        ? byRecording[recording].Select(f => Present(f.Path)).FirstOrDefault(p => p is not null)
                        : null);

            checks.Add(evidence is null
                ? new Check(job, full, info.Length, null, LibraryCopyMissing)
                : new Check(job, full, info.Length, new LegacyCleanupFile(job.Id, job.SourcePath, info.Length, alreadyThere, evidence), null));
        }

        return checks;
    }

    private static bool IsAlreadyInLibrary(ImportJob job) =>
        job.State == ImportState.Rejected && job.Error is { } error && error.StartsWith(ImportJob.AlreadyInLibrary, StringComparison.Ordinal);

    /// <summary>Voller Pfad unter <paramref name="root"/>; <c>null</c> für Pfade, die hinaus- oder in Punkt-Ordner wie <c>.trash</c> führen.</summary>
    internal static string? Inside(string root, string relative)
    {
        if (relative.Length == 0 || relative.Split('/').Any(part => part.Length == 0 || part.StartsWith('.')))
            return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }
}
