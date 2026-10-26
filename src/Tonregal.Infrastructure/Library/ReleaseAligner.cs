// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Duplicates;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;
using LibraryPathRule = Tonregal.Domain.ValueObjects.LibraryPath;

namespace Tonregal.Infrastructure.Library;

public sealed class ReleaseAligner(
    TonregalDbContext db,
    ITagReader tagReader,
    ITagWriter tagWriter,
    IMetadataLookup lookup,
    IOptions<TonregalOptions> options,
    TimeProvider time,
    ILogger<ReleaseAligner> logger) : IReleaseAligner
{
    private const string TempPrefix = ".tonregal-";

    public async Task<int> CountReleasesAsync(int albumId, CancellationToken ct)
    {
        var album = await LoadAsync(albumId, ct);
        if (!File.Exists(options.Value.MountMarker))
            return 0;
        return Read(album).Select(r => r.Tags.MusicBrainzReleaseId).OfType<Guid>().Distinct().Count();
    }

    public async Task<ReleaseAlignResult> AlignAsync(int albumId, CancellationToken ct)
    {
        var album = await LoadAsync(albumId, ct);
        var o = options.Value;
        if (!File.Exists(o.MountMarker))
            throw new LibraryUnavailableException($"Mount-Marker {o.MountMarker} fehlt – ist die SSD gemountet?");

        var rows = Read(album);
        // Mehrheit gewinnt, bei Gleichstand das Release, das das Album schon trägt.
        var target = rows
            .Select(r => r.Tags.MusicBrainzReleaseId).OfType<Guid>()
            .GroupBy(id => id)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key == album.MusicBrainzReleaseId)
            .Select(g => (Guid?)g.Key)
            .FirstOrDefault();
        if (target is not { } releaseId)
            return new ReleaseAlignResult(null, 0, 0);

        var root = Path.GetFullPath(o.Library);
        var now = time.GetUtcNow();
        int changed = 0, skipped = 0;
        foreach (var (file, path, tags) in rows.Where(r => r.Tags.MusicBrainzReleaseId != releaseId))
        {
            var track = tags.MusicBrainzRecordingId is { } recordingId
                ? await lookup.GetReleaseTrackAsync(releaseId, recordingId, ct)
                : null;
            if (track is null)
            {
                skipped++;
                continue;
            }

            var moved = Retag(path, root, track with { AcoustId = tags.AcoustId });
            file.Path = Path.GetRelativePath(root, moved).Replace(Path.DirectorySeparatorChar, '/');
            if (moved != path)
                MoveLeftovers(Path.GetDirectoryName(path)!, Path.GetDirectoryName(moved)!, root, o.Trash, now);
            changed++;
        }

        album.MusicBrainzReleaseId = releaseId;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Album {Album}: {Changed} Dateien auf Release {Release} gebracht, {Skipped} übersprungen",
            album.Title, changed, releaseId, skipped);
        return new ReleaseAlignResult(releaseId, changed, skipped);
    }

    private async Task<Album> LoadAsync(int albumId, CancellationToken ct) =>
        await db.Albums.Include(a => a.Tracks).ThenInclude(t => t.Files).SingleOrDefaultAsync(a => a.Id == albumId, ct)
            ?? throw new KeyNotFoundException($"Album {albumId} gibt es nicht");

    private List<(AudioFile File, string Path, AudioTags Tags)> Read(Album album)
    {
        var root = Path.GetFullPath(options.Value.Library);
        var rows = new List<(AudioFile, string, AudioTags)>();
        foreach (var file in album.Tracks.SelectMany(t => t.Files))
        {
            var path = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
                continue;
            try
            {
                rows.Add((file, path, tagReader.Read(path).Tags));
            }
            catch (InvalidDataException e)
            {
                logger.LogWarning("Tags von {Path} nicht lesbar: {Message}", file.Path, e.Message);
            }
        }

        return rows;
    }

    /// <summary>Tags in eine Kopie schreiben, prüfen, dann die Kopie an den Platz nach der Ordnerregel legen.</summary>
    private string Retag(string path, string root, ReleaseTrack track)
    {
        var relative = LibraryPathRule.For(
            track.AlbumArtist, track.Year, track.Album, track.Kind,
            track.Disc, track.DiscCount, track.Track, track.Title, Path.GetExtension(path));
        var target = Path.Combine(root, relative);
        var folder = Path.GetDirectoryName(target)!;
        for (var i = 2; target != path && File.Exists(target); i++)
            target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(relative)} ({i}){Path.GetExtension(relative)}");

        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $"{TempPrefix}{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            File.Copy(path, temp);
            tagWriter.Write(temp, track, cover: null);
            var written = tagReader.Read(temp).Tags;
            if (written.MusicBrainzReleaseId != track.ReleaseId || written.MusicBrainzRecordingId != track.RecordingId)
                throw new InvalidDataException($"Tags ließen sich nicht korrekt schreiben: {path}");
            File.Move(temp, target, overwrite: target == path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        if (target != path)
            File.Delete(path);
        return target;
    }

    /// <summary>Ein leerer alter Albumordner gibt sein Cover an den neuen ab und verschwindet.</summary>
    private static void MoveLeftovers(string oldFolder, string newFolder, string root, string trash, DateTimeOffset now)
    {
        if (oldFolder == newFolder || !Directory.Exists(oldFolder)
            || Directory.EnumerateFiles(oldFolder).Any(f => TagLibTagReader.SupportedExtensions.Contains(Path.GetExtension(f)) && !Path.GetFileName(f).StartsWith('.')))
            return;
        foreach (var name in new[] { "cover.jpg", "cover.png" })
        {
            var cover = Path.Combine(oldFolder, name);
            if (File.Exists(cover) && !File.Exists(Path.Combine(newFolder, name)))
                File.Move(cover, Path.Combine(newFolder, name));
        }

        DuplicateService.RemoveEmptyAlbumFolder(oldFolder, root, trash, now);
    }
}
