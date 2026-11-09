// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Infrastructure.Playlists;

/// <summary>
/// Eigene Playlists in der Datenbank, dazu je eine Kopie als <c>Library/Playlists/tonregal-liste-{id}.m3u8</c>: so liegt
/// die Liste auch in der Sicherung der Library, und Navidrome oder ein anderer Player kann sie lesen. Der Dateiname
/// hängt wie bei den Smart Playlists an der ID, damit ein Umbenennen keine zweite Liste in Navidrome anlegt.
/// </summary>
public sealed class PlaylistService(
    TonregalDbContext db,
    IOptions<TonregalOptions> options,
    TimeProvider time,
    ILogger<PlaylistService> logger) : IPlaylists
{
    public const int MaxTracks = 10000;
    private const string FilePrefix = "tonregal-liste-";

    private string Directory => Path.Combine(options.Value.Library, SmartPlaylists.Folder);

    public static string RelativePath(int id) => $"{SmartPlaylists.Folder}/{FilePrefix}{id}.m3u8";

    public async Task<IReadOnlyList<PlaylistSummary>> ListAsync(CancellationToken ct)
    {
        var playlists = await db.Playlists.AsNoTracking().ToListAsync(ct);
        var entries = (await db.PlaylistEntries.AsNoTracking()
                .Select(e => new EntryShape(e.PlaylistId, e.Position, e.Track.AlbumId, e.Track.DurationMs))
                .ToListAsync(ct))
            .ToLookup(e => e.PlaylistId);
        return [.. playlists
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => Summary(p, entries[p.Id]))];
    }

    public async Task<PlaylistDetail?> GetAsync(int id, CancellationToken ct)
    {
        if (await db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist)
            return null;
        var items = await ItemsAsync(id, ct);
        return new PlaylistDetail(Summary(playlist, items.Select((i, n) => new EntryShape(id, n, i.AlbumId, i.DurationMs))), items);
    }

    public async Task<PlaylistSummary> CreateAsync(PlaylistInput input, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var playlist = new Playlist { Name = "", CreatedAt = now };
        await ApplyAsync(playlist, input, ct);
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync(ct);
        await TryWriteAsync(playlist.Id, ct);
        return await SummaryAsync(playlist, ct);
    }

    public async Task<PlaylistSummary?> UpdateAsync(int id, PlaylistInput input, CancellationToken ct)
    {
        if (await db.Playlists.FindAsync([id], ct) is not { } playlist)
            return null;
        await ApplyAsync(playlist, input, ct);
        await db.SaveChangesAsync(ct);
        await TryWriteAsync(id, ct);
        return await SummaryAsync(playlist, ct);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        if (await db.Playlists.FindAsync([id], ct) is not { } playlist)
            return false;
        db.Playlists.Remove(playlist);
        await db.SaveChangesAsync(ct);
        try
        {
            File.Delete(Path.Combine(options.Value.Library, RelativePath(id)));
        }
        catch (IOException e)
        {
            logger.LogWarning("Playlist-Datei {Path} nicht gelöscht: {Message}", RelativePath(id), e.Message);
        }
        return true;
    }

    public async Task<PlaylistAddResult?> AddAsync(int id, IReadOnlyList<int> trackIds, CancellationToken ct)
    {
        if (await db.Playlists.Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist)
            return null;
        var wanted = await KnownAsync(trackIds, ct);
        var present = playlist.Entries.Select(e => e.TrackId).ToHashSet();
        var added = wanted.Where(present.Add).ToList();
        if (present.Count > MaxTracks)
            throw new ArgumentException($"Höchstens {MaxTracks} Stücke je Playlist", "trackIds");

        var now = time.GetUtcNow();
        var position = playlist.Entries.Count == 0 ? 0 : playlist.Entries.Max(e => e.Position) + 1;
        foreach (var trackId in added)
            playlist.Entries.Add(new PlaylistEntry { TrackId = trackId, Position = position++, AddedAt = now });
        if (added.Count > 0)
        {
            playlist.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await TryWriteAsync(id, ct);
        }
        return new PlaylistAddResult(added.Count, wanted.Count - added.Count, await SummaryAsync(playlist, ct));
    }

    public async Task<PlaylistDetail?> SetTracksAsync(int id, IReadOnlyList<int> trackIds, CancellationToken ct)
    {
        if (await db.Playlists.Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist)
            return null;
        var wanted = await KnownAsync(trackIds, ct);
        if (wanted.Count > MaxTracks)
            throw new ArgumentException($"Höchstens {MaxTracks} Stücke je Playlist", "trackIds");

        var now = time.GetUtcNow();
        var existing = playlist.Entries.ToDictionary(e => e.TrackId);
        playlist.Entries.RemoveAll(e => !wanted.Contains(e.TrackId));
        for (var position = 0; position < wanted.Count; position++)
        {
            if (existing.TryGetValue(wanted[position], out var entry))
                entry.Position = position;
            else
                playlist.Entries.Add(new PlaylistEntry { TrackId = wanted[position], Position = position, AddedAt = now });
        }
        playlist.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await TryWriteAsync(id, ct);
        return await GetAsync(id, ct);
    }

    public async Task<bool> RemoveAsync(int id, int trackId, CancellationToken ct)
    {
        if (await db.Playlists.Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist
            || playlist.Entries.Find(e => e.TrackId == trackId) is not { } entry)
            return false;
        playlist.Entries.Remove(entry);
        // Lückenlos halten, damit Anhängen und Sortieren mit einfachen Positionen auskommen.
        var position = 0;
        foreach (var rest in playlist.Entries.OrderBy(e => e.Position))
            rest.Position = position++;
        playlist.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await TryWriteAsync(id, ct);
        return true;
    }

    public async Task WriteFilesAsync(CancellationToken ct)
    {
        var ids = await db.Playlists.AsNoTracking().Select(p => p.Id).ToListAsync(ct);
        foreach (var id in ids)
            await WriteAsync(id, ct);

        if (!System.IO.Directory.Exists(Directory))
            return;
        var wanted = ids.Select(id => Path.GetFileName(RelativePath(id))).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in System.IO.Directory.EnumerateFiles(Directory, FilePrefix + "*.m3u8")
                     .Where(f => !wanted.Contains(Path.GetFileName(f))))
        {
            logger.LogInformation("Entferne verwaiste Playlist {File}", stale);
            File.Delete(stale);
        }
    }

    /// <summary>
    /// Erweitertes M3U mit Pfaden relativ zur Datei, wie Navidrome es liest; je Track die Datei, die auch der Player nimmt.
    /// </summary>
    public static string ToM3u(string name, IEnumerable<(PlaylistItem Item, string Path)> entries)
    {
        var text = new StringBuilder();
        text.Append("#EXTM3U\n");
        text.Append("#PLAYLIST:").Append(OneLine(name)).Append('\n');
        foreach (var (item, path) in entries)
        {
            text.Append("#EXTINF:").Append(item.DurationMs / 1000).Append(',')
                .Append(OneLine($"{item.Artist} - {item.Title}")).Append('\n');
            text.Append("../").Append(path).Append('\n');
        }
        return text.ToString();
    }

    private static string OneLine(string value) => value.ReplaceLineEndings(" ");

    /// <summary>Datenbank zuerst: fehlt die Library gerade, bleibt die Playlist gespeichert und die Datei kommt beim nächsten Start.</summary>
    private async Task TryWriteAsync(int id, CancellationToken ct)
    {
        try
        {
            await WriteAsync(id, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Playlist-Datei {Path} nicht geschrieben: {Message}", RelativePath(id), e.Message);
        }
    }

    /// <summary>Nur bei geändertem Inhalt, damit Navidrome nicht bei jedem Start alles neu einliest.</summary>
    private async Task WriteAsync(int id, CancellationToken ct)
    {
        if (await db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) is not { } playlist)
            return;
        var files = await EntriesAsync(id, ct);
        var content = ToM3u(playlist.Name, files.Select(f => (SmartPlaylists.Item(f), f.Path)));

        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(options.Value.Library, RelativePath(id));
        if (File.Exists(path) && await File.ReadAllTextAsync(path, ct) == content)
            return;
        var temp = Path.Combine(Directory, $".{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
        File.Move(temp, path, overwrite: true);
    }

    private async Task<List<PlaylistItem>> ItemsAsync(int id, CancellationToken ct) =>
        [.. (await EntriesAsync(id, ct)).Select(SmartPlaylists.Item)];

    /// <summary>Je Eintrag die bevorzugte Datei, in der Reihenfolge der Playlist.</summary>
    private async Task<List<AudioFile>> EntriesAsync(int id, CancellationToken ct)
    {
        var order = await db.PlaylistEntries.AsNoTracking()
            .Where(e => e.PlaylistId == id)
            .OrderBy(e => e.Position)
            .Select(e => e.TrackId)
            .ToListAsync(ct);
        var byId = await db.Tracks.AsNoTracking()
            .Where(t => order.Contains(t.Id))
            .Include(t => t.Artists)
            .Include(t => t.Album).ThenInclude(a => a.AlbumArtist)
            .Include(t => t.Files)
            .AsSplitQuery()
            .ToDictionaryAsync(t => t.Id, ct);
        var tracks = order.Where(byId.ContainsKey).Select(t => byId[t]);
        return [.. tracks.Where(t => t.Files.Count > 0).Select(SmartPlaylists.PreferredFile)];
    }

    /// <summary>Die angefragten Tracks ohne Doppelte, in ihrer Reihenfolge; unbekannte IDs sind ein Fehler.</summary>
    private async Task<List<int>> KnownAsync(IReadOnlyList<int>? trackIds, CancellationToken ct)
    {
        var wanted = (trackIds ?? []).Distinct().ToList();
        if (wanted.Count > MaxTracks)
            throw new ArgumentException($"Höchstens {MaxTracks} Stücke je Playlist", "trackIds");
        var known = (await db.Tracks.Where(t => wanted.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        if (wanted.Where(id => !known.Contains(id)).Select(id => (int?)id).FirstOrDefault() is { } unknown)
            throw new ArgumentException($"Track {unknown} gibt es nicht", "trackIds");
        return wanted;
    }

    private async Task<PlaylistSummary> SummaryAsync(Playlist playlist, CancellationToken ct)
    {
        var entries = await db.PlaylistEntries.AsNoTracking()
            .Where(e => e.PlaylistId == playlist.Id)
            .Select(e => new EntryShape(e.PlaylistId, e.Position, e.Track.AlbumId, e.Track.DurationMs))
            .ToListAsync(ct);
        return Summary(playlist, entries);
    }

    private static PlaylistSummary Summary(Playlist p, IEnumerable<EntryShape> entries)
    {
        var ordered = entries.OrderBy(e => e.Position).ToList();
        return new PlaylistSummary(
            p.Id, p.Name, p.Comment, ordered.Count, ordered.Sum(e => (long)e.DurationMs), p.UpdatedAt,
            [.. ordered.Select(e => e.AlbumId).Distinct().Take(4)]);
    }

    private sealed record EntryShape(int PlaylistId, int Position, int AlbumId, int DurationMs);

    private async Task ApplyAsync(Playlist playlist, PlaylistInput input, CancellationToken ct)
    {
        var name = input.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100)
            throw new ArgumentException("Name fehlt oder ist länger als 100 Zeichen", "name");
        if (input.Comment?.Length > 500)
            throw new ArgumentException("Kommentar höchstens 500 Zeichen", "comment");

        var lower = name.ToLowerInvariant();
        var taken = await db.Playlists.AsNoTracking().Where(p => p.Id != playlist.Id).Select(p => p.Name).ToListAsync(ct);
        if (taken.Any(t => t.ToLowerInvariant() == lower))
            throw new InvalidOperationException($"Es gibt schon eine Playlist „{name}“");

        playlist.Name = name;
        playlist.Comment = string.IsNullOrWhiteSpace(input.Comment) ? null : input.Comment.Trim();
        playlist.UpdatedAt = time.GetUtcNow();
    }
}
