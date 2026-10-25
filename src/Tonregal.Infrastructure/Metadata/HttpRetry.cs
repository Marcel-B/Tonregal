// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Tonregal.Application.Abstractions;

namespace Tonregal.Infrastructure.Metadata;

internal static class HttpRetry
{
    private const int MaxAttempts = 3;

    /// <summary>
    /// Sendet eine Anfrage und wiederholt sie bei 503/429 (MusicBrainz drosselt so) oder
    /// Netzwerkfehlern. Die Anfrage wird für jeden Versuch neu gebaut, weil sie sich nicht zweimal
    /// senden lässt.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, Func<HttpRequestMessage> createRequest, string service, TimeProvider time, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(createRequest(), ct);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await Task.Delay(Backoff(attempt, null), time, ct);
                continue;
            }
            catch (HttpRequestException e)
            {
                throw new ExternalToolUnavailableException($"{service} nicht erreichbar: {e.Message}", e);
            }

            if (response.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests))
                return response;
            if (attempt >= MaxAttempts)
                throw new ExternalToolUnavailableException($"{service} überlastet (HTTP {(int)response.StatusCode})");

            var retryAfter = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            await Task.Delay(Backoff(attempt, retryAfter), time, ct);
        }
    }

    private static TimeSpan Backoff(int attempt, TimeSpan? retryAfter) =>
        retryAfter is { } delta && delta < TimeSpan.FromSeconds(30) ? delta : TimeSpan.FromSeconds(2 * attempt);
}
