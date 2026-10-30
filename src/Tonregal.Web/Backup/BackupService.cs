// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Monitoring;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Hubs;

namespace Tonregal.Web.Backup;

public sealed record BackupRunDto(
    int Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, BackupStatus Status, int FilesSynced, long BytesSynced, string? Log)
{
    public static BackupRunDto From(BackupRun r) => new(r.Id, r.StartedAt, r.FinishedAt, r.Status, r.FilesSynced, r.BytesSynced, r.Log);
}

/// <param name="ConsecutiveFailures">Fehlschläge seit dem letzten erfolgreichen Lauf; ab zwei zeigt die UI rot.</param>
public sealed record BackupStatusDto(
    bool Running, string? Step, BackupRunDto? LastRun, DateTimeOffset? LastSuccessAt, int ConsecutiveFailures, DateTimeOffset? NextRunAt);

/// <summary>
/// Backup-Scheduler: jede Nacht zur <see cref="BackupOptions.Time"/> und auf Anfrage ein Lauf über den
/// <see cref="IBackupRunner"/>. Fortschritt geht als <c>backupStatus</c> an die SPA.
/// </summary>
public sealed class BackupService(
    IServiceScopeFactory scopes,
    IHubContext<StatusHub> hub,
    IOptions<BackupOptions> options,
    TimeProvider time,
    ILogger<BackupService> logger) : BackgroundService
{
    public const string StatusEvent = "backupStatus";

    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile BackupStatusDto _status = new(false, null, null, null, 0, null);
    private CancellationTokenSource? _current;

    public BackupStatusDto Status => _status with { NextRunAt = NextRun(time.GetLocalNow()) };

    /// <summary>Startet einen Lauf; läuft schon einer, folgt höchstens ein weiterer.</summary>
    public BackupStatusDto RequestRun()
    {
        _requests.Writer.TryWrite(true);
        return _status;
    }

    /// <summary>Bricht den laufenden Lauf ab; rclone wird beendet, der Lauf als fehlgeschlagen gespeichert.</summary>
    public BackupStatusDto Cancel()
    {
        try
        {
            _current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return _status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await MarkInterruptedRunsAsync(stoppingToken);
        await PublishAsync(await LoadStatusAsync(running: false, step: null, stoppingToken), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = NextRun(time.GetLocalNow());
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            if (next is { } at)
                timer.CancelAfter(at - time.GetLocalNow());
            try
            {
                await _requests.Reader.ReadAsync(timer.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Planmäßiger Lauf.
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunAsync(stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await PublishAsync(_status with { Running = true, Step = "Startet" }, ct);
        using var current = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _current = current;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var progress = new Progress<string>(step =>
            {
                if (_status.Running)
                    _ = PublishAsync(_status with { Step = step }, ct);
            });
            var run = await scope.ServiceProvider.GetRequiredService<IBackupRunner>().RunAsync(progress, current.Token);
            logger.LogInformation("Backup {Status}: {Files} Datei(en), {Bytes} Bytes", run.Status, run.FilesSynced, run.BytesSynced);
            TonregalMetrics.BackupRuns.WithLabels(run.Status == BackupStatus.Succeeded ? "succeeded" : "failed").Inc();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Backup-Lauf abgebrochen");
            TonregalMetrics.BackupRuns.WithLabels("failed").Inc();
        }
        finally
        {
            _current = null;
        }

        await PublishAsync(await LoadStatusAsync(running: false, step: null, ct), ct);
    }

    /// <summary>Läufe, die beim letzten Beenden der App noch liefen, gelten als fehlgeschlagen.</summary>
    private async Task MarkInterruptedRunsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TonregalDbContext>();
        foreach (var run in await db.BackupRuns.Where(r => r.Status == BackupStatus.Running).ToListAsync(ct))
        {
            run.Status = BackupStatus.Failed;
            run.FinishedAt ??= time.GetUtcNow();
            run.Log = string.IsNullOrEmpty(run.Log) ? "Abgebrochen (App neu gestartet)" : run.Log;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<BackupStatusDto> LoadStatusAsync(bool running, string? step, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TonregalDbContext>();
        var recent = await RecentAsync(db, 20, ct);
        var lastSuccess = recent.FirstOrDefault(r => r.Status == BackupStatus.Succeeded);
        var failures = recent.TakeWhile(r => r.Status != BackupStatus.Succeeded).Count(r => r.Status == BackupStatus.Failed);
        return new BackupStatusDto(running, step, recent.FirstOrDefault(), lastSuccess?.FinishedAt, failures, NextRun(time.GetLocalNow()));
    }

    /// <summary>Die letzten Läufe, neueste zuerst. SQLite sortiert DateTimeOffset nicht, daher nach Id.</summary>
    public static async Task<List<BackupRunDto>> RecentAsync(TonregalDbContext db, int count, CancellationToken ct) =>
        (await db.BackupRuns.AsNoTracking().OrderByDescending(r => r.Id).Take(count).ToListAsync(ct)).Select(BackupRunDto.From).ToList();

    private DateTimeOffset? NextRun(DateTimeOffset now)
    {
        var o = options.Value;
        if (!o.Enabled)
            return null;
        var today = new DateTimeOffset(now.Date + o.Time.ToTimeSpan(), now.Offset);
        return today > now ? today : today.AddDays(1);
    }

    private async Task PublishAsync(BackupStatusDto status, CancellationToken ct)
    {
        _status = status;
        if (status.LastSuccessAt is { } success)
            TonregalMetrics.BackupLastSuccess.Set(success.ToUnixTimeSeconds());
        try
        {
            await hub.Clients.All.SendAsync(StatusEvent, status, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "backupStatus konnte nicht gesendet werden");
        }
    }
}
