// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Domain.ValueObjects;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Infrastructure.Duplicates;

/// <summary>
/// Dubletten nach dem Plan: gleicher Datei-Hash, gleiche Aufnahme (MusicBrainz-Recording-ID oder
/// AcoustID) oder wahrscheinlich gleich (Artist + Titel + Dauer ±2 s). Dateien, die über eine dieser
/// Verbindungen zusammenhängen, bilden eine Gruppe. MP3 und FLAC am selben Track sind keine Dublette.
/// </summary>
public sealed class DuplicateService(
    TonregalDbContext db,
    IOptions<TonregalOptions> options,
    TimeProvider time,
    ILogger<DuplicateService> logger) : IDuplicateService
{
    internal sealed record FileRow(
        int Id, int TrackId, string Path, string FileHash, AudioFormat Format, int Bitrate, int DurationMs,
        string Title, IReadOnlyList<string> Artists, Guid? RecordingId, Guid? AcoustId)
    {
        public string? Recording => RecordingId?.ToString() ?? AcoustId?.ToString();
    }

    internal sealed record Candidate(DuplicateKind Kind, IReadOnlyList<FileRow> Files);

    public async Task<DuplicateScanResult> DetectAsync(CancellationToken ct)
    {
        var files = (await db.AudioFiles
                .AsNoTracking()
                .Select(f => new
                {
                    f.Id, f.TrackId, f.Path, f.FileHash, f.Format, f.Bitrate, f.Track.DurationMs, f.Track.Title,
                    Artists = f.Track.Artists.Select(a => a.Name).ToList(),
                    AlbumArtist = f.Track.Album.AlbumArtist.Name,
                    f.Track.MusicBrainzRecordingId, f.Track.AcoustId,
                })
                .ToListAsync(ct))
            .Select(f => new FileRow(
                f.Id, f.TrackId, f.Path, f.FileHash, f.Format, f.Bitrate, f.DurationMs, f.Title,
                f.Artists.Count > 0 ? f.Artists : [f.AlbumArtist], f.MusicBrainzRecordingId, f.AcoustId))
            .ToList();
        var candidates = Find(files);

        var groups = await db.DuplicateGroups.Include(g => g.Files).ToListAsync(ct);
        var open = groups.Where(g => g.Decision == DuplicateDecision.Open).ToList();
        var keptTogether = groups
            .Where(g => g.Decision == DuplicateDecision.Resolved)
            .Select(g => g.Files.Select(f => f.Id).ToHashSet())
            .ToList();
        var current = new HashSet<DuplicateGroup>();
        var created = 0;

        foreach (var candidate in candidates)
        {
            var ids = candidate.Files.Select(f => f.Id).ToHashSet();
            if (keptTogether.Any(ids.IsSubsetOf))
                continue;

            var existing = open.FirstOrDefault(g => !current.Contains(g) && g.Files.Select(f => f.Id).ToHashSet().SetEquals(ids));
            if (existing is not null)
            {
                existing.Kind = candidate.Kind;
                current.Add(existing);
                continue;
            }

            var group = new DuplicateGroup
            {
                Kind = candidate.Kind,
                CreatedAt = time.GetUtcNow(),
                KeeperFileId = Suggest(candidate.Files).Id,
                Files = await db.AudioFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct),
            };
            db.DuplicateGroups.Add(group);
            current.Add(group);
            created++;
        }

        var stale = open.Where(g => !current.Contains(g)).ToList();
        db.DuplicateGroups.RemoveRange(stale);
        await db.SaveChangesAsync(ct);

        var result = new DuplicateScanResult(current.Count, created, stale.Count);
        if (created + stale.Count > 0)
            logger.LogInformation("Dubletten: {Open} offen, {Created} neu, {Removed} erledigt", result.Open, created, stale.Count);
        return result;
    }

    /// <summary>Verbindet Dateien über Hash, Aufnahme und Track-Key und bildet daraus Gruppen.</summary>
    internal static List<Candidate> Find(IReadOnlyList<FileRow> files)
    {
        var parent = Enumerable.Range(0, files.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        void Link(IEnumerable<int> members)
        {
            int? first = null;
            foreach (var i in members)
            {
                if (first is null)
                    first = Root(i);
                else
                    parent[Root(i)] = first.Value;
            }
        }

        var indexed = files.Select((f, i) => (File: f, Index: i)).ToList();
        foreach (var same in indexed.GroupBy(x => x.File.FileHash))
            Link(same.Select(x => x.Index));
        foreach (var same in indexed.Where(x => x.File.Recording is not null).GroupBy(x => x.File.Recording))
            Link(same.Select(x => x.Index));

        // Track-Key je Artist, damit „A feat. B“ und „A“ zusammenfinden; ohne Artist nichts raten.
        var keyed = indexed
            .SelectMany(x => x.File.Artists
                .Where(a => a != LibraryScanner.UnknownArtist)
                .Select(a => (Key: TrackKey.Create(a, x.File.Title, TimeSpan.FromMilliseconds(x.File.DurationMs)), x.Index)))
            .Where(x => x.Key.Artist.Length > 0 && x.Key.Title.Length > 0);
        foreach (var same in keyed.GroupBy(x => x.Key.TextKey))
        {
            var byDuration = same.OrderBy(x => x.Key.DurationSeconds).ToList();
            for (var i = 1; i < byDuration.Count; i++)
                if (byDuration[i].Key.Matches(byDuration[i - 1].Key) && byDuration[i].Index != byDuration[i - 1].Index)
                    Link([byDuration[i - 1].Index, byDuration[i].Index]);
        }

        return indexed
            .GroupBy(x => Root(x.Index))
            .Select(g => g.Select(x => x.File).OrderBy(f => f.Path, StringComparer.Ordinal).ToList())
            .Where(g => g.Count > 1 && !IsFormatUpgrade(g))
            .Select(g => new Candidate(KindOf(g), g))
            .ToList();
    }

    private static bool IsFormatUpgrade(IReadOnlyList<FileRow> files) =>
        files.All(f => f.TrackId == files[0].TrackId) && files.Select(f => f.Format).Distinct().Count() == files.Count;

    private static DuplicateKind KindOf(IReadOnlyList<FileRow> files) =>
        files.All(f => f.FileHash == files[0].FileHash) ? DuplicateKind.ExactHash
        : files[0].Recording is { } recording && files.All(f => f.Recording == recording) ? DuplicateKind.Fingerprint
        : DuplicateKind.Metadata;

    /// <summary>Vorschlag: verlustfrei vor verlustbehaftet, dann höhere Bitrate, längere Fassung, sonst die ältere Datei.</summary>
    internal static FileRow Suggest(IEnumerable<FileRow> files) =>
        files
            .OrderByDescending(f => IsLossless(f.Format))
            .ThenByDescending(f => f.Bitrate)
            .ThenByDescending(f => f.DurationMs)
            .ThenByDescending(f => f.RecordingId is not null)
            .ThenBy(f => f.Id)
            .First();

    public static bool IsLossless(AudioFormat format) => format is AudioFormat.Flac or AudioFormat.Alac or AudioFormat.Wav;

    public async Task ResolveAsync(int groupId, IReadOnlyCollection<int> keepFileIds, CancellationToken ct)
    {
        var group = await db.DuplicateGroups.Include(g => g.Files).SingleOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new KeyNotFoundException($"Dublettengruppe {groupId} gibt es nicht");
        if (group.Decision != DuplicateDecision.Open)
            throw new InvalidOperationException("Diese Gruppe ist schon entschieden");
        if (keepFileIds.Count == 0 || keepFileIds.Any(id => group.Files.All(f => f.Id != id)))
            throw new InvalidOperationException("Mindestens eine Datei der Gruppe muss bleiben");

        var o = options.Value;
        if (!File.Exists(o.MountMarker))
            throw new LibraryUnavailableException($"Mount-Marker {o.MountMarker} fehlt – ist die SSD gemountet?");
        var root = Path.GetFullPath(o.Library);
        var now = time.GetUtcNow();

        foreach (var file in group.Files.Where(f => !keepFileIds.Contains(f.Id)).ToList())
        {
            var full = Path.Combine(root, file.Path);
            if (File.Exists(full))
            {
                var trashed = LibraryTrash.Move(o.Trash, full, file.Path, now);
                logger.LogInformation("Dublette {Path} → {Trash}", file.Path, trashed);
                RemoveEmptyAlbumFolder(Path.GetDirectoryName(full)!, root, o.Trash, now);
            }

            group.Files.Remove(file);
            db.AudioFiles.Remove(file);
        }

        group.Decision = DuplicateDecision.Resolved;
        if (group.KeeperFileId is not { } keeper || !keepFileIds.Contains(keeper))
            group.KeeperFileId = keepFileIds.First();
        group.DecidedAt = now;
        await db.SaveChangesAsync(ct);

        await LibraryScanner.RemoveOrphansAsync(db, ct);
        // Andere offene Gruppen können durch das Verschieben auf eine Datei geschrumpft sein.
        await db.DuplicateGroups
            .Where(g => g.Decision == DuplicateDecision.Open && g.Files.Count < 2)
            .ExecuteDeleteAsync(ct);
    }

    public int PurgeTrash()
    {
        var removed = LibraryTrash.Purge(options.Value.Trash, time.GetUtcNow());
        if (removed > 0)
            logger.LogInformation("Papierkorb: {Count} Tagesordner älter als {Days} Tage gelöscht", removed, LibraryTrash.Retention.TotalDays);
        return removed;
    }

    /// <summary>
    /// Ist im Albumordner keine Audiodatei mehr, wandert der Rest (cover.jpg) mit in den Papierkorb
    /// und der Ordner verschwindet, ebenso ein danach leerer Artist-Ordner.
    /// </summary>
    private static void RemoveEmptyAlbumFolder(string folder, string root, string trash, DateTimeOffset now)
    {
        while (folder.Length > root.Length && folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            if (Directory.EnumerateDirectories(folder).Any())
                return;
            var entries = Directory.EnumerateFiles(folder).ToList();
            if (entries.Any(e => TagLibTagReader.SupportedExtensions.Contains(Path.GetExtension(e)) && !Path.GetFileName(e).StartsWith('.')))
                return;

            foreach (var entry in entries)
            {
                if (Path.GetFileName(entry).StartsWith('.'))
                    File.Delete(entry);
                else
                    LibraryTrash.Move(trash, entry, Path.GetRelativePath(root, entry), now);
            }

            Directory.Delete(folder);
            folder = Path.GetDirectoryName(folder)!;
        }
    }
}
