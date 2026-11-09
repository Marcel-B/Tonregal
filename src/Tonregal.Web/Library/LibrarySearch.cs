// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Api;

namespace Tonregal.Web.Library;

/// <summary>Treffer einer Suche; <see cref="TrackCount"/> zählt alle passenden Titel, auch die über die Grenze hinaus.</summary>
public sealed record LibrarySearchResult(
    string Query, IReadOnlyList<ArtistSummary> Artists, IReadOnlyList<AlbumListItem> Albums, IReadOnlyList<PlaylistItem> Tracks, int TrackCount);

/// <summary>
/// Sucht Interpreten, Alben und Titel. Verglichen wird im Speicher statt mit SQL-LIKE, weil SQLite Groß- und
/// Kleinschreibung nur für ASCII ignoriert: „Ärzte“ fände „ärzte“ nicht, „Beyonce“ nicht „Beyoncé“. Jedes Wort der
/// Suche muss vorkommen, bei Titeln irgendwo in Titel, Interpreten oder Album („beatles help“ findet „Help!“).
/// </summary>
public static class LibrarySearch
{
    public const int MaxArtists = 10;
    public const int MaxAlbums = 20;
    public const int MaxTracks = 100;

    public static async Task<LibrarySearchResult> SearchAsync(TonregalDbContext db, string? query, CancellationToken ct)
    {
        var text = query?.Trim() ?? "";
        var needle = Normalize(text);
        var words = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return new LibrarySearchResult(text, [], [], [], 0);

        var artists = (await db.Artists.AsNoTracking()
                .Where(a => a.Albums.Any())
                .Select(a => new { a.Id, a.Name, a.SortName, AlbumCount = a.Albums.Count })
                .ToListAsync(ct))
            .Select(a => (Item: a, Name: Normalize(a.Name)))
            .Where(a => ContainsAll(a.Name, words))
            .OrderBy(a => Rank(a.Name, needle)).ThenBy(a => a.Item.SortName, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxArtists)
            .Select(a => new ArtistSummary(a.Item.Id, a.Item.Name, a.Item.SortName, a.Item.AlbumCount))
            .ToList();

        var albums = (await db.Albums.AsNoTracking()
                .Select(a => new { a.Id, a.Title, a.Year, a.Kind, TrackCount = a.Tracks.Count, ArtistId = a.AlbumArtist.Id, Artist = a.AlbumArtist.Name })
                .ToListAsync(ct))
            .Select(a => (Item: a, Title: Normalize(a.Title), All: Normalize($"{a.Title} {a.Artist}")))
            .Where(a => ContainsAll(a.All, words))
            .OrderBy(a => Rank(a.Title, needle)).ThenBy(a => a.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxAlbums)
            .Select(a => new AlbumListItem(a.Item.Id, a.Item.Title, a.Item.Year, a.Item.Kind, a.Item.TrackCount, new ArtistRef(a.Item.ArtistId, a.Item.Artist)))
            .ToList();

        // Erst schlanke Zeilen vergleichen, dann nur die Treffer samt Dateien laden.
        var tracks = (await db.Tracks.AsNoTracking()
                .Where(t => t.Files.Any())
                .Select(t => new { t.Id, t.Title, Album = t.Album.Title, AlbumArtist = t.Album.AlbumArtist.Name, Artists = t.Artists.Select(a => a.Name).ToList() })
                .ToListAsync(ct))
            .Select(t => (t.Id, t.Title, Normalized: Normalize(t.Title), All: Normalize($"{t.Title} {string.Join(' ', t.Artists)} {t.AlbumArtist} {t.Album}")))
            .Where(t => ContainsAll(t.All, words))
            .OrderBy(t => Rank(t.Normalized, needle)).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var ids = tracks.Take(MaxTracks).Select(t => t.Id).ToList();
        var loaded = await db.Tracks.AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .Include(t => t.Artists)
            .Include(t => t.Album).ThenInclude(a => a.AlbumArtist)
            .Include(t => t.Files)
            .AsSplitQuery()
            .ToDictionaryAsync(t => t.Id, ct);

        return new LibrarySearchResult(
            text, artists, albums, ids.Where(loaded.ContainsKey).Select(id => ListeningApi.Item(loaded[id])).ToList(), tracks.Count);
    }

    /// <summary>Klein, ohne Akzente und Satzzeichen: „AC/DC“ wird „acdc“, „Beyoncé“ „beyonce“, „Straße“ „strasse“.</summary>
    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var space = true;
        foreach (var c in value.Normalize(NormalizationForm.FormKD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            if (char.IsWhiteSpace(c) || c is '-' or '_')
            {
                if (!space)
                    builder.Append(' ');
                space = true;
            }
            else if (char.IsLetterOrDigit(c))
            {
                if (c == 'ß')
                    builder.Append("ss");
                else
                    builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static bool ContainsAll(string haystack, string[] words) => words.All(w => haystack.Contains(w, StringComparison.Ordinal));

    /// <summary>Genau so heißend vor „fängt so an“ vor „steht drin“ vor „Wörter verstreut“.</summary>
    private static int Rank(string name, string needle) =>
        name == needle ? 0 : name.StartsWith(needle, StringComparison.Ordinal) ? 1 : name.Contains(needle, StringComparison.Ordinal) ? 2 : 3;
}
