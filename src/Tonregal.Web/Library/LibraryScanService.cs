// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Tonregal.Application.Abstractions;
using Tonregal.Web.Hubs;

namespace Tonregal.Web.Library;

public sealed record ScanStatus(
    bool Running, ScanProgress? Progress, DateTimeOffset? LastRunAt, ScanResult? LastResult, string? LastError);

/// <summary>
/// Führt Library-Scans nacheinander im Hintergrund aus: einmal beim Start und auf Anfrage.
/// Fortschritt und Ergebnis gehen als <c>scanStatus</c> an alle SignalR-Clients.
/// </summary>
public sealed class LibraryScanService(
    IServiceScopeFactory scopes,
    IHubContext<StatusHub> hub,
    TimeProvider time,
    ILogger<LibraryScanService> logger) : BackgroundService
{
    public const string StatusEvent = "scanStatus";

    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile ScanStatus _status = new(false, null, null, null, null);

    public ScanStatus Status => _status;

    /// <summary>Fordert einen Scan an; läuft bereits einer, folgt höchstens ein weiterer.</summary>
    public void RequestScan() => _requests.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RequestScan();
        await foreach (var _ in _requests.Reader.ReadAllAsync(stoppingToken))
            await RunAsync(stoppingToken);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await PublishAsync(_status with { Running = true, Progress = null }, ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var scanner = scope.ServiceProvider.GetRequiredService<ILibraryScanner>();
            var progress = new SyncProgress(p => _ = PublishAsync(_status with { Progress = p }, ct));
            var result = await scanner.ScanAsync(progress, ct);
            await PublishAsync(new ScanStatus(false, null, time.GetUtcNow(), result, null), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (e is LibraryUnavailableException)
                logger.LogWarning("Library-Scan übersprungen: {Message}", e.Message);
            else
                logger.LogError(e, "Library-Scan fehlgeschlagen");
            await PublishAsync(_status with { Running = false, Progress = null, LastRunAt = time.GetUtcNow(), LastError = e.Message }, ct);
        }
    }

    private async Task PublishAsync(ScanStatus status, CancellationToken ct)
    {
        _status = status;
        try
        {
            await hub.Clients.All.SendAsync(StatusEvent, status, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "scanStatus konnte nicht gesendet werden");
        }
    }

    /// <summary>Meldet synchron; <see cref="Progress{T}"/> würde über den Threadpool nachlaufen.</summary>
    private sealed class SyncProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
