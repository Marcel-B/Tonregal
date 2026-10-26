// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Web.Api;

/// <summary>
/// Audio für den Player. Der Stream kommt von Navidrome (so greift dort eingestelltes Transcoding und
/// die Play-Counts landen am selben Ort); Tonregal reicht ihn durch, damit der Browser nur einen Host
/// kennt und keine Zugangsdaten sieht. Kennt Navidrome die Datei noch nicht (Scan alle 15 Minuten) oder
/// ist es nicht eingerichtet, spielt Tonregal die Datei direkt.
/// </summary>
public static class PlayerApi
{
    public const string SourceHeader = "X-Tonregal-Source";

    private static readonly string[] ForwardedHeaders = ["Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Last-Modified", "ETag"];

    public static RouteGroupBuilder MapPlayerApi(this RouteGroupBuilder api)
    {
        var player = api.MapGroup("/player");

        player.MapGet("/files/{id:int}/stream", async Task<IResult> (
            int id, HttpContext context, TonregalDbContext db, INavidromeClient navidrome, IOptions<TonregalOptions> options, CancellationToken ct) =>
        {
            var path = await db.AudioFiles.Where(f => f.Id == id).Select(f => f.Path).SingleOrDefaultAsync(ct);
            if (path is null)
                return TypedResults.NotFound();

            if (await navidrome.FindSongIdAsync(path, ct) is { } songId
                && await navidrome.StreamAsync(songId, Range(context.Request), ct) is { } response)
                return new ProxyResult(response);

            if (LibraryApi.ResolveInside(Path.GetFullPath(options.Value.Library), path) is not { } file || !File.Exists(file))
                return TypedResults.NotFound();
            context.Response.Headers[SourceHeader] = "file";
            return TypedResults.PhysicalFile(file, LibraryApi.AudioContentType(file), enableRangeProcessing: true);
        });

        // Ohne Navidrome (oder solange es die Datei noch nicht gescannt hat) gibt es keine Play-Counts: 202 statt Fehler.
        player.MapPost("/files/{id:int}/scrobble", async Task<IResult> (
            int id, bool? submission, TonregalDbContext db, INavidromeClient navidrome, CancellationToken ct) =>
        {
            var path = await db.AudioFiles.Where(f => f.Id == id).Select(f => f.Path).SingleOrDefaultAsync(ct);
            if (path is null)
                return TypedResults.NotFound();
            if (await navidrome.FindSongIdAsync(path, ct) is not { } songId)
                return TypedResults.Accepted((string?)null);
            return await navidrome.ScrobbleAsync(songId, submission ?? true, ct)
                ? TypedResults.NoContent()
                : TypedResults.Problem("Navidrome hat den Scrobble nicht angenommen", statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        return api;
    }

    private static RangeHeaderValue? Range(HttpRequest request) =>
        RangeHeaderValue.TryParse(request.Headers.Range.ToString(), out var range) ? range : null;

    /// <summary>Reicht Status, die für Range-Anfragen nötigen Header und den Body unverändert durch.</summary>
    private sealed class ProxyResult(HttpResponseMessage response) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            using var _ = response;
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.Headers[SourceHeader] = "navidrome";
            foreach (var name in ForwardedHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                    context.Response.Headers[name] = values.ToArray();
            }

            try
            {
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Der Browser bricht beim Springen oder Weiterschalten laufende Anfragen ab.
            }
        }
    }
}
