// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Tonregal.Application.Abstractions;

namespace Tonregal.Infrastructure.Metadata;

internal static class HttpRetry
{
    private const int MaxAttempts = 3;

    /// <summary>
    /// Sendet eine Anfrage und wiederholt sie bei 503/429 (MusicBrainz drosselt so), Netzwerkfehlern
    /// oder Zeitüberschreitung. Die Anfrage wird für jeden Versuch neu gebaut, weil sie sich nicht zweimal
    /// senden lässt. <paramref name="throttled"/> hört jede Drosselung, auch die letzte.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, Func<HttpRequestMessage> createRequest, string service, TimeProvider time, CancellationToken ct,
        Action? throttled = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(createRequest(), ct);
            }
            catch (Exception e) when (IsTransient(e, ct) && attempt < MaxAttempts)
            {
                await Task.Delay(Backoff(attempt, null), time, ct);
                continue;
            }
            catch (Exception e) when (IsTransient(e, ct))
            {
                // Als „nicht verfügbar“ melden: Der Job behält den Grund und kommt beim nächsten Lauf
                // wieder dran, statt den Durchlauf mit einer rohen Ausnahme abzubrechen.
                throw new ExternalToolUnavailableException($"{service} nicht erreichbar: {e.Message}", e);
            }

            if (response.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests))
                return response;
            throttled?.Invoke();
            if (attempt >= MaxAttempts)
                throw new ExternalToolUnavailableException($"{service} überlastet (HTTP {(int)response.StatusCode})");

            var retryAfter = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            await Task.Delay(Backoff(attempt, retryAfter), time, ct);
        }
    }

    /// <summary>Netzwerkfehler oder abgelaufene Wartezeit, nicht aber ein Abbruch durch den Aufrufer.</summary>
    private static bool IsTransient(Exception e, CancellationToken ct) =>
        e is HttpRequestException or TimeoutException
        || (e is TaskCanceledException && !ct.IsCancellationRequested);

    private static TimeSpan Backoff(int attempt, TimeSpan? retryAfter) =>
        retryAfter is { } delta && delta < TimeSpan.FromSeconds(30) ? delta : TimeSpan.FromSeconds(2 * attempt);
}
