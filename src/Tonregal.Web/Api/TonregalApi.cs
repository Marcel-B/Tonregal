// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Analysis;
using Tonregal.Web.Backup;
using Tonregal.Web.Inbox;
using Tonregal.Web.Library;

namespace Tonregal.Web.Api;

public sealed record HealthResponse(string Status, bool LibraryMounted, string Version);

public sealed record LibraryStats(
    int Artists, int Albums, int Tracks, int Files, int OpenReviews, int OpenDuplicates, long LibraryBytes, int AnalyzedFiles);

/// <summary>Was ein Neustart gerade abschneiden würde; <c>deploy/update-tonregal.sh</c> wartet, solange <see cref="Busy"/> gilt.</summary>
public sealed record BusyResponse(bool Busy, IReadOnlyList<string> Reasons);

public static class TonregalApi
{
    public static RouteGroupBuilder MapTonregalApi(this RouteGroupBuilder api)
    {
        api.MapGet("/health", (IOptions<TonregalOptions> options) =>
        {
            var version = typeof(TonregalApi).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            return new HealthResponse("ok", File.Exists(options.Value.MountMarker), version);
        });

        api.MapGet("/stats", async (TonregalDbContext db, ILibraryAnalyzer analysis, CancellationToken ct) => new LibraryStats(
            await db.Artists.CountAsync(ct),
            await db.Albums.CountAsync(ct),
            await db.Tracks.CountAsync(ct),
            await db.AudioFiles.CountAsync(ct),
            await db.ImportJobs.CountAsync(j => j.State == Domain.Entities.ImportState.NeedsReview, ct),
            await db.DuplicateGroups.CountAsync(g => g.Decision == Domain.Entities.DuplicateDecision.Open, ct),
            await db.AudioFiles.SumAsync(f => f.SizeBytes, ct),
            (await analysis.CountAsync(ct)).Analyzed));

        api.MapGet("/busy", (LibraryWriteLock writeLock, InboxService inbox, LibraryScanService scans, AnalysisService analysis, BackupService backup) =>
        {
            var reasons = new List<string>();
            if (writeLock.Holder is { } holder)
                reasons.Add(holder);
            if (inbox.Status.Running)
                reasons.Add("Inbox");
            if (scans.Status.Running)
                reasons.Add("Scan");
            if (analysis.Status.Running)
                reasons.Add("Analyse");
            if (backup.Status.Running)
                reasons.Add("Backup");
            var distinct = reasons.Distinct().ToList();
            return new BusyResponse(distinct.Count > 0, distinct);
        });

        return api;
    }
}
