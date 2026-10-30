// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Monitoring;
using Tonregal.Web.Hubs;

namespace Tonregal.Web.Analysis;

public sealed record AnalysisStatus(
    bool Running, bool Requested, string? CurrentAlbum, AnalysisCounts? Counts, string? Waiting,
    DateTimeOffset? LastRunAt, string? LastError, IReadOnlyList<string> RecentErrors);

/// <summary>
/// Analyse-Worker: arbeitet die Library albumweise mit dem <see cref="ILibraryAnalyzer"/> ab. Ohne
/// Start-Knopf nur im Nachtfenster und nur, wenn der <see cref="AnalysisOptions.BusyCommand"/> keinen
/// laufenden KI-Job meldet; mit Knopf sofort, bis alles analysiert ist oder jemand stoppt.
/// Jeder Fortschritt geht als <c>analysisStatus</c> an die SPA.
/// </summary>
public sealed class AnalysisService(
    IServiceScopeFactory scopes,
    IHubContext<StatusHub> hub,
    IOptions<AnalysisOptions> options,
    TimeProvider time,
    ILogger<AnalysisService> logger) : BackgroundService
{
    public const string StatusEvent = "analysisStatus";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private const int MaxRecentErrors = 20;

    private readonly SemaphoreSlim _wake = new(0);
    private volatile bool _requested;
    private volatile bool _paused;
    private volatile AnalysisStatus _status = new(false, false, null, null, null, null, null, []);

    public AnalysisStatus Status => _status;

    /// <summary>Startet die Analyse sofort, unabhängig vom Nachtfenster.</summary>
    public AnalysisStatus Start()
    {
        _requested = true;
        _paused = false;
        _wake.Release();
        return _status = _status with { Requested = true };
    }

    /// <summary>Hält nach dem laufenden Album an; erst die nächste Nacht (oder der Knopf) startet sie wieder.</summary>
    public AnalysisStatus Stop()
    {
        _requested = false;
        _paused = true;
        _wake.Release();
        return _status = _status with { Requested = false };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshCountsAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunWhileAllowedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _wake.WaitAsync(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunWhileAllowedAsync(CancellationToken ct)
    {
        var errors = _status.RecentErrors.ToList();
        string? waiting;
        while ((waiting = await WaitReasonAsync(ct)) is null)
        {
            await PublishAsync(_status with { Running = true, Waiting = null }, ct);
            AlbumAnalysis? album;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var analyzer = scope.ServiceProvider.GetRequiredService<ILibraryAnalyzer>();
                album = await analyzer.AnalyzeNextAlbumAsync(ct);
                if (album is not null)
                {
                    // Errors hat je gescheiterte Datei eine Zeile, ReplayGain kann für ein Album eine dazulegen.
                    var failed = Math.Min(album.Errors.Count, album.Files);
                    TonregalMetrics.Analysis.WithLabels("failed").Inc(failed);
                    TonregalMetrics.Analysis.WithLabels("done").Inc(album.Files - failed);
                }
                errors.InsertRange(0, album?.Errors ?? []);
                errors = [.. errors.Take(MaxRecentErrors)];
                await PublishAsync(_status with
                {
                    CurrentAlbum = album?.Album, Counts = await analyzer.CountAsync(ct), LastRunAt = time.GetUtcNow(),
                    LastError = null, RecentErrors = errors,
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // Fehlendes Werkzeug, SSD weg, DB-Konflikt: anhalten und es beim nächsten Anlauf erneut versuchen.
                if (e is ExternalToolUnavailableException or LibraryUnavailableException)
                    logger.LogWarning("Analyse angehalten: {Message}", e.Message);
                else
                    logger.LogError(e, "Analyse fehlgeschlagen");
                _requested = false;
                await PublishAsync(_status with { Running = false, Requested = false, CurrentAlbum = null, LastError = e.Message }, ct);
                return;
            }

            if (album is null)
            {
                _requested = false;
                await PublishAsync(_status with { Running = false, Requested = false, CurrentAlbum = null, Waiting = "Alles analysiert" }, ct);
                return;
            }
        }

        await PublishAsync(_status with { Running = false, Requested = _requested, CurrentAlbum = null, Waiting = waiting }, ct);
    }

    /// <summary><c>null</c>, wenn die Analyse jetzt laufen darf, sonst der Grund fürs Warten.</summary>
    private async Task<string?> WaitReasonAsync(CancellationToken ct)
    {
        if (_requested)
            return null;
        var o = options.Value;
        if (!o.RunAtNight)
            return _paused ? "Angehalten" : "Startet nur per Knopf";
        var now = TimeOnly.FromDateTime(time.GetLocalNow().DateTime);
        if (!o.InNightWindow(now))
        {
            _paused = false;
            return $"Wartet aufs Nachtfenster {o.NightStart:HH\\:mm}–{o.NightEnd:HH\\:mm}";
        }

        if (_paused)
            return "Angehalten bis zur nächsten Nacht";
        return await IsBusyAsync(o.BusyCommand, ct) ? "Ein KI-Job läuft gerade" : null;
    }

    private async Task<bool> IsBusyAsync(string? command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", command])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("BusyCommand fehlgeschlagen, Analyse läuft trotzdem: {Message}", e.Message);
            return false;
        }
    }

    private async Task RefreshCountsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var counts = await scope.ServiceProvider.GetRequiredService<ILibraryAnalyzer>().CountAsync(ct);
            await PublishAsync(_status with { Counts = counts }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Analyse-Stand konnte nicht gelesen werden");
        }
    }

    private async Task PublishAsync(AnalysisStatus status, CancellationToken ct)
    {
        if (status.Counts is { } counts)
            TonregalMetrics.AnalysisWaiting.Set(counts.Pending);
        _status = status;
        try
        {
            await hub.Clients.All.SendAsync(StatusEvent, status, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "analysisStatus konnte nicht gesendet werden");
        }
    }
}
