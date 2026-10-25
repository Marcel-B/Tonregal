// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Domain.ValueObjects;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Infrastructure.Import;

/// <summary>
/// Erste Hälfte der Import-Pipeline: Datei in der Inbox → Tags, Hash, Fingerprint → AcoustID und
/// MusicBrainz → <see cref="ImportState.Matched"/> oder <see cref="ImportState.NeedsReview"/>.
/// Die Dateien in der Inbox werden dabei nicht verändert.
/// </summary>
public sealed partial class InboxImporter(
    TonregalDbContext db,
    ITagReader tagReader,
    IFingerprintService fingerprints,
    IMetadataLookup lookup,
    InboxFileTracker tracker,
    IOptions<TonregalOptions> options,
    IOptions<MetadataOptions> metadataOptions,
    TimeProvider time,
    ILogger<InboxImporter> logger) : IInboxImporter
{
    /// <summary>Hier legt später die Ingest-API ihre Dateien ab; die haben schon einen Job.</summary>
    public const string ApiFolder = "_api";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private double AutoMatchScore => metadataOptions.Value.AutoMatchScore;

    public async Task<InboxSyncResult> SyncAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!File.Exists(o.MountMarker))
            throw new LibraryUnavailableException($"Mount-Marker {o.MountMarker} fehlt – ist die SSD gemountet?");

        var root = Path.GetFullPath(o.Inbox);
        Directory.CreateDirectory(root);
        var files = LibraryScanner.EnumerateAudioFiles(root)
            .Select(path => (Full: path, Relative: ToRelative(root, path)))
            .Where(f => !f.Relative.StartsWith(ApiFolder + "/", StringComparison.Ordinal))
            .ToList();
        var present = files.Select(f => f.Relative).ToHashSet(StringComparer.Ordinal);
        tracker.Retain(present);

        var jobs = await db.ImportJobs
            .Where(j => j.Source == FileSource.Inbox && ImportJob.ActiveStates.Contains(j.State))
            .ToDictionaryAsync(j => j.SourcePath, StringComparer.Ordinal, ct);
        // Verworfene Dateien bleiben in der Inbox liegen, bekommen aber keinen neuen Job, solange sie sich nicht ändern.
        var rejected = (await db.ImportJobs
                .Where(j => j.Source == FileSource.Inbox && j.State == ImportState.Rejected && j.Error == ImportJob.RejectedByUser)
                .Select(j => new { j.SourcePath, j.SizeBytes, j.ModifiedAt })
                .ToListAsync(ct))
            .Select(j => (j.SourcePath, j.SizeBytes, j.ModifiedAt))
            .ToHashSet();
        int created = 0, restarted = 0, removed = 0, waiting = 0;

        foreach (var (full, relative) in files)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(full);
                _ = info.Length;
            }
            catch (IOException)
            {
                continue;
            }

            var modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            jobs.TryGetValue(relative, out var job);
            if (job is not null && job.SizeBytes == info.Length && job.ModifiedAt == modifiedAt)
                continue;
            if (job is null && rejected.Contains((relative, info.Length, modifiedAt)))
                continue;
            if (!tracker.IsStable(relative, info.Length, TimeSpan.FromSeconds(o.InboxStableSeconds)))
            {
                waiting++;
                continue;
            }

            if (job is null)
            {
                job = new ImportJob { SourcePath = relative, StartedAt = time.GetUtcNow() };
                db.ImportJobs.Add(job);
                created++;
            }
            else
            {
                logger.LogInformation("Inbox: {Path} hat sich geändert, Job {Id} beginnt von vorn", relative, job.Id);
                job.Reset();
                restarted++;
            }

            job.SizeBytes = info.Length;
            job.ModifiedAt = modifiedAt;
        }

        foreach (var job in jobs.Values.Where(j => !present.Contains(j.SourcePath)))
        {
            Reject(job, ImportJob.VanishedReason);
            removed++;
        }

        await db.SaveChangesAsync(ct);
        if (created + restarted + removed > 0)
            logger.LogInformation("Inbox: {Created} neu, {Restarted} geändert, {Removed} verschwunden, {Waiting} noch im Kopieren",
                created, restarted, removed, waiting);
        return new InboxSyncResult(created, restarted, removed, waiting);
    }

    public async Task<ImportJob?> ProcessNextAsync(CancellationToken ct)
    {
        var job = await db.ImportJobs
            .Where(j => j.Source == FileSource.Inbox && (j.State == ImportState.New || j.State == ImportState.Fingerprinted))
            .OrderBy(j => j.Id)
            .FirstOrDefaultAsync(ct);
        if (job is null)
            return null;

        var path = Path.Combine(Path.GetFullPath(options.Value.Inbox), job.SourcePath);
        if (!File.Exists(path))
        {
            Reject(job, ImportJob.VanishedReason);
            await db.SaveChangesAsync(ct);
            return job;
        }

        AudioTags tags;
        try
        {
            var info = tagReader.Read(path);
            tags = info.Tags;
            if (job.State == ImportState.New)
                await FingerprintAsync(job, path, info, ct);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Inbox: {Path} nicht lesbar: {Message}", job.SourcePath, e.Message);
            job.State = ImportState.NeedsReview;
            job.ReviewReason = "Datei nicht lesbar";
            job.Error = e.Message;
            await db.SaveChangesAsync(ct);
            return job;
        }

        IReadOnlyList<RecordingCandidate> candidates;
        try
        {
            candidates = await lookup.LookupAsync(new Fingerprint(job.Fingerprint!, TimeSpan.FromMilliseconds(job.DurationMs)), ct);
        }
        catch (ExternalToolUnavailableException e)
        {
            // Bleibt Fingerprinted und wird beim nächsten Lauf erneut nachgeschlagen.
            job.Error = e.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }

        job.Error = null;
        job.Candidates = JsonSerializer.Serialize(candidates, Json);
        Decide(job, tags, candidates, AutoMatchScore);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Inbox: {Path} → {State} ({Score:P0}) {Reason}", job.SourcePath, job.State, job.MatchScore ?? 0, job.ReviewReason);

        await ApplyAlbumGroupAsync(job.Group, ct);
        return job;
    }

    private async Task FingerprintAsync(ImportJob job, string path, AudioFileInfo info, CancellationToken ct)
    {
        job.Format = info.Properties.Format;
        job.DurationMs = (int)info.Properties.Duration.TotalMilliseconds;
        job.TagTitle = info.Tags.Title;
        job.TagArtist = info.Tags.Artists.Count > 0 ? string.Join(", ", info.Tags.Artists) : info.Tags.AlbumArtist;
        job.TagAlbum = info.Tags.Album;
        job.FileHash = await HashAsync(path, ct);

        var fingerprint = await fingerprints.ComputeAsync(path, ct);
        job.Fingerprint = fingerprint.Value;
        if (fingerprint.Duration > TimeSpan.Zero)
            job.DurationMs = (int)fingerprint.Duration.TotalMilliseconds;
        job.State = ImportState.Fingerprinted;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Abweichung zwischen Datei und Aufnahme, ab der ein Kandidat nicht mehr in Frage kommt.</summary>
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Matched nur bei hohem Score, ohne Widerspruch zu den Tags und, wenn die Tags keinen Titel
    /// haben, mit genau einer Aufnahme unter den guten Treffern; sonst Review mit Begründung und Vorschlag.
    /// </summary>
    internal static void Decide(ImportJob job, AudioTags tags, IReadOnlyList<RecordingCandidate> candidates, double autoMatchScore)
    {
        job.SelectedRecordingId = null;
        job.SelectedReleaseId = null;
        job.ReviewReason = null;
        if (candidates.Count == 0)
        {
            job.MatchScore = null;
            job.State = ImportState.NeedsReview;
            job.ReviewReason = "AcoustID kennt diese Aufnahme nicht";
            return;
        }

        var bestScore = candidates.Max(c => c.Score);
        job.MatchScore = bestScore;
        var good = Good(candidates, bestScore, autoMatchScore, job.DurationMs);
        var durationMismatch = good.Count == 0;
        if (durationMismatch)
            good = candidates.Where(c => c.Score >= Math.Min(autoMatchScore, bestScore)).ToList();

        // Vorhandene MusicBrainz-IDs in den Tags haben Vorrang (Altbestand aus Picard o. Ä.), danach
        // Titel und Artist aus den Tags: AcoustID hängt an manchen Fingerprints auch fremde Songs.
        var selected = good.FirstOrDefault(c => tags.MusicBrainzReleaseId is { } r && c.ReleaseId == r
                && (tags.MusicBrainzRecordingId is not { } rec || c.RecordingId == rec))
            ?? good.FirstOrDefault(c => tags.MusicBrainzRecordingId is { } rec && c.RecordingId == rec)
            ?? good.FirstOrDefault(c => MatchesTags(c, tags))
            ?? good[0];
        job.SelectedRecordingId = selected.RecordingId;
        job.SelectedReleaseId = selected.ReleaseId;

        job.ReviewReason = durationMismatch
            ? $"Dauer {FormatDuration(job.DurationMs)} passt zu keinem Treffer"
            : ReviewReason(tags, good, selected, bestScore, autoMatchScore);
        job.State = job.ReviewReason is null ? ImportState.Matched : ImportState.NeedsReview;
    }

    private static string? ReviewReason(
        AudioTags tags, List<RecordingCandidate> good, RecordingCandidate selected, double bestScore, double autoMatchScore)
    {
        if (bestScore < autoMatchScore)
            return $"Score {bestScore:P0} unter {autoMatchScore:P0}";
        if (tags.MusicBrainzRecordingId is { } recording && good.All(c => c.RecordingId != recording))
            return "MusicBrainz-ID in den Tags passt nicht zum Fingerprint";
        if (tags.Title is { } title)
            return MatchesTags(selected, tags) ? null : $"Tags passen zu keinem Treffer: „{title}“";

        // Ohne Titel in den Tags entscheidet nur der Fingerprint; dann muss er eindeutig sein.
        return good.FirstOrDefault(c => !SameSong(c, selected)) is { } other
            ? $"Fingerprint passt auch zu „{other.Title}“ von {string.Join(", ", other.Artists)}"
            : null;
    }

    /// <summary>Kandidaten mit gutem Score, deren Länge zur Datei passt.</summary>
    internal static List<RecordingCandidate> Good(
        IReadOnlyList<RecordingCandidate> candidates, double bestScore, double autoMatchScore, int fileDurationMs) =>
        candidates
            .Where(c => c.Score >= Math.Min(autoMatchScore, bestScore))
            .Where(c => fileDurationMs <= 0 || c.DurationMs is not { } length
                || Math.Abs(length - fileDurationMs) <= DurationTolerance.TotalMilliseconds)
            .ToList();

    private static bool MatchesTags(RecordingCandidate c, AudioTags tags) =>
        tags.Title is { } title && SameTitle(title, c.Title)
        && (tags.Artists.Count == 0 || c.Artists.Count == 0 || tags.Artists.Any(a => c.Artists.Any(b => SameTitle(a, b))));

    private static string FormatDuration(int ms) => $"{ms / 60000}:{ms / 1000 % 60:00}";

    /// <summary>
    /// Dateien eines Inbox-Ordners sind ein Album-Kandidat: Steht ein Release bei der Mehrheit der
    /// Dateien unter den guten Treffern, bekommen alle Dateien, bei denen es vorkommt, dieses Release.
    /// </summary>
    private async Task ApplyAlbumGroupAsync(string group, CancellationToken ct)
    {
        if (group.Length == 0)
            return;
        var prefix = group + "/";
        var jobs = (await db.ImportJobs
                .Where(j => j.Source == FileSource.Inbox && ImportJob.ActiveStates.Contains(j.State) && j.SourcePath.StartsWith(prefix))
                .ToListAsync(ct))
            .Where(j => j.Group == group)
            .ToList();
        if (jobs.Any(j => j.State is ImportState.New or ImportState.Fingerprinted))
            return;

        var withCandidates = jobs
            .Select(j => (Job: j, Good: GoodCandidates(j)))
            .Where(x => x.Good.Count > 0)
            .ToList();
        if (withCandidates.Count < 2)
            return;

        var release = withCandidates
            .SelectMany(x => x.Good.Where(c => c.ReleaseId is not null).Select(c => c.ReleaseId!.Value).Distinct())
            .CountBy(id => id)
            .OrderByDescending(x => x.Value)
            .FirstOrDefault();
        if (release.Value * 2 <= jobs.Count)
            return;

        foreach (var (job, good) in withCandidates)
        {
            var candidate = good.FirstOrDefault(c => c.ReleaseId == release.Key && c.RecordingId == job.SelectedRecordingId)
                ?? good.FirstOrDefault(c => c.ReleaseId == release.Key);
            if (candidate is null)
                continue;
            job.SelectedRecordingId = candidate.RecordingId;
            job.SelectedReleaseId = candidate.ReleaseId;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Inbox: Ordner {Group} → Release {Release} ({Count} von {Total} Dateien)",
            group, release.Key, release.Value, jobs.Count);
    }

    private List<RecordingCandidate> GoodCandidates(ImportJob job)
    {
        if (job.Candidates is null || job.MatchScore is not { } best)
            return [];
        return Good(JsonSerializer.Deserialize<List<RecordingCandidate>>(job.Candidates, Json) ?? [], best, AutoMatchScore, job.DurationMs);
    }

    private void Reject(ImportJob job, string reason)
    {
        job.State = ImportState.Rejected;
        job.Error = reason;
        job.FinishedAt = time.GetUtcNow();
        logger.LogInformation("Inbox: {Path}: {Reason}", job.SourcePath, reason);
    }

    /// <summary>
    /// Dieselbe Aufnahme in anderer Fassung („Auberge (single edit)“) oder mit anderem Credit
    /// („Chris Rea & Band“) zählt nicht als Widerspruch.
    /// </summary>
    private static bool SameSong(RecordingCandidate a, RecordingCandidate b) =>
        SameTitle(a.Title, b.Title)
        && (a.Artists.Count == 0 || b.Artists.Count == 0 || SameTitle(a.Artists[0], b.Artists[0]));

    /// <summary>Gleich nach Normalisierung, oder einer enthält den anderen („Song (Remastered 2011)“).</summary>
    internal static bool SameTitle(string a, string b)
    {
        var x = TrackKey.Normalize(DroppedG().Replace(a, "$1ing"));
        var y = TrackKey.Normalize(DroppedG().Replace(b, "$1ing"));
        return x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal);
    }

    /// <summary>„Lookin'“ → „Looking“.</summary>
    [GeneratedRegex(@"(\w)in['’]", RegexOptions.IgnoreCase)]
    private static partial Regex DroppedG();

    private static string ToRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
