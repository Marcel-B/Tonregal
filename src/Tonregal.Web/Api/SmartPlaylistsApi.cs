// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Application.Abstractions;

namespace Tonregal.Web.Api;

/// <summary>Seite „Smart Playlists“: Regeln pflegen; Tonregal schreibt die .nsp-Dateien, Navidrome füllt die Listen.</summary>
public static class SmartPlaylistsApi
{
    public static RouteGroupBuilder MapSmartPlaylistsApi(this RouteGroupBuilder api)
    {
        var playlists = api.MapGroup("/smart-playlists");

        playlists.MapGet("/", (ISmartPlaylists service, CancellationToken ct) => service.ListAsync(ct));
        playlists.MapGet("/keys", (ISmartPlaylists service, CancellationToken ct) => service.KeysAsync(ct));

        playlists.MapGet("/{id:int}/tracks", async Task<IResult> (int id, ISmartPlaylists service, CancellationToken ct) =>
            await service.TracksAsync(id, ct) is { } tracks ? TypedResults.Ok(tracks) : TypedResults.NotFound());

        playlists.MapPost("/", (SmartPlaylistInput input, ISmartPlaylists service, CancellationToken ct) =>
            Guarded(async () =>
            {
                var created = await service.CreateAsync(input, ct);
                return TypedResults.Created($"/api/smart-playlists/{created.Id}", created);
            }));

        playlists.MapPut("/{id:int}", (int id, SmartPlaylistInput input, ISmartPlaylists service, CancellationToken ct) =>
            Guarded(async () => await service.UpdateAsync(id, input, ct) is { } updated
                ? TypedResults.Ok(updated)
                : TypedResults.NotFound()));

        playlists.MapDelete("/{id:int}", async Task<IResult> (int id, ISmartPlaylists service, CancellationToken ct) =>
            await service.DeleteAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound());

        return api;
    }

    private static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ArgumentException e)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [e.ParamName ?? "input"] = [Message(e)] });
        }
        catch (InvalidOperationException e)
        {
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>Ohne den angehängten „(Parameter '…')“-Teil.</summary>
    private static string Message(ArgumentException e) =>
        e.ParamName is null ? e.Message : e.Message.Replace($" (Parameter '{e.ParamName}')", "", StringComparison.Ordinal);
}
