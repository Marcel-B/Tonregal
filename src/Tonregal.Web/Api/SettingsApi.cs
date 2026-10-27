// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Application.Abstractions;

namespace Tonregal.Web.Api;

/// <summary>
/// Seite „Verbindungen“: API-Keys der Ingest-API anzeigen und neu erzeugen. Tonregal hat keine Anmeldung, wer die
/// Oberfläche erreicht (nur übers Tailnet), sieht also auch die Keys.
/// </summary>
public static class SettingsApi
{
    public static RouteGroupBuilder MapSettingsApi(this RouteGroupBuilder api)
    {
        var ingest = api.MapGroup("/settings/ingest");

        ingest.MapGet("/", (IIngestKeys keys, CancellationToken ct) => keys.ListAsync(ct));

        ingest.MapPost("/{source}/key", async Task<IResult> (string source, IIngestKeys keys, CancellationToken ct) =>
        {
            try
            {
                return TypedResults.Ok(await keys.RegenerateAsync(source, ct));
            }
            catch (ArgumentException e)
            {
                return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException e)
            {
                return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        return api;
    }
}
