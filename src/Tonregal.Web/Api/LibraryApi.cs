// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Library;

namespace Tonregal.Web.Api;

public sealed record ArtistSummary(int Id, string Name, string SortName, int AlbumCount);

public sealed record AlbumSummary(int Id, string Title, int? Year, AlbumKind Kind, int TrackCount);

public sealed record ArtistDetail(int Id, string Name, IReadOnlyList<AlbumSummary> Albums);

public sealed record ArtistRef(int Id, string Name);

public sealed record FileDto(int Id, AudioFormat Format, int Bitrate, int SampleRate, long SizeBytes, string Path);

public sealed record TrackDto(
    int Id, int Disc, int Number, string Title, IReadOnlyList<string> Artists, int DurationMs,
    double? Bpm, string? InitialKey, string? Genre, IReadOnlyList<FileDto> Files);

public sealed record AlbumDetail(
    int Id, string Title, int? Year, AlbumKind Kind, int DiscCount, ArtistRef AlbumArtist, IReadOnlyList<TrackDto> Tracks);

public static class LibraryApi
{
    public static RouteGroupBuilder MapLibraryApi(this RouteGroupBuilder api)
    {
        var library = api.MapGroup("/library");

        library.MapGet("/artists", async (TonregalDbContext db, CancellationToken ct) =>
            await db.Artists
                .Where(a => a.Albums.Any())
                .OrderBy(a => a.SortName)
                .Select(a => new ArtistSummary(a.Id, a.Name, a.SortName, a.Albums.Count))
                .ToListAsync(ct));

        library.MapGet("/artists/{id:int}", async Task<IResult> (int id, TonregalDbContext db, CancellationToken ct) =>
        {
            var artist = await db.Artists
                .Where(a => a.Id == id)
                .Select(a => new ArtistDetail(a.Id, a.Name, a.Albums
                    .OrderBy(al => al.Year).ThenBy(al => al.Title)
                    .Select(al => new AlbumSummary(al.Id, al.Title, al.Year, al.Kind, al.Tracks.Count))
                    .ToList()))
                .SingleOrDefaultAsync(ct);
            return artist is null ? TypedResults.NotFound() : TypedResults.Ok(artist);
        });

        library.MapGet("/albums/{id:int}", async Task<IResult> (int id, TonregalDbContext db, CancellationToken ct) =>
        {
            var album = await db.Albums
                .Include(a => a.AlbumArtist)
                .Include(a => a.Tracks).ThenInclude(t => t.Artists)
                .Include(a => a.Tracks).ThenInclude(t => t.Files)
                .AsSplitQuery()
                .SingleOrDefaultAsync(a => a.Id == id, ct);
            if (album is null)
                return TypedResults.NotFound();

            var tracks = album.Tracks
                .OrderBy(t => t.Disc).ThenBy(t => t.Number).ThenBy(t => t.Title)
                .Select(t => new TrackDto(
                    t.Id, t.Disc, t.Number, t.Title, t.Artists.Select(a => a.Name).ToList(), t.DurationMs,
                    t.Bpm, t.InitialKey, t.Genre,
                    t.Files.OrderByDescending(f => f.Format == AudioFormat.Flac).ThenByDescending(f => f.Bitrate)
                        .Select(f => new FileDto(f.Id, f.Format, f.Bitrate, f.SampleRate, f.SizeBytes, f.Path)).ToList()))
                .ToList();
            return TypedResults.Ok(new AlbumDetail(
                album.Id, album.Title, album.Year, album.Kind, album.DiscCount,
                new ArtistRef(album.AlbumArtist.Id, album.AlbumArtist.Name), tracks));
        });

        library.MapGet("/albums/{id:int}/cover", async Task<IResult> (
            int id, TonregalDbContext db, ITagReader tagReader, IOptions<TonregalOptions> options, CancellationToken ct) =>
        {
            var album = await db.Albums
                .Where(a => a.Id == id)
                .Select(a => new { a.CoverPath, FirstFile = a.Tracks.SelectMany(t => t.Files).Select(f => f.Path).FirstOrDefault() })
                .SingleOrDefaultAsync(ct);
            if (album is null)
                return TypedResults.NotFound();

            var root = Path.GetFullPath(options.Value.Library);
            if (ResolveInside(root, album.CoverPath) is { } coverFile && File.Exists(coverFile))
                return TypedResults.PhysicalFile(coverFile, coverFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");

            if (ResolveInside(root, album.FirstFile) is { } audioFile && File.Exists(audioFile))
            {
                try
                {
                    if (tagReader.ReadCover(audioFile) is { } picture)
                        return TypedResults.File(picture.Data, picture.MimeType);
                }
                catch (InvalidDataException)
                {
                }
            }

            return TypedResults.NotFound();
        });

        library.MapGet("/scan", (LibraryScanService scans) => scans.Status);

        library.MapPost("/scan", (LibraryScanService scans) =>
        {
            scans.RequestScan();
            return TypedResults.Accepted("/api/library/scan", scans.Status);
        });

        return api;
    }

    /// <summary>Löst einen Library-relativen Pfad auf und verhindert, dass er aus der Library herausführt.</summary>
    private static string? ResolveInside(string root, string? relative)
    {
        if (string.IsNullOrEmpty(relative))
            return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }
}
