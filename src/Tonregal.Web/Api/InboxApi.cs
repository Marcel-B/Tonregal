// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Import;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Inbox;
using Tonregal.Web.Library;

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

public sealed record SelectCandidateRequest(Guid RecordingId, Guid ReleaseId);

public sealed record ImportRequest(IReadOnlyList<int> JobIds);

public sealed record UploadResponse(string Path);

public sealed record RetryAllResponse(int Count);

/// <summary>Massenlauf über den Altbestand; je Datei zählt ihr jüngster Job.</summary>
public sealed record LegacySummary(
    string Path, bool Exists, int Total, int Pending, int Matched, int NeedsReview, int Imported, int AlreadyInLibrary, int Rejected);

public sealed record ImportJobCandidates(int JobId, Guid? SelectedRecordingId, Guid? SelectedReleaseId, IReadOnlyList<RecordingCandidate> Candidates);

public static class InboxApi
{
    public static RouteGroupBuilder MapInboxApi(this RouteGroupBuilder api)
    {
        var inbox = api.MapGroup("/inbox");

        // Offene Jobs, nach Ordner und Dateiname sortiert; dazu, was in den letzten Stunden als
        // byte-gleiche Kopie verworfen wurde, damit ein Upload nicht kommentarlos verschwindet.
        inbox.MapGet("/", async (TonregalDbContext db, InboxService service, TimeProvider time, CancellationToken ct, FileSource source = FileSource.Inbox) =>
        {
            var since = time.GetUtcNow().AddHours(-12);
            var jobs = await db.ImportJobs.AsNoTracking()
                .Where(j => j.Source == source)
                .Where(j => ImportJob.ActiveStates.Contains(j.State)
                    || (source == FileSource.Inbox && j.State == ImportState.Rejected && j.FinishedAt > since
                        && j.Error!.StartsWith(ImportJob.AlreadyInLibrary)))
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

        // Alle Problemfälle von vorn, z. B. nach einem Update der Erkennung.
        inbox.MapPost("/retry-all", async (TonregalDbContext db, InboxService service, CancellationToken ct, FileSource source = FileSource.Inbox) =>
        {
            var jobs = await db.ImportJobs
                .Where(j => j.Source == source && ImportJob.ActiveStates.Contains(j.State) && (j.State == ImportState.NeedsReview || j.Error != null))
                .ToListAsync(ct);
            foreach (var job in jobs)
                job.Reset();
            await db.SaveChangesAsync(ct);
            service.RequestRun();
            return new RetryAllResponse(jobs.Count);
        });

        // Problemfälle als Text, zum Kopieren und Weitergeben.
        inbox.MapGet("/report", async (TonregalDbContext db, TimeProvider time, CancellationToken ct, FileSource source = FileSource.Inbox) =>
        {
            var jobs = (await db.ImportJobs.AsNoTracking()
                    .Where(j => j.Source == source && ImportJob.ActiveStates.Contains(j.State))
                    .ToListAsync(ct))
                .Where(InboxReport.IsProblem)
                .OrderBy(j => j.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return TypedResults.Text(InboxReport.Format(jobs, Candidates, time.GetUtcNow()), "text/plain; charset=utf-8");
        });

        // Entscheidung im Review: dieser Kandidat ist es. Der Job gilt damit als erkannt.
        inbox.MapPost("/{id:int}/select", async Task<IResult> (int id, SelectCandidateRequest request, TonregalDbContext db, CancellationToken ct) =>
        {
            var job = await db.ImportJobs.FindAsync([id], ct);
            if (job is null)
                return TypedResults.NotFound();
            if (job.State is not (ImportState.Matched or ImportState.NeedsReview))
                return TypedResults.Problem("Nur erkannte oder zu prüfende Jobs lassen sich ändern", statusCode: StatusCodes.Status409Conflict);
            if (!Candidates(job).Any(c => c.RecordingId == request.RecordingId && c.ReleaseId == request.ReleaseId))
                return TypedResults.Problem("Diesen Kandidaten gibt es für den Job nicht", statusCode: StatusCodes.Status400BadRequest);

            job.SelectedRecordingId = request.RecordingId;
            job.SelectedReleaseId = request.ReleaseId;
            job.State = ImportState.Matched;
            job.ReviewReason = null;
            job.Error = null;
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(ToDto(job));
        });

        // Kein Treffer passt: mit Titel und Interpret aus Tags oder Dateiname ins Sammelalbum.
        inbox.MapPost("/{id:int}/import-unmatched", async Task<IResult> (
            int id, TonregalDbContext db, ILibraryImporter importer, LibraryScanService scans, LibraryWriteLock writeLock, CancellationToken ct) =>
        {
            if (await db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) is not { } existing)
                return TypedResults.NotFound();
            if (writeLock.Holder == "Backup")
                return TypedResults.Problem("Das Backup läuft gerade, bitte danach noch einmal", statusCode: StatusCodes.Status409Conflict);
            if (existing.State != ImportState.NeedsReview)
                return TypedResults.Problem("Nur zu prüfende Dateien lassen sich ohne Treffer übernehmen", statusCode: StatusCodes.Status409Conflict);
            var job = await importer.ImportUnmatchedAsync(id, ct);
            if (job.State == ImportState.Imported)
                scans.RequestScan();
            return job.Error is { } error
                ? TypedResults.Problem(error, statusCode: StatusCodes.Status422UnprocessableEntity)
                : TypedResults.Ok(ToDto(job));
        });

        // Altbestand: Stand des Massenlaufs und Start.
        inbox.MapGet("/legacy", async (TonregalDbContext db, IOptions<TonregalOptions> options, CancellationToken ct) =>
        {
            var states = (await db.ImportJobs.AsNoTracking()
                    .Where(j => j.Source == FileSource.Legacy)
                    .Select(j => new { j.Id, j.SourcePath, j.State, j.Error })
                    .ToListAsync(ct))
                .GroupBy(j => j.SourcePath, StringComparer.Ordinal)
                .Select(g => g.MaxBy(j => j.Id)!)
                .ToList();
            var root = options.Value.Legacy;
            return new LegacySummary(
                root, Directory.Exists(root), states.Count,
                states.Count(s => s.State is ImportState.New or ImportState.Fingerprinted),
                states.Count(s => s.State == ImportState.Matched),
                states.Count(s => s.State == ImportState.NeedsReview),
                states.Count(s => s.State == ImportState.Imported),
                states.Count(s => s.State == ImportState.Rejected && s.Error != null && s.Error.StartsWith(ImportJob.AlreadyInLibrary)),
                states.Count(s => s.State == ImportState.Rejected && (s.Error == null || !s.Error.StartsWith(ImportJob.AlreadyInLibrary))));
        });

        inbox.MapPost("/legacy/scan", (InboxService service, IOptions<TonregalOptions> options) =>
        {
            if (!Directory.Exists(options.Value.Legacy))
                return Results.Problem($"Den Altbestand-Ordner {options.Value.Legacy} gibt es nicht", statusCode: StatusCodes.Status404NotFound);
            service.RequestLegacyRun();
            return Results.Accepted("/api/inbox/legacy", service.Status);
        });

        // Datei bleibt in der Inbox, bekommt aber keinen neuen Job, solange sie sich nicht ändert.
        inbox.MapPost("/{id:int}/reject", async Task<IResult> (int id, TonregalDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var job = await db.ImportJobs.FindAsync([id], ct);
            if (job is null)
                return TypedResults.NotFound();
            if (!ImportJob.ActiveStates.Contains(job.State))
                return TypedResults.Problem("Der Job ist bereits abgeschlossen", statusCode: StatusCodes.Status409Conflict);
            job.State = ImportState.Rejected;
            job.Error = ImportJob.RejectedByUser;
            job.FinishedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        });

        // Übernahme in die Library läuft im Hintergrund; Fortschritt kommt per inboxStatus.
        inbox.MapPost("/import", async Task<IResult> (ImportRequest request, TonregalDbContext db, InboxService service, CancellationToken ct) =>
        {
            var ids = request.JobIds.Distinct().ToList();
            var matched = await db.ImportJobs
                .Where(j => ids.Contains(j.Id) && j.State == ImportState.Matched && j.SelectedReleaseId != null)
                .Select(j => j.Id)
                .ToListAsync(ct);
            if (matched.Count == 0)
                return TypedResults.Problem("Keiner der Jobs ist zum Übernehmen bereit", statusCode: StatusCodes.Status409Conflict);
            return TypedResults.Accepted("/api/inbox", service.EnqueueImports(matched));
        });

        // Drag & Drop: eine Datei pro Anfrage, path ist der relative Pfad aus dem Browser („Album/01.mp3“).
        inbox.MapPost("/upload", async Task<IResult> (
                IFormFile file, [FromForm] string path, IOptions<TonregalOptions> options, InboxService service, CancellationToken ct) =>
            {
                if (!File.Exists(options.Value.MountMarker))
                    return TypedResults.Problem("Die SSD ist nicht gemountet", statusCode: StatusCodes.Status503ServiceUnavailable);
                try
                {
                    await using var content = file.OpenReadStream();
                    var saved = await InboxUpload.SaveAsync(options.Value.Inbox, path, content, ct);
                    service.RequestRun();
                    return TypedResults.Created("/api/inbox", new UploadResponse(saved));
                }
                catch (ArgumentException e)
                {
                    return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
                }
            })
            .DisableAntiforgery()
            .WithMetadata(new DisableRequestSizeLimitAttribute())
            .WithFormOptions(multipartBodyLengthLimit: 4L << 30);

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
