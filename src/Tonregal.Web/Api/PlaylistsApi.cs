// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Application.Abstractions;

namespace Tonregal.Web.Api;

/// <summary>Eigene Playlists: anlegen, Stücke hinzufügen, sortieren, entfernen; abgespielt wird im Player der Seite oder der App.</summary>
public static class PlaylistsApi
{
    public sealed record TrackList(IReadOnlyList<int>? TrackIds);

    public static RouteGroupBuilder MapPlaylistsApi(this RouteGroupBuilder api)
    {
        var playlists = api.MapGroup("/playlists");

        playlists.MapGet("/", (IPlaylists service, CancellationToken ct) => service.ListAsync(ct));

        playlists.MapGet("/{id:int}", async Task<IResult> (int id, IPlaylists service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } detail ? TypedResults.Ok(detail) : TypedResults.NotFound());

        playlists.MapPost("/", (PlaylistInput input, IPlaylists service, CancellationToken ct) =>
            Guarded(async () =>
            {
                var created = await service.CreateAsync(input, ct);
                return TypedResults.Created($"/api/playlists/{created.Id}", created);
            }));

        playlists.MapPut("/{id:int}", (int id, PlaylistInput input, IPlaylists service, CancellationToken ct) =>
            Guarded(async () => await service.UpdateAsync(id, input, ct) is { } updated ? TypedResults.Ok(updated) : TypedResults.NotFound()));

        playlists.MapDelete("/{id:int}", async Task<IResult> (int id, IPlaylists service, CancellationToken ct) =>
            await service.DeleteAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound());

        playlists.MapPost("/{id:int}/tracks", (int id, TrackList body, IPlaylists service, CancellationToken ct) =>
            Guarded(async () => await service.AddAsync(id, body.TrackIds ?? [], ct) is { } result ? TypedResults.Ok(result) : TypedResults.NotFound()));

        playlists.MapPut("/{id:int}/tracks", (int id, TrackList body, IPlaylists service, CancellationToken ct) =>
            Guarded(async () => await service.SetTracksAsync(id, body.TrackIds ?? [], ct) is { } detail ? TypedResults.Ok(detail) : TypedResults.NotFound()));

        playlists.MapDelete("/{id:int}/tracks/{trackId:int}", async Task<IResult> (int id, int trackId, IPlaylists service, CancellationToken ct) =>
            await service.RemoveAsync(id, trackId, ct) ? TypedResults.NoContent() : TypedResults.NotFound());

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
            var message = e.ParamName is null ? e.Message : e.Message.Replace($" (Parameter '{e.ParamName}')", "", StringComparison.Ordinal);
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [e.ParamName ?? "input"] = [message] });
        }
        catch (InvalidOperationException e)
        {
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
