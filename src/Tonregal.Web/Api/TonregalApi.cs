// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Web.Api;

public sealed record HealthResponse(string Status, bool LibraryMounted, string Version);

public sealed record LibraryStats(int Artists, int Albums, int Tracks, int Files, int OpenReviews, int OpenDuplicates);

public static class TonregalApi
{
    public static RouteGroupBuilder MapTonregalApi(this RouteGroupBuilder api)
    {
        api.MapGet("/health", (IOptions<TonregalOptions> options) =>
        {
            var version = typeof(TonregalApi).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            return new HealthResponse("ok", File.Exists(options.Value.MountMarker), version);
        });

        api.MapGet("/stats", async (TonregalDbContext db, CancellationToken ct) => new LibraryStats(
            await db.Artists.CountAsync(ct),
            await db.Albums.CountAsync(ct),
            await db.Tracks.CountAsync(ct),
            await db.AudioFiles.CountAsync(ct),
            await db.ImportJobs.CountAsync(j => j.State == Domain.Entities.ImportState.NeedsReview, ct),
            await db.DuplicateGroups.CountAsync(g => g.Decision == Domain.Entities.DuplicateDecision.Open, ct)));

        return api;
    }
}
