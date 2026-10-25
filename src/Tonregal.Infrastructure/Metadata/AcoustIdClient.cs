// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.ValueObjects;

namespace Tonregal.Infrastructure.Metadata;

public sealed record AcoustIdMatch(Guid AcoustId, double Score, IReadOnlyList<Guid> RecordingIds);

/// <summary>AcoustID-Web-API (<c>/v2/lookup</c>): Fingerprint → AcoustID + MusicBrainz-Recording-IDs.</summary>
public sealed class AcoustIdClient(HttpClient http, IOptions<MetadataOptions> options, TimeProvider time)
{
    public const string HttpClientName = "acoustid";
    public static readonly Uri BaseAddress = new("https://api.acoustid.org/v2/");

    public async Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(Fingerprint fingerprint, CancellationToken ct)
    {
        var apiKey = options.Value.AcoustIdApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ExternalToolUnavailableException(
                "AcoustID-API-Key fehlt (Tonregal:Metadata:AcoustIdApiKey, kostenlos unter https://acoustid.org/new-application)");

        // POST statt GET: Fingerprints sind für eine URL zu lang.
        using var response = await HttpRetry.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Post, "lookup")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client"] = apiKey,
                ["meta"] = "recordingids",
                ["duration"] = ((int)Math.Round(fingerprint.Duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
                ["fingerprint"] = fingerprint.Value,
            }),
        }, "AcoustID", time, ct);

        var body = await response.Content.ReadFromJsonAsync<LookupResponse>(ct);
        if (body?.Status != "ok")
        {
            var message = body?.Error?.Message ?? $"HTTP {(int)response.StatusCode}";
            throw new ExternalToolUnavailableException($"AcoustID: {message}");
        }

        return (body.Results ?? [])
            .Select(r => new AcoustIdMatch(r.Id, r.Score, (r.Recordings ?? []).Select(rec => rec.Id).Distinct().ToList()))
            .OrderByDescending(m => m.Score)
            .ToList();
    }

    private sealed record LookupResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("results")] List<Result>? Results,
        [property: JsonPropertyName("error")] Error? Error);

    private sealed record Result(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("recordings")] List<Recording>? Recordings);

    private sealed record Recording([property: JsonPropertyName("id")] Guid Id);

    private sealed record Error(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string? Message);
}
