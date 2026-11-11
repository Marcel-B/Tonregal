// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Lyrics;
using Tonregal.Infrastructure.Persistence;
using LibraryPathRule = Tonregal.Domain.ValueObjects.LibraryPath;

namespace Tonregal.Infrastructure.Library;

/// <summary>
/// Tag-Editor: schreibt die Änderungen in eine Kopie jeder Datei, prüft sie, legt sie an den Platz nach der
/// Ordnerregel und liest die Dateien danach neu ein. Ändert sich nichts am Pfad, bleibt die Datei, wo sie ist, auch
/// wenn ihr Name nicht der Ordnerregel folgt.
/// </summary>
public sealed class TagEditor(
    TonregalDbContext db,
    ITagReader tagReader,
    ITagWriter tagWriter,
    ILibraryScanner scanner,
    IPlaylists playlists,
    LibraryScanLock scanLock,
    LibraryWriteLock writeLock,
    IOptions<TonregalOptions> options,
    TimeProvider time,
    ILogger<TagEditor> logger) : ITagEditor
{
    private const string TempPrefix = ".tonregal-";
    private const string Holder = "Tag-Editor";

    public async Task<TagEditResult> EditTrackAsync(int trackId, TagEdit edit, CancellationToken ct)
    {
        edit = Normalize(edit, singleTrack: true);
        using var _ = await scanLock.AcquireAsync(Holder, ct);
        using var __ = await writeLock.AcquireAsync(Holder, ct);
        var track = await db.Tracks.Include(t => t.Files).SingleOrDefaultAsync(t => t.Id == trackId, ct)
            ?? throw new KeyNotFoundException($"Track {trackId} gibt es nicht");
        // Wandert ein Track aus einem Album mit weiteren Tracks, gehört er nicht mehr zu dessen Release.
        var leavesAlbum = await db.Tracks.AnyAsync(t => t.AlbumId == track.AlbumId && t.Id != track.Id, ct);
        return await ApplyAsync(track.Files, edit, dropRelease: leavesAlbum, track.AlbumId, ct);
    }

    public async Task<TagEditResult> EditAlbumAsync(int albumId, TagEdit edit, CancellationToken ct)
    {
        edit = Normalize(edit, singleTrack: false);
        using var _ = await scanLock.AcquireAsync(Holder, ct);
        using var __ = await writeLock.AcquireAsync(Holder, ct);
        var album = await db.Albums.Include(a => a.Tracks).ThenInclude(t => t.Files).SingleOrDefaultAsync(a => a.Id == albumId, ct)
            ?? throw new KeyNotFoundException($"Album {albumId} gibt es nicht");
        return await ApplyAsync([.. album.Tracks.SelectMany(t => t.Files)], edit, dropRelease: false, album.Id, ct);
    }

    public async Task<TagEditResult> SetCoverAsync(int albumId, EmbeddedPicture cover, CancellationToken ct)
    {
        var mime = ImageType(cover.Data) ?? throw new TagEditException("Das Cover muss ein JPEG- oder PNG-Bild sein");
        cover = cover with { MimeType = mime };
        using var _ = await scanLock.AcquireAsync(Holder, ct);
        using var __ = await writeLock.AcquireAsync(Holder, ct);
        var album = await db.Albums.Include(a => a.Tracks).ThenInclude(t => t.Files).SingleOrDefaultAsync(a => a.Id == albumId, ct)
            ?? throw new KeyNotFoundException($"Album {albumId} gibt es nicht");
        var root = MountedLibrary();
        var now = time.GetUtcNow();
        var errors = new List<string>();
        var touched = new List<string>();
        var folders = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in album.Tracks.SelectMany(t => t.Files))
        {
            if (Resolve(root, file.Path) is not { } full)
            {
                errors.Add($"{file.Path}: Datei fehlt");
                continue;
            }

            try
            {
                Rewrite(full, full, temp =>
                {
                    tagWriter.ReplaceCover(temp, cover);
                    if (tagReader.ReadCover(temp) is null)
                        throw new InvalidDataException($"Cover ließ sich nicht einbetten: {Path.GetFileName(full)}");
                });
                folders.Add(Path.GetDirectoryName(full)!);
                touched.Add(file.Path);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{file.Path}: {e.Message}");
            }
        }

        // Navidrome und der Scanner nehmen cover.jpg vor dem eingebetteten Bild; das alte geht in den Papierkorb.
        var name = mime == "image/png" ? "cover.png" : "cover.jpg";
        foreach (var folder in folders)
        {
            foreach (var old in new[] { "cover.jpg", "cover.png" }.Select(n => Path.Combine(folder, n)).Where(File.Exists))
                LibraryTrash.Move(options.Value.Trash, old, LibraryScanner.ToRelative(root, old), now);
            await File.WriteAllBytesAsync(Path.Combine(folder, name), cover.Data, ct);
        }

        if (folders.Count > 0)
            album.CoverPath = LibraryScanner.FindCover(folders.First(), root);
        await db.SaveChangesAsync(ct);
        await scanner.RescanAsync(touched, ct);
        logger.LogInformation("Neues Cover für {Album}: {Count} Dateien, {Errors} Fehler", album.Title, touched.Count, errors.Count);
        return new TagEditResult(album.Id, touched.Count, 0, errors);
    }

    private async Task<TagEditResult> ApplyAsync(IReadOnlyList<AudioFile> files, TagEdit edit, bool dropRelease, int albumId, CancellationToken ct)
    {
        var root = MountedLibrary();
        var o = options.Value;
        var now = time.GetUtcNow();
        var errors = new List<string>();
        var touched = new List<AudioFile>();
        var moved = new HashSet<string>(StringComparer.Ordinal);
        HashSet<int> formerAlbums = [albumId];
        var leftFolders = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (Resolve(root, file.Path) is not { } full)
            {
                errors.Add($"{file.Path}: Datei fehlt");
                continue;
            }

            try
            {
                var before = tagReader.Read(full).Tags;
                var changes = Changes(before, edit, dropRelease);
                if (changes.IsEmpty)
                    continue;

                var after = Merge(before, changes);
                var extension = Path.GetExtension(full);
                var target = full;
                if (RulePath(before, full) != RulePath(after, full))
                {
                    var relative = RulePath(after, full);
                    target = Path.Combine(root, relative);
                    var folder = Path.GetDirectoryName(target)!;
                    // Belegter Platz: daneben ablegen, die Dublettenerkennung zeigt danach beide.
                    for (var i = 2; !SameFile(target, full) && File.Exists(target); i++)
                        target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(relative)} ({i}){extension}");
                }

                Rewrite(full, target, temp =>
                {
                    tagWriter.WriteChanges(temp, changes);
                    var written = tagReader.Read(temp).Tags;
                    if ((changes.Title is { } title && written.Title != title) || (changes.Album is { } album && written.Album != album))
                        throw new InvalidDataException($"Tags ließen sich nicht korrekt schreiben (gelesen: „{written.Title}“)");
                });

                if (target != full)
                {
                    target = OnDisk(root, target);
                    MoveSidecar(full, target);
                    leftFolders[Path.GetDirectoryName(full)!] = Path.GetDirectoryName(target)!;
                }

                file.Path = LibraryScanner.ToRelative(root, target);
                if (target != full)
                    moved.Add(file.Path);
                touched.Add(file);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Tag-Editor: {Path} nicht geändert: {Message}", file.Path, e.Message);
                errors.Add($"{file.Path}: {e.Message}");
            }
        }

        foreach (var (oldFolder, newFolder) in leftFolders)
            ReleaseAligner.MoveLeftovers(oldFolder, newFolder, root, o.Trash, now);

        // Die neuen Pfade müssen im Index stehen, bevor der Scanner sie liest, sonst hielte er sie für neue Dateien.
        await db.SaveChangesAsync(ct);
        var paths = touched.Select(f => f.Path).ToList();
        await scanner.RescanAsync(paths, ct);
        db.ChangeTracker.Clear();

        var albumIds = await FixUpAsync(paths, edit, moved, formerAlbums, root, ct);
        if (moved.Count > 0)
            await playlists.WriteFilesAsync(ct);

        logger.LogInformation("Tag-Editor: {Changed} Dateien geändert, {Moved} umbenannt, {Errors} Fehler",
            touched.Count, moved.Count, errors.Count);
        // Das Album, in dem jetzt die meisten der geänderten Tracks stehen; ohne Änderung das bisherige.
        var resultAlbum = albumIds.Count > 0 ? albumIds.GroupBy(id => id).MaxBy(g => g.Count())!.Key : (int?)albumId;
        if (resultAlbum == albumId && !await db.Albums.AnyAsync(a => a.Id == albumId, ct))
            resultAlbum = null;
        return new TagEditResult(resultAlbum, touched.Count, moved.Count, errors);
    }

    /// <summary>
    /// Was der Scanner nur ergänzt, nie löscht oder ersetzt (Jahr des Albums, Genre, Cover-Pfad), setzt der Editor
    /// selbst. Umbenannte Dateien haben in Navidrome eine neue ID; die Bewertung muss noch einmal hin.
    /// </summary>
    private async Task<List<int>> FixUpAsync(
        List<string> paths, TagEdit edit, HashSet<string> moved, HashSet<int> formerAlbums, string root, CancellationToken ct)
    {
        var files = await db.AudioFiles.Include(f => f.Track).ThenInclude(t => t.Album)
            .Where(f => paths.Contains(f.Path)).ToListAsync(ct);
        foreach (var file in files)
        {
            var track = file.Track;
            if (edit.Genre is { } genre)
                track.Genre = genre.Length == 0 ? null : genre;
            if (edit.Year is { } year)
                track.Album.Year = year == 0 ? null : year;
            if (track.RatedAt is not null && moved.Contains(file.Path))
                track.RatingPending = true;
            var folder = Path.GetDirectoryName(Path.Combine(root, file.Path))!;
            track.Album.CoverPath = LibraryScanner.FindCover(folder, root)
                ?? (Resolve(root, track.Album.CoverPath) is not null ? track.Album.CoverPath : null);
        }

        // Alben, aus denen Tracks weggezogen sind, können ihren Ordner samt Cover verloren haben.
        foreach (var album in await db.Albums.Where(a => formerAlbums.Contains(a.Id) && a.CoverPath != null).ToListAsync(ct))
        {
            if (Resolve(root, album.CoverPath) is null)
                album.CoverPath = null;
        }

        await db.SaveChangesAsync(ct);
        return files.Select(f => f.Track.AlbumId).ToList();
    }

    private static TagEdit Normalize(TagEdit edit, bool singleTrack)
    {
        if (!singleTrack && (edit.Title is not null || edit.Artists is not null || edit.Number is not null || edit.Disc is not null))
            throw new TagEditException("Titel, Interpreten, Nummer und Disc lassen sich nur je Track ändern");

        var title = edit.Title?.Trim();
        if (title is { Length: 0 })
            throw new TagEditException("Der Titel darf nicht leer sein");
        var artists = edit.Artists?.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (artists is { Count: 0 })
            throw new TagEditException("Ein Track braucht mindestens einen Interpreten");
        var albumArtist = edit.AlbumArtist?.Trim();
        if (albumArtist is { Length: 0 })
            throw new TagEditException("Der Album-Interpret darf nicht leer sein");
        var album = edit.Album?.Trim();
        if (album is { Length: 0 })
            throw new TagEditException("Der Albumtitel darf nicht leer sein");
        if (edit.Year is < 0 or > 9999)
            throw new TagEditException("Das Jahr muss zwischen 1 und 9999 liegen (0 entfernt es)");
        if (edit.Number is < 0 or > 999)
            throw new TagEditException("Die Tracknummer muss zwischen 0 und 999 liegen");
        if (edit.Disc is < 1 or > 99)
            throw new TagEditException("Die Disc muss zwischen 1 und 99 liegen");

        return new TagEdit
        {
            Title = title,
            Artists = artists,
            AlbumArtist = albumArtist,
            Album = album,
            Year = edit.Year,
            Genre = edit.Genre?.Trim(),
            Number = edit.Number,
            Disc = edit.Disc,
        };
    }

    /// <summary>Nur was sich gegenüber der Datei wirklich ändert.</summary>
    internal static TagChanges Changes(AudioTags tags, TagEdit edit, bool dropRelease)
    {
        var oldAlbumArtist = tags.AlbumArtist ?? tags.Artists.FirstOrDefault();
        var albumArtist = edit.AlbumArtist is { } aa && aa != oldAlbumArtist ? aa : null;

        IReadOnlyList<string>? artists = null;
        if (edit.Artists is { } given && !given.SequenceEqual(tags.Artists))
        {
            artists = given;
        }
        else if (edit.Artists is null && albumArtist is not null && oldAlbumArtist is not null)
        {
            // Ein neuer Album-Interpret nimmt die Tracks mit, die er selbst singt; Gäste bleiben.
            var following = tags.Artists.Select(a => string.Equals(a, oldAlbumArtist, StringComparison.OrdinalIgnoreCase) ? albumArtist : a).ToList();
            if (!following.SequenceEqual(tags.Artists))
                artists = following;
        }

        var album = edit.Album is { } title && title != tags.Album ? title : null;
        return new TagChanges
        {
            Title = edit.Title is { } t && t != tags.Title ? t : null,
            Artists = artists,
            AlbumArtist = albumArtist,
            Album = album,
            Year = edit.Year is { } year && year != (tags.Year ?? 0) ? year : null,
            Genre = edit.Genre is { } genre && genre != (tags.Genre ?? "") ? genre : null,
            Track = edit.Number is { } number && number != (tags.Track ?? 0) ? number : null,
            Disc = edit.Disc is { } disc && disc != (tags.Disc ?? 1) ? disc : null,
            // Wer das Album einem Interpreten gibt, macht es zu dessen Album; „Various Artists“ zur Compilation.
            Compilation = albumArtist is null ? null
                : string.Equals(albumArtist, Artist.VariousArtists, StringComparison.OrdinalIgnoreCase) is var va && va != tags.IsCompilation ? va : null,
            DropRelease = dropRelease && (album is not null || albumArtist is not null) && tags.MusicBrainzReleaseId is not null,
        };
    }

    /// <summary>Die Tags, wie sie nach <paramref name="changes"/> in der Datei stehen, soweit sie den Pfad bestimmen.</summary>
    private static AudioTags Merge(AudioTags tags, TagChanges changes) => tags with
    {
        Title = changes.Title ?? tags.Title,
        Artists = changes.Artists ?? tags.Artists,
        AlbumArtist = changes.AlbumArtist ?? tags.AlbumArtist,
        Album = changes.Album ?? tags.Album,
        Year = changes.Year is { } year ? year == 0 ? null : year : tags.Year,
        Track = changes.Track is { } track ? track == 0 ? null : track : tags.Track,
        Disc = changes.Disc ?? tags.Disc,
        DiscCount = Math.Max(tags.DiscCount ?? 1, changes.Disc ?? tags.Disc ?? 1),
        IsCompilation = changes.Compilation ?? tags.IsCompilation,
    };

    /// <summary>Pfad nach der Ordnerregel, so wie der Import ihn für diese Tags wählen würde.</summary>
    internal static string RulePath(AudioTags tags, string file)
    {
        var compilation = tags.IsCompilation || string.Equals(tags.AlbumArtist, Artist.VariousArtists, StringComparison.OrdinalIgnoreCase);
        var albumArtist = tags.AlbumArtist ?? tags.Artists.FirstOrDefault() ?? LibraryScanner.UnknownArtist;
        var album = tags.Album ?? LibraryScanner.UnknownAlbum;
        var title = tags.Title ?? Path.GetFileNameWithoutExtension(file);
        var extension = Path.GetExtension(file);
        var folder = LibraryPathRule.AlbumFolder(albumArtist, album == LibraryScanner.UnknownAlbum ? null : tags.Year, album,
            compilation ? AlbumKind.Compilation : AlbumKind.Album);
        var name = tags.Track is > 0 and var number
            ? LibraryPathRule.FileName(tags.Disc ?? 1, Math.Max(tags.DiscCount ?? 1, tags.Disc ?? 1), number, title, extension)
            : LibraryPathRule.Sanitize(title) + extension.ToLowerInvariant();
        return Path.Combine(folder, name);
    }

    /// <summary>
    /// Schreibt in eine Kopie im Zielordner und legt sie erst nach <paramref name="write"/> (samt Prüfung) an ihren Platz.
    /// Schlägt etwas fehl, bleibt das Original, wie es war.
    /// </summary>
    private static void Rewrite(string full, string target, Action<string> write)
    {
        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $"{TempPrefix}{Guid.NewGuid():N}{Path.GetExtension(target)}");
        try
        {
            File.Copy(full, temp);
            write(temp);
            if (target == full)
            {
                File.Move(temp, target, overwrite: true);
            }
            else if (SameFile(target, full))
            {
                // Nur Groß- und Kleinschreibung anders, auf macOS dieselbe Datei: erst das Original weg, die Kopie ist geprüft.
                File.Delete(full);
                File.Move(temp, target);
            }
            else
            {
                File.Move(temp, target, overwrite: false);
                File.Delete(full);
            }
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>Der Songtext neben der Datei zieht mit um; liegt am Ziel schon einer, bleibt der.</summary>
    private static void MoveSidecar(string from, string to)
    {
        var sidecar = LyricsService.SidecarOf(from);
        var target = LyricsService.SidecarOf(to);
        if (!File.Exists(sidecar))
            return;
        if (SameFile(sidecar, target))
            File.Move(sidecar, target, overwrite: true);
        else if (!File.Exists(target))
            File.Move(sidecar, target);
    }

    /// <summary>Zwei Pfade für dieselbe Datei, auch wenn sie sich auf einem Dateisystem ohne Groß/klein nur darin unterscheiden.</summary>
    private static bool SameFile(string a, string b) =>
        a == b || (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) && File.Exists(a) && File.Exists(b));

    /// <summary>
    /// Der Pfad, wie er auf der Platte steht: Auf macOS bleibt ein Ordner „Abba“ so, auch wenn die Datei nach
    /// „ABBA/…“ verschoben wurde, und der Index muss die Schreibweise der Platte tragen.
    /// </summary>
    private static string OnDisk(string root, string full)
    {
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            var names = Directory.EnumerateFileSystemEntries(current).Select(Path.GetFileName).ToList();
            var name = names.FirstOrDefault(n => n == segment)
                ?? names.FirstOrDefault(n => string.Equals(n, segment, StringComparison.OrdinalIgnoreCase))
                ?? segment;
            current = Path.Combine(current, name);
        }

        return current;
    }

    private string MountedLibrary()
    {
        var o = options.Value;
        if (!File.Exists(o.MountMarker))
            throw new LibraryUnavailableException($"Mount-Marker {o.MountMarker} fehlt – ist die SSD gemountet?");
        return Path.GetFullPath(o.Library);
    }

    private static string? Resolve(string root, string? relative)
    {
        if (string.IsNullOrEmpty(relative))
            return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    internal static string? ImageType(byte[] data) => data switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        _ => null,
    };
}
