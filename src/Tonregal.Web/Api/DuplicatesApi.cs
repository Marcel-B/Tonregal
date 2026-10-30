// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Library;

namespace Tonregal.Web.Api;

public sealed record DuplicateFileDto(
    int Id, string Path, AudioFormat Format, int Bitrate, int SampleRate, long SizeBytes, int DurationMs,
    string Title, IReadOnlyList<string> Artists, int AlbumId, string Album, int? Year,
    bool HasMusicBrainzId, FileSource Source, DateTimeOffset AddedAt);

public sealed record DuplicateGroupDto(int Id, DuplicateKind Kind, int? SuggestedFileId, DateTimeOffset CreatedAt, IReadOnlyList<DuplicateFileDto> Files);

public sealed record ResolveDuplicateRequest(IReadOnlyList<int> KeepFileIds);

/// <summary>Die Gruppen, deren Vorschlag übernommen werden soll, so wie die Seite sie gezeigt hat.</summary>
public sealed record ResolveSuggestedRequest(IReadOnlyList<int> GroupIds);

public sealed record SkippedDuplicateGroup(int GroupId, string Reason);

public sealed record ResolveSuggestedResult(int Resolved, int Trashed, IReadOnlyList<SkippedDuplicateGroup> Skipped);

public static class DuplicatesApi
{
    public static RouteGroupBuilder MapDuplicatesApi(this RouteGroupBuilder api)
    {
        var duplicates = api.MapGroup("/duplicates");

        duplicates.MapGet("/", async (TonregalDbContext db, CancellationToken ct) =>
        {
            var groups = await db.DuplicateGroups
                .AsNoTracking()
                .Where(g => g.Decision == DuplicateDecision.Open)
                .OrderBy(g => g.Kind).ThenBy(g => g.Id)
                .Select(g => new
                {
                    g.Id, g.Kind, g.KeeperFileId, g.CreatedAt,
                    Files = g.Files.Select(f => new DuplicateFileDto(
                        f.Id, f.Path, f.Format, f.Bitrate, f.SampleRate, f.SizeBytes, f.Track.DurationMs,
                        f.Track.Title, f.Track.Artists.Select(a => a.Name).ToList(), f.Track.AlbumId, f.Track.Album.Title, f.Track.Album.Year,
                        f.Track.MusicBrainzRecordingId != null, f.Source, f.AddedAt)).ToList(),
                })
                .AsSplitQuery()
                .ToListAsync(ct);
            return groups.Select(g => new DuplicateGroupDto(
                    g.Id, g.Kind, g.KeeperFileId, g.CreatedAt, g.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();
        });

        // Neu suchen heißt: Library scannen, danach läuft die Dublettensuche.
        duplicates.MapPost("/scan", (LibraryScanService scans) =>
        {
            scans.RequestScan();
            return TypedResults.Accepted("/api/library/scan", scans.Status);
        });

        duplicates.MapPost("/{id:int}/resolve", async Task<IResult> (
            int id, ResolveDuplicateRequest request, IDuplicateService service, CancellationToken ct) =>
        {
            try
            {
                await service.ResolveAsync(id, request.KeepFileIds.Distinct().ToList(), ct);
                return TypedResults.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return TypedResults.NotFound();
            }
            catch (InvalidOperationException e)
            {
                return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (LibraryUnavailableException e)
            {
                return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // Alle Vorschläge auf einmal: Gruppe für Gruppe derselbe Weg wie oben, nur mit dem Vorschlag als
        // einziger Datei, die bleibt. Nur die genannten Gruppen, damit nichts entschieden wird, was die
        // Seite beim Bestätigen noch nicht gezeigt hat.
        duplicates.MapPost("/resolve-suggested", async Task<IResult> (
            ResolveSuggestedRequest request, TonregalDbContext db, IDuplicateService service, CancellationToken ct) =>
        {
            var resolved = 0;
            var trashed = 0;
            var skipped = new List<SkippedDuplicateGroup>();
            var ids = request.GroupIds.Distinct().ToList();
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                var group = await db.DuplicateGroups
                    .AsNoTracking()
                    .Where(g => g.Id == id && g.Decision == DuplicateDecision.Open)
                    .Select(g => new { g.KeeperFileId, Files = g.Files.Count })
                    .SingleOrDefaultAsync(ct);
                if (group is null)
                {
                    // Schon entschieden oder durch ein vorheriges Verschieben weggefallen.
                    skipped.Add(new(id, "Gruppe ist nicht mehr offen"));
                    continue;
                }
                if (group.KeeperFileId is not { } keeper)
                {
                    skipped.Add(new(id, "Gruppe hat keinen Vorschlag"));
                    continue;
                }

                try
                {
                    await service.ResolveAsync(id, [keeper], ct);
                    resolved++;
                    trashed += group.Files - 1;
                }
                catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
                {
                    skipped.Add(new(id, e.Message));
                }
                catch (LibraryUnavailableException e)
                {
                    if (resolved == 0)
                        return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
                    skipped.AddRange(ids.Skip(i).Select(rest => new SkippedDuplicateGroup(rest, e.Message)));
                    break;
                }
            }
            return TypedResults.Ok(new ResolveSuggestedResult(resolved, trashed, skipped));
        });

        return api;
    }
}
