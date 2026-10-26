// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Backup;

namespace Tonregal.Web.Api;

public sealed record BackupResponse(BackupStatusDto Status, IReadOnlyList<BackupRunDto> Runs);

public static class BackupApi
{
    public static RouteGroupBuilder MapBackupApi(this RouteGroupBuilder api)
    {
        var backup = api.MapGroup("/backup");

        backup.MapGet("/", async (BackupService service, TonregalDbContext db, CancellationToken ct) =>
            new BackupResponse(service.Status, await BackupService.RecentAsync(db, 14, ct)));
        // Auch von tonregal-backup-now in deploy/shell/tonregal.zsh benutzt.
        backup.MapPost("/run", (BackupService service) => TypedResults.Accepted("/api/backup", service.RequestRun()));

        return api;
    }
}
