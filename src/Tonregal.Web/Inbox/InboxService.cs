// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Monitoring;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Hubs;
using Tonregal.Web.Library;

namespace Tonregal.Web.Inbox;

public sealed record InboxStatus(
    bool Running, string? CurrentPath, int Processed, int Waiting, int Imported, int ImportsPending,
    DateTimeOffset? LastRunAt, string? LastError, int ImportFailed = 0);

/// <summary>
/// Überwacht die Inbox: <see cref="FileSystemWatcher"/> plus Vollscan beim Start und alle paar
/// Minuten (externe SSDs melden Events nicht immer zuverlässig). Neue Dateien laufen nacheinander
/// durch den <see cref="IInboxImporter"/>; angeforderte Übernahmen laufen danach durch den
/// <see cref="ILibraryImporter"/>. Jeder Fortschritt geht als <c>inboxStatus</c> an die SPA.
/// </summary>
public sealed class InboxService(
    IServiceScopeFactory scopes,
    IHubContext<StatusHub> hub,
    LibraryScanService libraryScan,
    IOptions<TonregalOptions> options,
    TimeProvider time,
    ILogger<InboxService> logger) : BackgroundService
{
    public const string StatusEvent = "inboxStatus";
    private static readonly TimeSpan FullScanInterval = TimeSpan.FromMinutes(5);

    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Angeforderte Übernahmen; <c>Unmatched</c>: ohne Treffer, mit Titel und Interpret aus den Tags.</summary>
    private readonly ConcurrentQueue<(int Id, bool Unmatched)> _imports = new();
    private int _legacyRequested;
    private volatile InboxStatus _status = new(false, null, 0, 0, 0, 0, null, null);
    private FileSystemWatcher? _watcher;

    public InboxStatus Status => _status;

    /// <summary>Fordert einen Durchlauf an; läuft bereits einer, folgt höchstens ein weiterer.</summary>
    public void RequestRun() => _requests.Writer.TryWrite(true);

    /// <summary>Liest beim nächsten Durchlauf auch den Altbestand-Ordner ein (Massenlauf).</summary>
    public void RequestLegacyRun()
    {
        Interlocked.Exchange(ref _legacyRequested, 1);
        RequestRun();
    }

    /// <summary>Stellt Jobs zur Übernahme in die Library an; sie laufen im nächsten Durchlauf.</summary>
    public InboxStatus EnqueueImports(IEnumerable<int> jobIds)
    {
        foreach (var id in jobIds)
            _imports.Enqueue((id, false));
        RequestRun();
        return _status with { ImportsPending = _imports.Count };
    }

    /// <summary>Stellt zu prüfende Jobs ohne Vorschlag an; sie gehen mit ihren Tags in die Library.</summary>
    public InboxStatus EnqueueUnmatched(IEnumerable<int> jobIds)
    {
        foreach (var id in jobIds)
            _imports.Enqueue((id, true));
        RequestRun();
        return _status with { ImportsPending = _imports.Count };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FullScanInterval, time);
        _ = Task.Run(async () =>
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                RequestRun();
        }, stoppingToken);

        RequestRun();
        await foreach (var _ in _requests.Reader.ReadAllAsync(stoppingToken))
            await RunAsync(stoppingToken);
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await PublishAsync(_status with { Running = true, CurrentPath = null, Processed = 0, Imported = 0, ImportFailed = 0, ImportsPending = _imports.Count }, ct);
        var processed = 0;
        var waiting = 0;
        var imported = 0;
        var failed = 0;
        var legacyImported = 0;
        try
        {
            EnsureWatcher();
            InboxSyncResult sync;
            await using (var scope = scopes.CreateAsyncScope())
                sync = await scope.ServiceProvider.GetRequiredService<IInboxImporter>().SyncAsync(ct);
            waiting = sync.Waiting;
            if (Interlocked.Exchange(ref _legacyRequested, 0) == 1)
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IInboxImporter>().SyncLegacyAsync(ct);
            }

            // Dateien, die noch kopiert werden, nach der Ruhephase erneut ansehen.
            if (sync.Waiting > 0)
                _ = RequestRunLaterAsync(TimeSpan.FromSeconds(options.Value.InboxStableSeconds + 1), ct);

            // Vom Nutzer angeforderte Übernahmen zuerst; sie brauchen kein AcoustID.
            (imported, failed) = await ImportQueuedAsync(ct);

            while (true)
            {
                // Pro Datei ein frischer DbContext, damit der Change-Tracker nicht wächst.
                await using var scope = scopes.CreateAsyncScope();
                var job = await scope.ServiceProvider.GetRequiredService<IInboxImporter>().ProcessNextAsync(ct);
                if (job is null)
                    break;
                processed++;
                if (ResultOf(job) is { } result)
                    TonregalMetrics.InboxFiles.WithLabels(result).Inc();
                await PublishAsync(_status with { CurrentPath = job.SourcePath, Processed = processed, Waiting = sync.Waiting }, ct);
                if (job.Source == FileSource.Legacy)
                    legacyImported += await ImportLegacyGroupAsync(job.Group, ct);
            }

            imported += legacyImported;
            if (legacyImported > 0)
                libraryScan.RequestScan();

            await PublishAsync(new InboxStatus(false, null, processed, sync.Waiting, imported, _imports.Count, time.GetUtcNow(), null, failed), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (e is LibraryUnavailableException or ExternalToolUnavailableException)
                logger.LogWarning("Inbox: {Message}", e.Message);
            else
                logger.LogError(e, "Inbox-Durchlauf fehlgeschlagen");
            await PublishAsync(new InboxStatus(false, null, processed, waiting, imported, _imports.Count, time.GetUtcNow(), e.Message, failed), ct);
        }
    }

    /// <summary>
    /// Übernimmt alle angeforderten Jobs; danach indexiert ein Library-Scan die neuen Dateien. Eine
    /// Datei, die scheitert, behält ihren Fehler am Job; die übrigen laufen weiter.
    /// </summary>
    private async Task<(int Imported, int Failed)> ImportQueuedAsync(CancellationToken ct)
    {
        var imported = 0;
        var failed = 0;
        while (_imports.TryDequeue(out var request))
        {
            ImportJob job;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var importer = scope.ServiceProvider.GetRequiredService<ILibraryImporter>();
                job = request.Unmatched
                    ? await importer.ImportUnmatchedAsync(request.Id, ct)
                    : await importer.ImportAsync(request.Id, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Übernahme von Job {Id} fehlgeschlagen", request.Id);
                failed++;
                TonregalMetrics.InboxFiles.WithLabels("failed").Inc();
                continue;
            }

            if (job.State == ImportState.Imported)
                imported++;
            else
                failed++;
            TonregalMetrics.InboxFiles.WithLabels(job.State == ImportState.Imported ? "imported" : "failed").Inc();
            await PublishAsync(_status with { CurrentPath = job.SourcePath, Imported = imported, ImportFailed = failed, ImportsPending = _imports.Count }, ct);
            if (job.Source == FileSource.Api)
                await SendAsync(Api.IngestApi.StatusEvent, Api.IngestJobDto.From(job), ct);
        }

        if (imported > 0)
            libraryScan.RequestScan();
        return (imported, failed);
    }

    /// <summary>
    /// Massenlauf: Ist ein Altbestand-Ordner fertig abgeglichen, wandern seine erkannten Dateien ohne
    /// Klick in die Library (als Kopie). Erst dann, damit die Album-Erkennung alle Dateien gesehen hat.
    /// </summary>
    private async Task<int> ImportLegacyGroupAsync(string group, CancellationToken ct)
    {
        List<int> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TonregalDbContext>();
            var jobs = (await db.ImportJobs.AsNoTracking()
                    .Where(j => j.Source == FileSource.Legacy && ImportJob.ActiveStates.Contains(j.State)
                        && (group.Length == 0 || j.SourcePath.StartsWith(group + "/")))
                    .ToListAsync(ct))
                .Where(j => j.Group == group)
                .ToList();
            if (group.Length > 0 && jobs.Any(j => j.State is ImportState.New or ImportState.Fingerprinted))
                return 0;
            ids = jobs.Where(j => j.State == ImportState.Matched && (j.SelectedReleaseId != null || j.SelectedRecordingId != null))
                .Select(j => j.Id).ToList();
        }

        var imported = 0;
        foreach (var id in ids)
        {
            await using var scope = scopes.CreateAsyncScope();
            var job = await scope.ServiceProvider.GetRequiredService<ILibraryImporter>().ImportAsync(id, ct);
            if (job.State == ImportState.Imported)
                imported++;
            TonregalMetrics.InboxFiles.WithLabels(job.State == ImportState.Imported ? "imported" : "failed").Inc();
        }

        return imported;
    }

    /// <summary>
    /// Wie eine abgeglichene Datei für <c>tonregal_inbox_files_total</c> zählt; <c>null</c> für eine, die
    /// verschwunden ist, bevor sie an der Reihe war.
    /// </summary>
    public static string? ResultOf(ImportJob job) => job.State switch
    {
        ImportState.Matched => "matched",
        ImportState.Imported => "imported",
        ImportState.Rejected when job.Error == ImportJob.VanishedReason => null,
        ImportState.Rejected => "duplicate",
        ImportState.NeedsReview when job.Error is not null => "failed",
        _ => "unmatched",
    };

    private async Task RequestRunLaterAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, time, ct);
            RequestRun();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Der Watcher entsteht erst, wenn die Inbox existiert (SSD gemountet).</summary>
    private void EnsureWatcher()
    {
        var inbox = options.Value.Inbox;
        if (_watcher is not null || !Directory.Exists(inbox))
            return;

        _watcher = new FileSystemWatcher(inbox)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
        };
        _watcher.Created += (_, _) => RequestRun();
        _watcher.Changed += (_, _) => RequestRun();
        _watcher.Renamed += (_, _) => RequestRun();
        _watcher.Deleted += (_, _) => RequestRun();
        _watcher.Error += (_, e) =>
        {
            // z. B. SSD ausgeworfen: neu aufsetzen beim nächsten Durchlauf.
            logger.LogWarning(e.GetException(), "Inbox-Watcher ausgefallen");
            _watcher?.Dispose();
            _watcher = null;
        };
        _watcher.EnableRaisingEvents = true;
        logger.LogInformation("Inbox-Watcher auf {Inbox}", inbox);
    }

    private Task PublishAsync(InboxStatus status, CancellationToken ct)
    {
        _status = status;
        return SendAsync(StatusEvent, status, ct);
    }

    private async Task SendAsync(string name, object payload, CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync(name, payload, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "{Event} konnte nicht gesendet werden", name);
        }
    }
}
