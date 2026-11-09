// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Web.Api;

public sealed record RatingInput(int Rating);

/// <summary>
/// Bewertungen und Zufallswiedergabe für die Player. Bewertungen liegen in Tonregals Datenbank (<see cref="ITrackRatings"/>);
/// Navidrome bekommt eine Kopie für seine Smart Playlists.
/// </summary>
public static class ListeningApi
{
    /// <summary>Mehr braucht keine Warteschlange; die ganze Bibliothek kommt so trotzdem in zufälliger Auswahl.</summary>
    public const int MaxShuffle = 2000;

    public static RouteGroupBuilder MapListeningApi(this RouteGroupBuilder api)
    {
        var player = api.MapGroup("/player");

        // Bewertung je Datei-ID ({"21": 4}); unbewertete fehlen.
        player.MapGet("/ratings", async (int[]? files, ITrackRatings ratings, CancellationToken ct) =>
        {
            var found = await ratings.ForFilesAsync([.. (files ?? []).Distinct().Take(1000)], ct);
            return found.ToDictionary(r => r.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), r => r.Value);
        });

        player.MapPut("/files/{id:int}/rating", async Task<IResult> (int id, RatingInput input, ITrackRatings ratings, CancellationToken ct) =>
        {
            if (input.Rating is < 0 or > 5)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["rating"] = ["0 bis 5 Sterne"] });
            return await ratings.SetAsync(id, input.Rating, ct) ? TypedResults.NoContent() : TypedResults.NotFound();
        });

        // Gleicht sofort mit Navidrome ab, statt auf den nächsten Lauf zu warten.
        player.MapPost("/ratings/sync", async (ITrackRatings ratings, CancellationToken ct) => TypedResults.Ok(await ratings.SyncAsync(ct)));

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

    internal static PlaylistItem Item(Track t)
    {
        var file = t.Files.OrderByDescending(f => f.Format == AudioFormat.Flac).ThenByDescending(f => f.Bitrate).First();
        var artist = t.Artists.Count > 0 ? string.Join(", ", t.Artists.Select(a => a.Name)) : t.Album.AlbumArtist.Name;
        return new PlaylistItem(file.Id, t.Id, t.Title, artist, t.Album.Title, t.AlbumId, t.DurationMs);
    }
}
