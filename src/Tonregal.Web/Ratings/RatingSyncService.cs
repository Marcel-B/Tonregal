// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;

namespace Tonregal.Web.Ratings;

/// <summary>
/// Gleicht Bewertungen eine Minute nach dem Start und dann alle <see cref="NavidromeOptions.RatingSyncMinutes"/> mit
/// Navidrome ab: der erste Lauf übernimmt die dort gesetzten, danach reicht er nach, was Navidrome beim Bewerten noch
/// nicht kannte.
/// </summary>
public sealed class RatingSyncService(
    IServiceScopeFactory scopes,
    IOptions<NavidromeOptions> options,
    TimeProvider time,
    ILogger<RatingSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.RatingSyncMinutes;
        if (minutes <= 0 || !options.Value.Configured)
            return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes), time);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ITrackRatings>().SyncAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Abgleich der Bewertungen mit Navidrome fehlgeschlagen");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Dienst endet
        }
    }
}
