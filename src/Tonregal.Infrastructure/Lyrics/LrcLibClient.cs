// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Metadata;

namespace Tonregal.Infrastructure.Lyrics;

/// <summary>Ein Eintrag von lrclib.net; <see cref="Duration"/> in Sekunden.</summary>
public sealed record LrcLibTrack(
    long Id, string? TrackName, string? ArtistName, string? AlbumName, double? Duration,
    bool Instrumental, string? PlainLyrics, string? SyncedLyrics);

/// <summary>
/// lrclib.net: freie Songtext-Datenbank ohne Schlüssel, mit synchronisierten Texten. Die API bittet nur um einen
/// User-Agent mit App und Adresse; den setzt <see cref="DependencyInjection"/> wie für MusicBrainz.
/// </summary>
public sealed class LrcLibClient(HttpClient http, TimeProvider time)
{
    public const string HttpClientName = "lrclib";
    public static readonly Uri BaseAddress = new("https://lrclib.net/api/");

    /// <summary>So weit darf die Länge bei lrclib.net abweichen; die API selbst prüft beim genauen Abruf ±2 s.</summary>
    private const double DurationTolerance = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Genauer Abruf über Titel, Artist, Album und Länge; sonst die Suche, bester Treffer nach Länge.</summary>
    public async Task<LrcLibTrack?> FindAsync(string artist, string title, string? album, double durationSeconds, CancellationToken ct)
    {
        if (album is not null && durationSeconds > 0)
        {
            var query = $"get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}"
                + $"&album_name={Uri.EscapeDataString(album)}&duration={Math.Round(durationSeconds)}";
            if (await GetAsync<LrcLibTrack>(query, ct) is { } exact)
                return exact;
        }

        var hits = await GetAsync<List<LrcLibTrack>>(
            $"search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}", ct) ?? [];
        return Best(hits, durationSeconds);
    }

    /// <summary>Nur Treffer mit passender Länge (wenn bekannt); synchronisierte vor reinen, dann die nächstliegende Länge.</summary>
    internal static LrcLibTrack? Best(IEnumerable<LrcLibTrack> hits, double durationSeconds) =>
        hits
            .Where(h => h.Instrumental || !string.IsNullOrWhiteSpace(h.SyncedLyrics) || !string.IsNullOrWhiteSpace(h.PlainLyrics))
            .Where(h => durationSeconds <= 0 || h.Duration is not { } d || Math.Abs(d - durationSeconds) <= DurationTolerance)
            .OrderByDescending(h => !string.IsNullOrWhiteSpace(h.SyncedLyrics))
            .ThenBy(h => durationSeconds > 0 && h.Duration is { } d ? Math.Abs(d - durationSeconds) : 0)
            .FirstOrDefault();

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var response = await HttpRetry.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, path), "lrclib.net", time, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new ExternalToolUnavailableException($"lrclib.net: HTTP {(int)response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }
}
