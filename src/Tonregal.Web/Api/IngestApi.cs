// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Web.Inbox;

namespace Tonregal.Web.Api;

/// <summary>Stand eines Ingest-Jobs; <c>Imported</c> mit <see cref="LibraryPath"/> heißt: liegt in der Library.</summary>
public sealed record IngestJobDto(
    int Id, string ExternalId, ImportState State, string? LibraryPath, string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt)
{
    public static IngestJobDto From(ImportJob job) => new(
        job.Id, job.ExternalId?.Split(':', 2)[^1] ?? "", job.State, job.LibraryPath, job.Error, job.StartedAt, job.FinishedAt);
}

/// <summary>
/// Ingest-API für eigene Stücke, z. B. aus Tonwerk-AI. Multipart mit <c>file</c> (Audio),
/// <c>metadata</c> (JSON, siehe <see cref="IngestMetadata"/>) und optional <c>cover</c> (JPEG/PNG);
/// Anmeldung per <c>X-Api-Key</c>, der Key bestimmt die Quelle.
/// </summary>
public static class IngestApi
{
    public const string KeyHeader = "X-Api-Key";
    public const string StatusEvent = "ingestStatus";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapIngestApi(this RouteGroupBuilder api)
    {
        var ingest = api.MapGroup("/ingest");

        ingest.MapPost("/", async Task<IResult> (
                HttpRequest request, IFormFile file, [FromForm] string metadata, IFormFile? cover,
                IIngestKeys keys, IIngestService service, InboxService inbox, CancellationToken ct) =>
            {
                if (await AuthenticateAsync(request, keys, ct) is not { } source)
                    return await UnauthorizedAsync(keys, ct);

                IngestMetadata? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<IngestMetadata>(metadata, Json);
                }
                catch (JsonException e)
                {
                    return TypedResults.Problem($"metadata ist kein gültiges JSON: {e.Message}", statusCode: StatusCodes.Status400BadRequest);
                }

                if (parsed is null)
                    return TypedResults.Problem("metadata fehlt", statusCode: StatusCodes.Status400BadRequest);

                IngestResult result;
                try
                {
                    await using var audio = file.OpenReadStream();
                    await using var image = cover?.OpenReadStream();
                    result = await service.IngestAsync(source, parsed, file.FileName, audio, image, ct);
                }
                catch (ArgumentException e)
                {
                    return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
                }
                catch (LibraryUnavailableException e)
                {
                    return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var job = result.Job;
                // Wiederholter Aufruf nach gescheiterter Übernahme: erneut versuchen.
                if (job.State == ImportState.Matched && (result.Created || job.Error is not null))
                    inbox.EnqueueImports([job.Id]);
                var dto = IngestJobDto.From(job);
                return result.Created
                    ? TypedResults.Accepted($"/api/ingest/{job.Id}", dto)
                    : TypedResults.Ok(dto);
            })
            .DisableAntiforgery()
            .WithMetadata(new DisableRequestSizeLimitAttribute())
            .WithFormOptions(multipartBodyLengthLimit: 4L << 30);

        ingest.MapGet("/{id:int}", async Task<IResult> (
            int id, HttpRequest request, IIngestKeys keys, IIngestService service, CancellationToken ct) =>
        {
            if (await AuthenticateAsync(request, keys, ct) is not { } source)
                return await UnauthorizedAsync(keys, ct);
            return await service.FindAsync(source, id, ct) is { } job
                ? TypedResults.Ok(IngestJobDto.From(job))
                : TypedResults.NotFound();
        });

        return api;
    }

    private static Task<string?> AuthenticateAsync(HttpRequest request, IIngestKeys keys, CancellationToken ct) =>
        keys.AuthenticateAsync(request.Headers[KeyHeader].ToString(), ct);

    private static async Task<IResult> UnauthorizedAsync(IIngestKeys keys, CancellationToken ct) => await keys.AnyAsync(ct)
        ? TypedResults.Problem($"{KeyHeader} fehlt oder ist unbekannt", statusCode: StatusCodes.Status401Unauthorized)
        : TypedResults.Problem("Ingest-API nicht eingerichtet: Key auf der Seite „Verbindungen“ erzeugen", statusCode: StatusCodes.Status503ServiceUnavailable);
}
