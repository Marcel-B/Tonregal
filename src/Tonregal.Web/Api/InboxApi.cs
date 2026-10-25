// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Import;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Inbox;

namespace Tonregal.Web.Api;

public sealed record ImportJobDto(
    int Id,
    string Path,
    string Group,
    ImportState State,
    AudioFormat Format,
    int DurationMs,
    string? TagTitle,
    string? TagArtist,
    string? TagAlbum,
    double? MatchScore,
    string? ReviewReason,
    string? Error,
    RecordingCandidate? Selected,
    int CandidateCount,
    DateTimeOffset StartedAt);

public sealed record InboxResponse(InboxStatus Status, IReadOnlyList<ImportJobDto> Jobs);

public sealed record ImportJobCandidates(int JobId, Guid? SelectedRecordingId, Guid? SelectedReleaseId, IReadOnlyList<RecordingCandidate> Candidates);

public static class InboxApi
{
    public static RouteGroupBuilder MapInboxApi(this RouteGroupBuilder api)
    {
        var inbox = api.MapGroup("/inbox");

        // Offene Jobs, nach Ordner und Dateiname sortiert.
        inbox.MapGet("/", async (TonregalDbContext db, InboxService service, CancellationToken ct) =>
        {
            var jobs = await db.ImportJobs.AsNoTracking()
                .Where(j => ImportJob.ActiveStates.Contains(j.State))
                .ToListAsync(ct);
            return new InboxResponse(service.Status, jobs
                .OrderBy(j => j.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(ToDto)
                .ToList());
        });

        inbox.MapPost("/scan", (InboxService service) =>
        {
            service.RequestRun();
            return TypedResults.Accepted("/api/inbox", service.Status);
        });

        inbox.MapGet("/{id:int}/candidates", async Task<IResult> (int id, TonregalDbContext db, CancellationToken ct) =>
            await db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) is { } job
                ? TypedResults.Ok(new ImportJobCandidates(job.Id, job.SelectedRecordingId, job.SelectedReleaseId, Candidates(job)))
                : TypedResults.NotFound());

        // Job von vorn: z. B. nachdem der AcoustID-Key eingetragen wurde.
        inbox.MapPost("/{id:int}/retry", async Task<IResult> (int id, TonregalDbContext db, InboxService service, CancellationToken ct) =>
        {
            var job = await db.ImportJobs.FindAsync([id], ct);
            if (job is null)
                return TypedResults.NotFound();
            if (!ImportJob.ActiveStates.Contains(job.State))
                return TypedResults.Problem("Der Job ist bereits abgeschlossen", statusCode: StatusCodes.Status409Conflict);
            job.Reset();
            await db.SaveChangesAsync(ct);
            service.RequestRun();
            return TypedResults.Ok(ToDto(job));
        });

        return api;
    }

    private static ImportJobDto ToDto(ImportJob job)
    {
        var candidates = Candidates(job);
        var selected = candidates.FirstOrDefault(c => c.RecordingId == job.SelectedRecordingId && c.ReleaseId == job.SelectedReleaseId);
        return new ImportJobDto(
            job.Id, job.SourcePath, job.Group, job.State, job.Format, job.DurationMs,
            job.TagTitle, job.TagArtist, job.TagAlbum, job.MatchScore, job.ReviewReason, job.Error,
            selected, candidates.Count, job.StartedAt);
    }

    private static IReadOnlyList<RecordingCandidate> Candidates(ImportJob job) =>
        job.Candidates is null ? [] : JsonSerializer.Deserialize<List<RecordingCandidate>>(job.Candidates, InboxImporter.Json) ?? [];
}
