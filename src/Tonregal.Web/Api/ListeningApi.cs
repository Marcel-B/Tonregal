// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Web.Api;

public sealed record RatingInput(int Rating);

/// <summary>
/// Bewertungen und Zufallswiedergabe für die Player. Bewertungen liegen in Navidrome (Subsonic <c>setRating</c>), damit
/// Smart Playlists und andere Subsonic-Apps sie sehen; Tonregal speichert sie nicht selbst.
/// </summary>
public static class ListeningApi
{
    /// <summary>Mehr braucht keine Warteschlange; die ganze Bibliothek kommt so trotzdem in zufälliger Auswahl.</summary>
    public const int MaxShuffle = 2000;

    public static RouteGroupBuilder MapListeningApi(this RouteGroupBuilder api)
    {
        var player = api.MapGroup("/player");

        // Bewertung je Datei-ID ({"21": 4}); unbewertete fehlen. Ohne Navidrome bleibt die Antwort leer.
        player.MapGet("/ratings", async (int[]? files, TonregalDbContext db, INavidromeClient navidrome, CancellationToken ct) =>
        {
            var ids = (files ?? []).Distinct().Take(1000).ToList();
            var paths = await db.AudioFiles.Where(f => ids.Contains(f.Id)).Select(f => new { f.Id, f.Path }).ToListAsync(ct);
            var ratings = await navidrome.RatingsAsync([.. paths.Select(p => p.Path)], ct);
            return paths.Where(p => ratings.ContainsKey(p.Path)).ToDictionary(p => p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), p => ratings[p.Path]);
        });

        player.MapPut("/files/{id:int}/rating", async Task<IResult> (
            int id, RatingInput input, TonregalDbContext db, INavidromeClient navidrome, CancellationToken ct) =>
        {
            if (input.Rating is < 0 or > 5)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["rating"] = ["0 bis 5 Sterne"] });
            var path = await db.AudioFiles.Where(f => f.Id == id).Select(f => f.Path).SingleOrDefaultAsync(ct);
            if (path is null)
                return TypedResults.NotFound();
            if (await navidrome.FindSongIdAsync(path, ct) is not { } songId)
                return TypedResults.Problem(
                    "Navidrome kennt das Stück noch nicht (Scan alle 15 Minuten) oder ist nicht eingerichtet.",
                    statusCode: StatusCodes.Status409Conflict);
            return await navidrome.SetRatingAsync(songId, input.Rating, ct)
                ? TypedResults.NoContent()
                : TypedResults.Problem("Navidrome hat die Bewertung nicht angenommen", statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        // Zufällige Warteschlange: mit artist alle Stücke des Interpreten (eigene Alben und Gastauftritte), sonst die ganze Bibliothek.
        player.MapGet("/shuffle", async Task<IResult> (int? artist, int? limit, TonregalDbContext db, CancellationToken ct) =>
        {
            var tracks = db.Tracks.AsNoTracking().Where(t => t.Files.Any());
            if (artist is { } artistId)
            {
                if (!await db.Artists.AnyAsync(a => a.Id == artistId, ct))
                    return TypedResults.NotFound();
                tracks = tracks.Where(t => t.Album.AlbumArtistId == artistId || t.Artists.Any(a => a.Id == artistId));
            }

            var take = Math.Clamp(limit ?? MaxShuffle, 1, MaxShuffle);
            // Erst nur IDs mischen, dann die gezogenen laden: die ganze Bibliothek samt Dateien bräuchte sonst viel Speicher.
            var ids = (await tracks.Select(t => t.Id).ToListAsync(ct)).OrderBy(_ => Random.Shared.Next()).Take(take).ToList();
            var loaded = await db.Tracks.AsNoTracking()
                .Where(t => ids.Contains(t.Id))
                .Include(t => t.Artists)
                .Include(t => t.Album).ThenInclude(a => a.AlbumArtist)
                .Include(t => t.Files)
                .AsSplitQuery()
                .ToDictionaryAsync(t => t.Id, ct);
            return TypedResults.Ok(ids.Where(loaded.ContainsKey).Select(id => Item(loaded[id])).ToList());
        });

        return api;
    }

    private static PlaylistItem Item(Track t)
    {
        var file = t.Files.OrderByDescending(f => f.Format == AudioFormat.Flac).ThenByDescending(f => f.Bitrate).First();
        var artist = t.Artists.Count > 0 ? string.Join(", ", t.Artists.Select(a => a.Name)) : t.Album.AlbumArtist.Name;
        return new PlaylistItem(file.Id, t.Id, t.Title, artist, t.Album.Title, t.AlbumId, t.DurationMs);
    }
}
