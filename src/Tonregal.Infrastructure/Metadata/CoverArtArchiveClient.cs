// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Tonregal.Application.Abstractions;

namespace Tonregal.Infrastructure.Metadata;

/// <summary>Front-Cover aus dem Cover Art Archive (per MusicBrainz-Release- oder Release-Gruppen-ID), 1200 px.</summary>
public sealed class CoverArtArchiveClient(HttpClient http, TimeProvider time) : ICoverArtSource
{
    public const string HttpClientName = "coverartarchive";
    public static readonly Uri BaseAddress = new("https://coverartarchive.org/");

    public async Task<EmbeddedPicture?> GetFrontCoverAsync(Guid releaseId, Guid? releaseGroupId, CancellationToken ct) =>
        await GetAsync($"release/{releaseId}/front-1200", ct)
        ?? (releaseGroupId is { } group ? await GetAsync($"release-group/{group}/front-1200", ct) : null);

    private async Task<EmbeddedPicture?> GetAsync(string path, CancellationToken ct)
    {
        using var response = await HttpRetry.SendAsync(
            http, () => new HttpRequestMessage(HttpMethod.Get, path), "Cover Art Archive", time, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new ExternalToolUnavailableException($"Cover Art Archive: HTTP {(int)response.StatusCode}");

        var data = await response.Content.ReadAsByteArrayAsync(ct);
        return new EmbeddedPicture(data, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }
}
