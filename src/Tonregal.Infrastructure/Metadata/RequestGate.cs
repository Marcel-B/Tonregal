// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Infrastructure.Metadata;

/// <summary>
/// Hält einen Mindestabstand zwischen zwei Anfragen an denselben Dienst (MusicBrainz 1/s,
/// AcoustID 3/s). Eine Instanz pro Dienst, als Singleton – die HttpMessageHandler selbst
/// werden von der HttpClientFactory regelmäßig neu gebaut.
/// </summary>
public sealed class RequestGate(TimeSpan minimumInterval, TimeProvider time)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    public TimeSpan MinimumInterval => minimumInterval;

    public async Task WaitAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var wait = _next - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, time, ct);
            _next = time.GetUtcNow() + minimumInterval;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Lässt jede Anfrage durch das <see cref="RequestGate"/> des Dienstes. Die Zeit für die Antwort
/// zählt erst danach: Mit <c>HttpClient.Timeout</c> zählte auch das Warten am Tor, und bei
/// vielen Anfragen in der Schlange lief so eine Anfrage ab, bevor sie überhaupt gesendet war.
/// </summary>
public sealed class RateLimitedHandler(RequestGate gate, TimeSpan? timeout = null) : DelegatingHandler
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        var limit = timeout ?? DefaultTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        try
        {
            return await base.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"keine Antwort nach {limit.TotalSeconds:0} s");
        }
    }
}
