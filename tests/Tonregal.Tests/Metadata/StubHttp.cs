// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;

namespace Tonregal.Tests.Metadata;

/// <summary>HTTP-Attrappe: beantwortet Anfragen aus einer Funktion und merkt sich, was gesendet wurde.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage File(string testDataFile) =>
        Json(System.IO.File.ReadAllText(Path.Combine(TestLibrary.TestData, testDataFile)));

    public HttpClient Client(Uri baseAddress) => new(this) { BaseAddress = baseAddress };
}

/// <summary>TimeProvider, bei dem Wartezeiten (Retry-Backoff) sofort ablaufen.</summary>
public sealed class NoDelayTimeProvider : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, TimeSpan.Zero, period);
}
