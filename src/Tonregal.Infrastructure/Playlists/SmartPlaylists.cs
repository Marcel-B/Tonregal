// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Infrastructure.Playlists;

/// <summary>
/// Hält <c>Library/Playlists/tonregal-{id}.nsp</c> im Stand der Datenbank. Der Dateiname hängt an der ID, nicht am
/// Namen, weil Navidrome eine Playlist über ihren Pfad wiedererkennt: ein Umbenennen ändert so nur den Namen in
/// Navidrome, statt eine zweite Playlist anzulegen.
/// </summary>
public sealed class SmartPlaylists(
    TonregalDbContext db,
    IOptions<TonregalOptions> options,
    INavidromeClient navidrome,
    TimeProvider time,
    ILogger<SmartPlaylists> logger) : ISmartPlaylists
{
    public const string Folder = "Playlists";
    public const string DefaultComment = "Von Tonregal verwaltet, Änderungen in Tonregal machen";
    private const string FilePrefix = "tonregal-";

    private string Directory => Path.Combine(options.Value.Library, Folder);

    public static string RelativePath(int id) => $"{Folder}/{FilePrefix}{id}.nsp";

    public async Task<IReadOnlyList<SmartPlaylistInfo>> ListAsync(CancellationToken ct)
    {
        var playlists = await db.SmartPlaylists.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var infos = new List<SmartPlaylistInfo>(playlists.Count);
        foreach (var playlist in playlists)
            infos.Add(await InfoAsync(playlist, ct));
        return infos;
    }

    public async Task<IReadOnlyList<KeyCount>> KeysAsync(CancellationToken ct) =>
        (await db.Tracks.Where(t => t.InitialKey != null && t.InitialKey != "")
            .GroupBy(t => t.InitialKey!)
            .Select(g => new KeyCount(g.Key, g.Count()))
            .ToListAsync(ct))
        .OrderBy(k => k.Key, StringComparer.Ordinal)
        .ToList();

    public async Task<SmartPlaylistInfo> CreateAsync(SmartPlaylistInput input, CancellationToken ct)
    {
        var playlist = new SmartPlaylist { Name = "" };
        await ApplyAsync(playlist, input, ct);
        db.SmartPlaylists.Add(playlist);
        await db.SaveChangesAsync(ct);
        await WriteAsync(playlist, ct);
        return await InfoAsync(playlist, ct);
    }

    public async Task<SmartPlaylistInfo?> UpdateAsync(int id, SmartPlaylistInput input, CancellationToken ct)
    {
        if (await db.SmartPlaylists.FindAsync([id], ct) is not { } playlist)
            return null;
        await ApplyAsync(playlist, input, ct);
        await db.SaveChangesAsync(ct);
        await WriteAsync(playlist, ct);
        return await InfoAsync(playlist, ct);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        if (await db.SmartPlaylists.FindAsync([id], ct) is not { } playlist)
            return false;
        db.SmartPlaylists.Remove(playlist);
        await db.SaveChangesAsync(ct);
        File.Delete(Path.Combine(options.Value.Library, RelativePath(id)));
        if (!await navidrome.DeletePlaylistAsync(RelativePath(id), ct))
            logger.LogInformation("Smart Playlist {Name} gelöscht; in Navidrome ggf. von Hand entfernen", playlist.Name);
        return true;
    }

    public async Task WriteFilesAsync(CancellationToken ct)
    {
        var playlists = await db.SmartPlaylists.AsNoTracking().ToListAsync(ct);
        foreach (var playlist in playlists)
            await WriteAsync(playlist, ct);

        var wanted = playlists.Select(p => Path.GetFileName(RelativePath(p.Id))).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in System.IO.Directory.EnumerateFiles(Directory, FilePrefix + "*.nsp")
                     .Where(f => !wanted.Contains(Path.GetFileName(f))))
        {
            logger.LogInformation("Entferne verwaiste Smart Playlist {File}", stale);
            File.Delete(stale);
        }
    }

    /// <summary>Navidromes Format, siehe https://www.navidrome.org/docs/usage/smartplaylists/.</summary>
    public static string ToNsp(SmartPlaylist p)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteString("comment", string.IsNullOrWhiteSpace(p.Comment) ? DefaultComment : p.Comment);
            w.WriteStartArray("all");
            if (p.BpmMin is not null || p.BpmMax is not null)
            {
                // Navidrome speichert fehlende BPM als 0; die Untergrenze 1 hält ungemessene Stücke heraus.
                Rule(w, "inTheRange", "bpm", () =>
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(p.BpmMin ?? 1);
                    w.WriteNumberValue(p.BpmMax ?? 999);
                    w.WriteEndArray();
                });
            }

            if (p.Keys.Count > 0)
            {
                w.WriteStartObject();
                w.WriteStartArray("any");
                foreach (var key in p.Keys)
                    Rule(w, "is", "key", () => w.WriteStringValue(key));
                w.WriteEndArray();
                w.WriteEndObject();
            }

            if (!string.IsNullOrWhiteSpace(p.Genre))
                Rule(w, "contains", "genre", () => w.WriteStringValue(p.Genre));
            if (p.AddedWithinDays is { } days)
                Rule(w, "inTheLast", "dateadded", () => w.WriteNumberValue(days));
            if (p.NeverPlayed)
                Rule(w, "is", "playcount", () => w.WriteNumberValue(0));
            if (p.MinRating is { } stars)
            {
                Rule(w, "inTheRange", "rating", () =>
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(stars);
                    w.WriteNumberValue(5);
                    w.WriteEndArray();
                });
            }
            w.WriteEndArray();

            var (sort, order) = p.Sort switch
            {
                SmartPlaylistSort.Newest => ("dateadded", "desc"),
                SmartPlaylistSort.Bpm => ("bpm", "asc"),
                SmartPlaylistSort.Title => ("title", "asc"),
                SmartPlaylistSort.Artist => ("artist", "asc"),
                _ => ("random", "asc"),
            };
            w.WriteString("sort", sort);
            w.WriteString("order", order);
            if (p.Limit is { } limit)
                w.WriteNumber("limit", limit);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static void Rule(Utf8JsonWriter w, string op, string field, Action value)
    {
        w.WriteStartObject();
        w.WriteStartObject(op);
        w.WritePropertyName(field);
        value();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>Nur bei geändertem Inhalt, damit Navidrome nicht bei jedem Start alles neu einliest.</summary>
    private async Task WriteAsync(SmartPlaylist playlist, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(options.Value.Library, RelativePath(playlist.Id));
        var content = ToNsp(playlist);
        if (File.Exists(path) && await File.ReadAllTextAsync(path, ct) == content)
            return;
        var temp = Path.Combine(Directory, $".{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
        File.Move(temp, path, overwrite: true);
    }

    private async Task<SmartPlaylistInfo> InfoAsync(SmartPlaylist p, CancellationToken ct) => new(
        p.Id, p.Name, p.Comment, p.BpmMin, p.BpmMax, p.Keys, p.Genre, p.AddedWithinDays, p.NeverPlayed, p.Sort, p.Limit, p.UpdatedAt,
        RelativePath(p.Id), await MatchingAsync(p, ct), ToNsp(p), p.MinRating);

    /// <summary>Dieselben Regeln gegen Tonregals Bibliothek; „neu“ zählt hier ab der Übernahme der ersten Datei.</summary>
    private async Task<int> MatchingAsync(SmartPlaylist p, CancellationToken ct)
    {
        var count = await Filtered(p).CountAsync(ct);
        return p.Limit is { } limit ? Math.Min(count, limit) : count;
    }

    /// <summary>Alle Regeln außer „nie gehört“ und der Bewertung, die in Navidrome liegen.</summary>
    private IQueryable<Track> Filtered(SmartPlaylist p)
    {
        var tracks = db.Tracks.Where(t => t.Files.Any());
        if (p.BpmMin is not null || p.BpmMax is not null)
        {
            var (min, max) = (p.BpmMin ?? 1, p.BpmMax ?? 999);
            tracks = tracks.Where(t => t.Bpm >= min && t.Bpm <= max);
        }

        if (p.Keys.Count > 0)
        {
            var keys = p.Keys.ToList();
            tracks = tracks.Where(t => t.InitialKey != null && keys.Contains(t.InitialKey));
        }

        if (!string.IsNullOrWhiteSpace(p.Genre))
        {
            var pattern = $"%{p.Genre.Replace("%", "").Replace("_", "")}%";
            tracks = tracks.Where(t => t.Genre != null && EF.Functions.Like(t.Genre, pattern));
        }

        if (p.AddedWithinDays is { } days)
        {
            var since = time.GetUtcNow().AddDays(-days);
            tracks = tracks.Where(t => t.Files.Any(f => f.AddedAt >= since));
        }

        return tracks;
    }

    public async Task<SmartPlaylistTracks?> TracksAsync(int id, CancellationToken ct)
    {
        if (await db.SmartPlaylists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist)
            return null;

        if (await navidrome.PlaylistFilesAsync(RelativePath(id), ct) is { } paths)
        {
            var files = await db.AudioFiles.AsNoTracking()
                .Where(f => paths.Contains(f.Path))
                .Include(f => f.Track).ThenInclude(t => t.Artists)
                .Include(f => f.Track).ThenInclude(t => t.Album).ThenInclude(a => a.AlbumArtist)
                .ToListAsync(ct);
            var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            // Eine Datei je Track, auch wenn Navidrome MP3 und FLAC desselben Stücks führt.
            var items = paths.Where(byPath.ContainsKey).Select(p => Item(byPath[p])).DistinctBy(i => i.TrackId).ToList();
            return new SmartPlaylistTracks(true, items);
        }

        var tracks = await Filtered(playlist)
            .Include(t => t.Artists)
            .Include(t => t.Album).ThenInclude(a => a.AlbumArtist)
            .Include(t => t.Files)
            .AsNoTracking()
            .ToListAsync(ct);
        if (playlist.MinRating is { } stars)
        {
            var ratings = await navidrome.RatingsAsync([.. tracks.SelectMany(t => t.Files).Select(f => f.Path)], ct);
            tracks = [.. tracks.Where(t => t.Files.Any(f => ratings.TryGetValue(f.Path, out var r) && r >= stars))];
        }

        IEnumerable<Track> ordered = playlist.Sort switch
        {
            SmartPlaylistSort.Newest => tracks.OrderByDescending(t => t.Files.Min(f => f.AddedAt)),
            SmartPlaylistSort.Bpm => tracks.OrderBy(t => t.Bpm ?? double.MaxValue),
            SmartPlaylistSort.Title => tracks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            SmartPlaylistSort.Artist => tracks.OrderBy(t => ArtistName(t), StringComparer.CurrentCultureIgnoreCase),
            _ => tracks.OrderBy(_ => Random.Shared.Next()),
        };
        var fallback = ordered.Take(playlist.Limit ?? MaxFallbackTracks)
            .Select(t => Item(t.Files.OrderByDescending(f => f.Format == AudioFormat.Flac).ThenByDescending(f => f.Bitrate).First()))
            .ToList();
        return new SmartPlaylistTracks(false, fallback);
    }

    /// <summary>Obergrenze, wenn Tonregal ohne Höchstzahl selbst auswertet; mehr braucht keine Warteschlange.</summary>
    private const int MaxFallbackTracks = 1000;

    private static string ArtistName(Track t) =>
        t.Artists.Count > 0 ? string.Join(", ", t.Artists.Select(a => a.Name)) : t.Album.AlbumArtist.Name;

    private static PlaylistItem Item(AudioFile f) =>
        new(f.Id, f.TrackId, f.Track.Title, ArtistName(f.Track), f.Track.Album.Title, f.Track.AlbumId, f.Track.DurationMs);

    private async Task ApplyAsync(SmartPlaylist playlist, SmartPlaylistInput input, CancellationToken ct)
    {
        var name = input.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100)
            throw new ArgumentException("Name fehlt oder ist länger als 100 Zeichen", "name");
        if (input.Comment?.Length > 500)
            throw new ArgumentException("Kommentar höchstens 500 Zeichen", "comment");
        if (input.BpmMin is <= 0 or > 999 || input.BpmMax is <= 0 or > 999)
            throw new ArgumentException("BPM zwischen 1 und 999", "bpm");
        if (input.BpmMin > input.BpmMax)
            throw new ArgumentException("BPM von ist größer als bis", "bpm");
        var keys = (input.Keys ?? []).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count > 24 || keys.Any(k => k.Length > 8))
            throw new ArgumentException("Höchstens 24 Tonarten mit je bis zu 8 Zeichen", "keys");
        var genre = string.IsNullOrWhiteSpace(input.Genre) ? null : input.Genre.Trim();
        if (genre?.Length > 100)
            throw new ArgumentException("Genre höchstens 100 Zeichen", "genre");
        if (input.AddedWithinDays is < 1 or > 3650)
            throw new ArgumentException("Tage zwischen 1 und 3650", "addedWithinDays");
        if (input.Limit is < 1 or > 10000)
            throw new ArgumentException("Höchstzahl zwischen 1 und 10000", "limit");
        if (input.MinRating is < 1 or > 5)
            throw new ArgumentException("Bewertung zwischen 1 und 5 Sternen", "minRating");
        if (!Enum.IsDefined(input.Sort))
            throw new ArgumentException("Unbekannte Sortierung", "sort");
        if (input.BpmMin is null && input.BpmMax is null && keys.Count == 0 && genre is null && input.AddedWithinDays is null && !input.NeverPlayed
            && input.MinRating is null)
            throw new ArgumentException("Mindestens eine Regel setzen", "rules");

        var lower = name.ToLowerInvariant();
        var taken = await db.SmartPlaylists.AsNoTracking().Where(p => p.Id != playlist.Id).Select(p => p.Name).ToListAsync(ct);
        if (taken.Any(t => t.ToLowerInvariant() == lower))
            throw new InvalidOperationException($"Es gibt schon eine Smart Playlist „{name}“");

        playlist.Name = name;
        playlist.Comment = string.IsNullOrWhiteSpace(input.Comment) ? null : input.Comment.Trim();
        playlist.BpmMin = input.BpmMin;
        playlist.BpmMax = input.BpmMax;
        playlist.Keys = keys;
        playlist.Genre = genre;
        playlist.AddedWithinDays = input.AddedWithinDays;
        playlist.NeverPlayed = input.NeverPlayed;
        playlist.MinRating = input.MinRating;
        playlist.Sort = input.Sort;
        playlist.Limit = input.Limit;
        playlist.UpdatedAt = time.GetUtcNow();
    }
}
