// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Infrastructure.Metadata;

/// <summary>
/// MusicBrainz-Web-API (ws/2). Höchstens 1 Anfrage/s über das <see cref="RequestGate"/>,
/// Antworten liegen <see cref="MetadataOptions.CacheDays"/> Tage im DB-Cache.
/// </summary>
public sealed class MusicBrainzClient(
    HttpClient http, TonregalDbContext db, IOptions<MetadataOptions> options, TimeProvider time)
{
    public const string HttpClientName = "musicbrainz";
    public static readonly Uri BaseAddress = new("https://musicbrainz.org/ws/2/");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Aufnahme mit Artists und allen Releases inkl. Release-Group und Track-Position.</summary>
    public Task<MbRecording?> GetRecordingAsync(Guid id, CancellationToken ct) =>
        GetAsync<MbRecording>($"recording:{id}", $"recording/{id}?inc=artist-credits+releases+release-groups+media&fmt=json", ct);

    /// <summary>Release mit vollständiger Trackliste, Artists und Release-Group.</summary>
    public Task<MbRelease?> GetReleaseAsync(Guid id, CancellationToken ct) =>
        GetAsync<MbRelease>($"release:{id}", $"release/{id}?inc=artist-credits+recordings+release-groups+media&fmt=json", ct);

    private async Task<T?> GetAsync<T>(string key, string path, CancellationToken ct) where T : class
    {
        var cacheKey = $"musicbrainz:{key}";
        var cached = await db.MetadataCache.FindAsync([cacheKey], ct);
        if (cached is not null && cached.FetchedAt > time.GetUtcNow().AddDays(-options.Value.CacheDays))
            return JsonSerializer.Deserialize<T>(cached.Json, Json);

        using var response = await HttpRetry.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, path), "MusicBrainz", time, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new ExternalToolUnavailableException($"MusicBrainz: HTTP {(int)response.StatusCode} für {path}");

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<T>(json, Json);

        if (cached is null)
            db.MetadataCache.Add(new MetadataCacheEntry { Key = cacheKey, Json = json, FetchedAt = time.GetUtcNow() });
        else
        {
            cached.Json = json;
            cached.FetchedAt = time.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
        return result;
    }
}
