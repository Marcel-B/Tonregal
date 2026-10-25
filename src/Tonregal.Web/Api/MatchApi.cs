// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Web.Api;

public sealed record ToolStatus(string? Fpcalc, bool AcoustIdConfigured);

public sealed record MatchResponse(int FileId, double DurationSeconds, IReadOnlyList<RecordingCandidate> Candidates);

public static class MatchApi
{
    public static RouteGroupBuilder MapMatchApi(this RouteGroupBuilder api)
    {
        api.MapGet("/tools", async (IFingerprintService fingerprints, IOptions<MetadataOptions> options, CancellationToken ct) =>
            new ToolStatus(await fingerprints.GetVersionAsync(ct), !string.IsNullOrWhiteSpace(options.Value.AcoustIdApiKey)));

        // Abgleich einer Library-Datei gegen AcoustID/MusicBrainz, ohne etwas zu ändern.
        api.MapGet("/library/files/{id:int}/candidates", async Task<IResult> (
            int id, TonregalDbContext db, IFingerprintService fingerprints, IMetadataLookup lookup,
            IOptions<TonregalOptions> options, CancellationToken ct) =>
        {
            var file = await db.AudioFiles.FindAsync([id], ct);
            if (file is null)
                return TypedResults.NotFound();

            var root = Path.GetFullPath(options.Value.Library);
            var path = LibraryApi.ResolveInside(root, file.Path);
            if (path is null || !File.Exists(path))
                return TypedResults.Problem($"{file.Path} fehlt in der Library", statusCode: StatusCodes.Status404NotFound);

            try
            {
                var fingerprint = await fingerprints.ComputeAsync(path, ct);
                var candidates = await lookup.LookupAsync(fingerprint, ct);
                return TypedResults.Ok(new MatchResponse(id, fingerprint.Duration.TotalSeconds, candidates));
            }
            catch (ExternalToolUnavailableException e)
            {
                return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (InvalidDataException e)
            {
                return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        return api;
    }
}
