// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Infrastructure.Ratings;

/// <summary>
/// Tonregal ist für Bewertungen maßgeblich. Navidrome bekommt sie nur noch nachgereicht (seine Smart Playlists werten
/// sie aus) und gibt seine eigenen nur für Stücke her, die Tonregal nie bewertet hat: so kommen die alten Bewertungen
/// herüber, ohne später eine in Tonregal gesetzte zu überschreiben.
/// </summary>
public sealed class TrackRatings(
    TonregalDbContext db,
    INavidromeClient navidrome,
    IOptions<NavidromeOptions> options,
    TimeProvider time,
    ILogger<TrackRatings> logger) : ITrackRatings
{
    /// <summary>So viele offene Bewertungen schickt ein Abgleich höchstens; der Rest kommt beim nächsten.</summary>
    private const int PushBatch = 500;

    public async Task<IReadOnlyDictionary<int, int>> ForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        var ids = fileIds.Distinct().ToList();
        var rated = await db.AudioFiles.AsNoTracking()
            .Where(f => ids.Contains(f.Id) && f.Track.Rating != null)
            .Select(f => new { f.Id, Rating = f.Track.Rating!.Value })
            .ToListAsync(ct);
        return rated.ToDictionary(r => r.Id, r => r.Rating);
    }

    public async Task<bool> SetAsync(int fileId, int rating, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rating, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rating, 5);
        var track = await db.Tracks.Include(t => t.Files).SingleOrDefaultAsync(t => t.Files.Any(f => f.Id == fileId), ct);
        if (track is null)
            return false;

        track.Rating = rating == 0 ? null : rating;
        track.RatedAt = time.GetUtcNow();
        // Auch ohne Navidrome vormerken: wird es später eingerichtet, bekommt es alle Bewertungen.
        track.RatingPending = true;
        await db.SaveChangesAsync(ct);

        // Gleich weiterreichen, damit Navidromes Smart Playlists sie sofort sehen; was nicht klappt, holt der Abgleich nach.
        if (options.Value.Configured && await PushAsync(track, ct))
        {
            track.RatingPending = false;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<RatingSyncResult> SyncAsync(CancellationToken ct)
    {
        if (!options.Value.Configured)
            return new RatingSyncResult(0, 0, 0);

        var imported = await ImportAsync(ct);

        var pending = await db.Tracks.Where(t => t.RatingPending).Include(t => t.Files).Take(PushBatch).ToListAsync(ct);
        var pushed = 0;
        foreach (var track in pending)
        {
            if (!await PushAsync(track, ct))
                continue;
            track.RatingPending = false;
            pushed++;
        }

        await db.SaveChangesAsync(ct);
        var left = await db.Tracks.CountAsync(t => t.RatingPending, ct);
        if (imported > 0 || pushed > 0)
            logger.LogInformation("Bewertungen abgeglichen: {Imported} aus Navidrome übernommen, {Pushed} an Navidrome, {Pending} offen", imported, pushed, left);
        return new RatingSyncResult(imported, pushed, left);
    }

    /// <summary>Navidromes Bewertungen für Stücke ohne Bewertung in Tonregal; bei mehreren Dateien zählt die höchste.</summary>
    private async Task<int> ImportAsync(CancellationToken ct)
    {
        var files = await db.AudioFiles.AsNoTracking()
            .Where(f => f.Track.RatedAt == null)
            .Select(f => new { f.TrackId, f.Path })
            .ToListAsync(ct);
        if (files.Count == 0)
            return 0;
        var ratings = await navidrome.RatingsAsync([.. files.Select(f => f.Path)], ct);
        var byTrack = files.Where(f => ratings.ContainsKey(f.Path))
            .GroupBy(f => f.TrackId)
            .ToDictionary(g => g.Key, g => g.Max(f => ratings[f.Path]));
        if (byTrack.Count == 0)
            return 0;

        var now = time.GetUtcNow();
        var ids = byTrack.Keys.ToList();
        // Nur Stücke, die zwischendurch niemand in Tonregal bewertet hat.
        var tracks = await db.Tracks.Where(t => ids.Contains(t.Id) && t.RatedAt == null).ToListAsync(ct);
        foreach (var track in tracks)
        {
            track.Rating = byTrack[track.Id];
            track.RatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return tracks.Count;
    }

    /// <summary>An jede Datei des Stücks, weil Navidrome MP3 und FLAC als zwei Songs führt.</summary>
    private async Task<bool> PushAsync(Track track, CancellationToken ct)
    {
        var all = true;
        foreach (var file in track.Files)
        {
            if (await navidrome.FindSongIdAsync(file.Path, ct) is not { } songId)
            {
                all = false;
                continue;
            }

            all &= await navidrome.SetRatingAsync(songId, track.Rating ?? 0, ct);
        }

        return all && track.Files.Count > 0;
    }
}
