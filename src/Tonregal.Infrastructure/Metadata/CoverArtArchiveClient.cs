// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Tonregal.Application.Abstractions;

namespace Tonregal.Infrastructure.Metadata;

/// <summary>Front-Cover aus dem Cover Art Archive (per MusicBrainz-Release-ID), 1200 px.</summary>
public sealed class CoverArtArchiveClient(HttpClient http, TimeProvider time) : ICoverArtSource
{
    public const string HttpClientName = "coverartarchive";
    public static readonly Uri BaseAddress = new("https://coverartarchive.org/");

    public async Task<EmbeddedPicture?> GetFrontCoverAsync(Guid releaseId, CancellationToken ct)
    {
        using var response = await HttpRetry.SendAsync(
            http, () => new HttpRequestMessage(HttpMethod.Get, $"release/{releaseId}/front-1200"), "Cover Art Archive", time, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new ExternalToolUnavailableException($"Cover Art Archive: HTTP {(int)response.StatusCode}");

        var data = await response.Content.ReadAsByteArrayAsync(ct);
        return new EmbeddedPicture(data, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }
}
