// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Infrastructure.Library;

/// <summary>
/// Gleicht den DB-Index mit den Dateien in der Library ab. Unveränderte Dateien (gleiche Größe und
/// Änderungszeit) werden übersprungen, verschwundene aus dem Index entfernt. Die Dateien selbst
/// werden nie verändert.
/// </summary>
public sealed class LibraryScanner(
    TonregalDbContext db,
    ITagReader tagReader,
    IOptions<TonregalOptions> options,
    TimeProvider time,
    ILogger<LibraryScanner> logger) : ILibraryScanner
{
    public const string UnknownArtist = "Unbekannter Künstler";
    public const string UnknownAlbum = "Unbekanntes Album";
    private const int BatchSize = 50;

    private static readonly string[] CoverFileNames = ["cover.jpg", "cover.png", "folder.jpg", "folder.png", "front.jpg", "front.png"];

    private readonly Dictionary<string, Artist> _artists = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int ArtistId, string Title), Album> _albums = [];
    private readonly Dictionary<(Artist Artist, string Title), Album> _newAlbums = [];

    public async Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var o = options.Value;
        if (!File.Exists(o.MountMarker))
            throw new LibraryUnavailableException($"Mount-Marker {o.MountMarker} fehlt – ist die SSD gemountet?");

        var stopwatch = Stopwatch.StartNew();
        var root = Path.GetFullPath(o.Library);
        Directory.CreateDirectory(root);

        var paths = EnumerateAudioFiles(root).ToList();
        logger.LogInformation("Library-Scan: {Count} Audiodateien unter {Root}", paths.Count, root);

        await LoadCachesAsync(ct);
        var existing = await db.AudioFiles.Include(f => f.Track).ToDictionaryAsync(f => f.Path, StringComparer.Ordinal, ct);
        // Frisch aus der Inbox übernommene Dateien mit ihrem Import-Job verknüpfen.
        var imported = await db.ImportJobs
            .Where(j => j.State == ImportState.Imported && j.AudioFileId == null && j.LibraryPath != null)
            .ToDictionaryAsync(j => j.LibraryPath!, StringComparer.Ordinal, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<ScanError>();
        int added = 0, updated = 0, unchanged = 0, processed = 0;

        foreach (var fullPath in paths)
        {
            ct.ThrowIfCancellationRequested();
            var relative = ToRelative(root, fullPath);
            seen.Add(relative);
            processed++;

            try
            {
                var info = new FileInfo(fullPath);
                var modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                existing.TryGetValue(relative, out var audioFile);
                if (audioFile is not null && audioFile.SizeBytes == info.Length && audioFile.ModifiedAt == modifiedAt)
                {
                    unchanged++;
                }
                else
                {
                    var metadata = tagReader.Read(fullPath);
                    var hash = await HashAsync(fullPath, ct);
                    if (audioFile is null)
                    {
                        audioFile = new AudioFile { Path = relative, FileHash = hash, AddedAt = time.GetUtcNow(), Source = FileSource.Legacy };
                        db.AudioFiles.Add(audioFile);
                        if (imported.TryGetValue(relative, out var job))
                        {
                            audioFile.Source = job.Source;
                            job.AudioFile = audioFile;
                        }
                        existing[relative] = audioFile;
                        added++;
                    }
                    else
                    {
                        updated++;
                    }

                    Apply(audioFile, metadata, info, modifiedAt, hash, fullPath, root);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.LogWarning("Library-Scan: {Path} übersprungen: {Message}", relative, e.Message);
                errors.Add(new ScanError(relative, e.Message));
            }

            if (processed % BatchSize == 0)
            {
                await db.SaveChangesAsync(ct);
                progress?.Report(new ScanProgress(processed, paths.Count, relative));
            }
        }

        var removed = existing.Values.Where(f => !seen.Contains(f.Path)).ToList();
        db.AudioFiles.RemoveRange(removed);
        await db.SaveChangesAsync(ct);
        await RemoveOrphansAsync(db, ct);
        progress?.Report(new ScanProgress(processed, paths.Count, null));

        var result = new ScanResult(added, updated, removed.Count, unchanged, errors, stopwatch.Elapsed);
        logger.LogInformation(
            "Library-Scan fertig in {Duration}: {Added} neu, {Updated} geändert, {Removed} entfernt, {Unchanged} unverändert, {Errors} Fehler",
            result.Duration, added, updated, removed.Count, unchanged, errors.Count);
        return result;
    }

    private void Apply(AudioFile file, AudioFileInfo metadata, FileInfo info, DateTimeOffset modifiedAt, string hash, string fullPath, string root)
    {
        var (tags, props) = metadata;
        var compilation = tags.IsCompilation
            || string.Equals(tags.AlbumArtist, Artist.VariousArtists, StringComparison.OrdinalIgnoreCase);
        var trackArtists = tags.Artists.Count > 0
            ? tags.Artists.Select((name, i) => GetArtist(name, i == 0 ? tags.ArtistSort : null)).ToList()
            : [GetArtist(tags.AlbumArtist ?? UnknownArtist, tags.AlbumArtistSort)];
        var albumArtist = compilation
            ? GetArtist(Artist.VariousArtists, null)
            : tags.AlbumArtist is { } aa ? GetArtist(aa, tags.AlbumArtistSort) : trackArtists[0];

        var album = GetAlbum(albumArtist, tags.Album ?? UnknownAlbum);
        album.Year ??= tags.Year;
        album.MusicBrainzReleaseId ??= tags.MusicBrainzReleaseId;
        album.DiscCount = Math.Max(album.DiscCount, Math.Max(tags.DiscCount ?? 1, tags.Disc ?? 1));
        if (compilation)
            album.Kind = AlbumKind.Compilation;
        album.CoverPath ??= FindCover(Path.GetDirectoryName(fullPath)!, root);

        var title = tags.Title ?? Path.GetFileNameWithoutExtension(fullPath);
        var disc = tags.Disc ?? 1;
        var number = tags.Track ?? 0;

        // Eine zweite Datei desselben Songs (z. B. FLAC neben MP3) hängt am vorhandenen Track.
        var track = file.Track is { } current && current.Album == album
            ? current
            : album.Tracks.FirstOrDefault(t => t.Disc == disc && t.Number == number
                && string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase))
              ?? new Track { Title = title, Album = album };
        if (track.Id == 0 && !album.Tracks.Contains(track))
            album.Tracks.Add(track);

        track.Title = title;
        track.Disc = disc;
        track.Number = number;
        track.DurationMs = (int)props.Duration.TotalMilliseconds;
        track.MusicBrainzRecordingId = tags.MusicBrainzRecordingId ?? track.MusicBrainzRecordingId;
        track.AcoustId = tags.AcoustId ?? track.AcoustId;
        track.Bpm = tags.Bpm ?? track.Bpm;
        track.InitialKey = tags.InitialKey ?? track.InitialKey;
        track.ReplayGainTrack = tags.ReplayGainTrackGain ?? track.ReplayGainTrack;
        track.Genre = tags.Genre ?? track.Genre;
        foreach (var artist in trackArtists.Where(a => !track.Artists.Contains(a)))
            track.Artists.Add(artist);
        track.Artists.RemoveAll(a => !trackArtists.Contains(a));

        file.Track = track;
        file.Format = props.Format;
        file.Bitrate = props.Bitrate;
        file.SampleRate = props.SampleRate;
        file.Channels = props.Channels;
        file.SizeBytes = info.Length;
        file.ModifiedAt = modifiedAt;
        file.FileHash = hash;
    }

    private async Task LoadCachesAsync(CancellationToken ct)
    {
        foreach (var artist in await db.Artists.ToListAsync(ct))
            _artists.TryAdd(artist.Name, artist);
        foreach (var album in await db.Albums.Include(a => a.Tracks).ThenInclude(t => t.Artists).ToListAsync(ct))
            _albums.TryAdd((album.AlbumArtistId, album.Title.ToLowerInvariant()), album);
    }

    private Artist GetArtist(string name, string? sortName)
    {
        if (_artists.TryGetValue(name, out var artist))
            return artist;
        artist = new Artist { Name = name, SortName = sortName ?? DefaultSortName(name) };
        _artists[name] = artist;
        db.Artists.Add(artist);
        return artist;
    }

    private Album GetAlbum(Artist albumArtist, string title)
    {
        var key = title.ToLowerInvariant();
        if (albumArtist.Id != 0 && _albums.TryGetValue((albumArtist.Id, key), out var album))
            return album;
        if (_newAlbums.TryGetValue((albumArtist, key), out album))
            return album;

        album = new Album { Title = title, AlbumArtist = albumArtist };
        db.Albums.Add(album);
        _newAlbums[(albumArtist, key)] = album;
        return album;
    }

    /// <summary>Entfernt Tracks ohne Dateien, Alben ohne Tracks und Artists ohne Alben und Tracks.</summary>
    internal static async Task RemoveOrphansAsync(TonregalDbContext db, CancellationToken ct)
    {
        await db.Tracks.Where(t => !t.Files.Any()).ExecuteDeleteAsync(ct);
        await db.Albums.Where(a => !a.Tracks.Any()).ExecuteDeleteAsync(ct);
        await db.Artists.Where(a => !a.Albums.Any() && !a.Tracks.Any()).ExecuteDeleteAsync(ct);
    }

    internal static IEnumerable<string> EnumerateAudioFiles(string root)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        return Directory.EnumerateFiles(root, "*", enumeration)
            .Where(p => TagLibTagReader.SupportedExtensions.Contains(Path.GetExtension(p)))
            // .trash und andere Punkt-Ordner sowie macOS-Ressourcendateien (._*) gehören nicht zur Library.
            .Where(p => !Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar).Any(s => s.StartsWith('.')))
            .Order(StringComparer.Ordinal);
    }

    private static string? FindCover(string directory, string root) =>
        CoverFileNames
            .Select(name => Path.Combine(directory, name))
            .Where(File.Exists)
            .Select(path => ToRelative(root, path))
            .FirstOrDefault();

    private static string ToRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static string DefaultSortName(string name) =>
        name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && name.Length > 4 ? $"{name[4..]}, {name[..3]}" : name;

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
