// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Web.Hubs;

namespace Tonregal.Web.Inbox;

public sealed record InboxStatus(
    bool Running, string? CurrentPath, int Processed, int Waiting, DateTimeOffset? LastRunAt, string? LastError);

/// <summary>
/// Überwacht die Inbox: <see cref="FileSystemWatcher"/> plus Vollscan beim Start und alle paar
/// Minuten (externe SSDs melden Events nicht immer zuverlässig). Neue Dateien laufen nacheinander
/// durch den <see cref="IInboxImporter"/>; jeder Fortschritt geht als <c>inboxStatus</c> an die SPA.
/// </summary>
public sealed class InboxService(
    IServiceScopeFactory scopes,
    IHubContext<StatusHub> hub,
    IOptions<TonregalOptions> options,
    TimeProvider time,
    ILogger<InboxService> logger) : BackgroundService
{
    public const string StatusEvent = "inboxStatus";
    private static readonly TimeSpan FullScanInterval = TimeSpan.FromMinutes(5);

    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile InboxStatus _status = new(false, null, 0, 0, null, null);
    private FileSystemWatcher? _watcher;

    public InboxStatus Status => _status;

    /// <summary>Fordert einen Durchlauf an; läuft bereits einer, folgt höchstens ein weiterer.</summary>
    public void RequestRun() => _requests.Writer.TryWrite(true);

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
        await PublishAsync(_status with { Running = true, CurrentPath = null, Processed = 0 }, ct);
        var processed = 0;
        var waiting = 0;
        try
        {
            EnsureWatcher();
            InboxSyncResult sync;
            await using (var scope = scopes.CreateAsyncScope())
                sync = await scope.ServiceProvider.GetRequiredService<IInboxImporter>().SyncAsync(ct);
            waiting = sync.Waiting;

            // Dateien, die noch kopiert werden, nach der Ruhephase erneut ansehen.
            if (sync.Waiting > 0)
                _ = RequestRunLaterAsync(TimeSpan.FromSeconds(options.Value.InboxStableSeconds + 1), ct);

            while (true)
            {
                // Pro Datei ein frischer DbContext, damit der Change-Tracker nicht wächst.
                await using var scope = scopes.CreateAsyncScope();
                var job = await scope.ServiceProvider.GetRequiredService<IInboxImporter>().ProcessNextAsync(ct);
                if (job is null)
                    break;
                processed++;
                await PublishAsync(_status with { CurrentPath = job.SourcePath, Processed = processed, Waiting = sync.Waiting }, ct);
            }

            await PublishAsync(new InboxStatus(false, null, processed, sync.Waiting, time.GetUtcNow(), null), ct);
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
            await PublishAsync(new InboxStatus(false, null, processed, waiting, time.GetUtcNow(), e.Message), ct);
        }
    }

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

    private async Task PublishAsync(InboxStatus status, CancellationToken ct)
    {
        _status = status;
        try
        {
            await hub.Clients.All.SendAsync(StatusEvent, status, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "inboxStatus konnte nicht gesendet werden");
        }
    }
}
