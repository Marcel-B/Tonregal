// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;

namespace Tonregal.Infrastructure.Navidrome;

/// <summary>
/// Spricht Navidromes Subsonic-API mit Token-Anmeldung. Die Song-ID kommt aus navidrome.db, weil die API
/// keine Suche nach Dateipfad kennt; Navidrome speichert den Pfad je nach Version absolut oder relativ
/// zum Musikordner.
/// </summary>
public sealed class NavidromeClient(
    HttpClient http,
    IOptions<NavidromeOptions> options,
    IOptions<TonregalOptions> tonregal,
    TimeProvider time,
    ILogger<NavidromeClient> logger) : INavidromeClient
{
    public const string ApiVersion = "1.16.1";
    public const string ClientName = "Tonregal";

    private NavidromeOptions O => options.Value;

    public string Database => string.IsNullOrWhiteSpace(O.Database)
        ? Path.Combine(tonregal.Value.MusicRoot, "navidrome", "navidrome.db")
        : O.Database;

    public Task<string?> FindSongIdAsync(string libraryPath, CancellationToken ct) => FindIdAsync("media_file", libraryPath, ct);

    public async Task<bool> DeletePlaylistAsync(string libraryPath, CancellationToken ct)
    {
        if (await FindIdAsync("playlist", libraryPath, ct) is not { } playlistId)
            return false;
        try
        {
            using var response = await http.GetAsync(Url("deletePlaylist", $"id={Uri.EscapeDataString(playlistId)}&f=json"), ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var ok = json.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString() == "ok";
            if (!ok)
                logger.LogWarning("Navidrome löscht Playlist {Id} nicht: {Body}", playlistId, Truncate(json.RootElement.ToString()));
            return ok;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Navidrome-Playlist {Id} nicht gelöscht: {Message}", playlistId, e.Message);
            return false;
        }
    }

    public async Task<IReadOnlyList<string>?> PlaylistFilesAsync(string libraryPath, CancellationToken ct)
    {
        if (await FindIdAsync("playlist", libraryPath, ct) is not { } playlistId)
            return null;
        List<string> songIds;
        try
        {
            using var response = await http.GetAsync(Url("getPlaylist", $"id={Uri.EscapeDataString(playlistId)}&f=json"), ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement.GetProperty("subsonic-response");
            if (root.GetProperty("status").GetString() != "ok")
            {
                logger.LogWarning("Navidrome liefert Playlist {Id} nicht: {Body}", playlistId, Truncate(root.ToString()));
                return null;
            }

            var playlist = root.GetProperty("playlist");
            songIds = playlist.TryGetProperty("entry", out var entries)
                ? [.. entries.EnumerateArray().Select(e => e.GetProperty("id").GetString()!)]
                : [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Navidrome-Playlist {Id} nicht gelesen: {Message}", playlistId, e.Message);
            return null;
        }

        if (songIds.Count == 0)
            return [];
        var paths = await SongPathsAsync(songIds, ct);
        if (paths is null)
            return null;
        // Navidrome speichert je nach Version absolute oder zur Library relative Pfade.
        var library = (Path.GetFullPath(tonregal.Value.Library).TrimEnd('/') + "/").Normalize(NormalizationForm.FormC);
        return [.. songIds.Where(paths.ContainsKey)
            .Select(id => paths[id].Normalize(NormalizationForm.FormC))
            .Select(p => p.StartsWith(library, StringComparison.Ordinal) ? p[library.Length..] : p)];
    }

    private async Task<Dictionary<string, string>?> SongPathsAsync(IReadOnlyList<string> songIds, CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAsync(ct);
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            // SQLite erlaubt nur begrenzt viele Parameter je Abfrage.
            foreach (var chunk in songIds.Distinct().Chunk(500))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT id, path FROM media_file WHERE id IN ({string.Join(", ", chunk.Select((_, i) => $"$i{i}"))})";
                for (var i = 0; i < chunk.Length; i++)
                    command.Parameters.AddWithValue($"$i{i}", chunk[i]);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    paths[reader.GetString(0)] = reader.GetString(1);
            }

            return paths;
        }
        catch (SqliteException e)
        {
            logger.LogWarning("navidrome.db nicht lesbar ({Database}): {Message}", Database, e.Message);
            return null;
        }
    }

    // Nicht ReadOnly: bei WAL-Datenbanken muss SQLite die -shm-Datei anfassen dürfen; gelesen wird nur.
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Database, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5,
        }.ToString());
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>So kann der Pfad in navidrome.db stehen: relativ oder absolut, in NFC oder NFD.</summary>
    private string[] PathCandidates(string libraryPath)
    {
        var relative = libraryPath.Replace('\\', '/');
        var absolute = Path.Combine(Path.GetFullPath(tonregal.Value.Library), relative);
        return [.. new[] { relative, absolute }
            .SelectMany(p => new[] { p.Normalize(NormalizationForm.FormC), p.Normalize(NormalizationForm.FormD) })
            .Distinct()];
    }

    /// <param name="table"><c>media_file</c> oder <c>playlist</c>, beide mit Spalten <c>id</c> und <c>path</c>.</param>
    private async Task<string?> FindIdAsync(string table, string libraryPath, CancellationToken ct)
    {
        if (!O.Configured || !File.Exists(Database))
            return null;
        var candidates = PathCandidates(libraryPath);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id FROM {table} WHERE path IN ({string.Join(", ", candidates.Select((_, i) => $"$p{i}"))}) LIMIT 1";
            for (var i = 0; i < candidates.Length; i++)
                command.Parameters.AddWithValue($"$p{i}", candidates[i]);
            return await command.ExecuteScalarAsync(ct) as string;
        }
        catch (SqliteException e)
        {
            logger.LogWarning("navidrome.db nicht lesbar ({Database}): {Message}", Database, e.Message);
            return null;
        }
    }

    /// <remarks>
    /// Aus navidrome.db statt über die API, weil ein Album sonst eine Anfrage je Stück bräuchte. Navidrome legt
    /// Bewertungen, Play-Counts und Sterne je Benutzer in <c>annotation</c> ab.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, int>> RatingsAsync(IReadOnlyCollection<string> libraryPaths, CancellationToken ct)
    {
        var ratings = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!O.Configured || !File.Exists(Database) || libraryPaths.Count == 0)
            return ratings;
        try
        {
            await using var connection = await OpenAsync(ct);
            // Höchstens 4 Schreibweisen je Pfad; SQLite erlaubt nur begrenzt viele Parameter je Abfrage.
            foreach (var chunk in libraryPaths.Distinct(StringComparer.Ordinal).Chunk(100))
            {
                var byCandidate = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var path in chunk)
                {
                    foreach (var candidate in PathCandidates(path))
                        byCandidate.TryAdd(candidate, path);
                }

                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT m.path, a.rating FROM media_file m
                    JOIN annotation a ON a.item_id = m.id AND a.item_type = 'media_file'
                    JOIN "user" u ON u.id = a.user_id
                    WHERE u.user_name = $user COLLATE NOCASE AND a.rating > 0
                      AND m.path IN ({string.Join(", ", byCandidate.Keys.Select((_, i) => $"$p{i}"))})
                    """;
                command.Parameters.AddWithValue("$user", O.User);
                var index = 0;
                foreach (var candidate in byCandidate.Keys)
                    command.Parameters.AddWithValue($"$p{index++}", candidate);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (byCandidate.TryGetValue(reader.GetString(0), out var path))
                        ratings[path] = Math.Clamp(reader.GetInt32(1), 1, 5);
                }
            }
        }
        catch (SqliteException e)
        {
            logger.LogWarning("Bewertungen aus navidrome.db nicht lesbar ({Database}): {Message}", Database, e.Message);
        }

        return ratings;
    }

    public async Task<bool> SetRatingAsync(string songId, int rating, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(
                Url("setRating", $"id={Uri.EscapeDataString(songId)}&rating={Math.Clamp(rating, 0, 5)}&f=json"), ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var ok = json.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString() == "ok";
            if (!ok)
                logger.LogWarning("Navidrome nimmt die Bewertung für {Id} nicht an: {Body}", songId, Truncate(json.RootElement.ToString()));
            return ok;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Bewertung an Navidrome fehlgeschlagen: {Message}", e.Message);
            return false;
        }
    }

    /// <remarks>Das Timeout des HttpClient gilt nur bis zu den Headern, nicht für den Rest des Streams.</remarks>
    public async Task<HttpResponseMessage?> StreamAsync(string songId, RangeHeaderValue? range, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("stream", $"id={Uri.EscapeDataString(songId)}"));
        request.Headers.Range = range;
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning("Navidrome nicht erreichbar: {Message}", e.Message);
            return null;
        }

        // Fehler meldet die Subsonic-API mit Status 200 und XML/JSON statt Audio.
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (response.IsSuccessStatusCode && !type.Contains("xml", StringComparison.Ordinal) && !type.Contains("json", StringComparison.Ordinal))
            return response;
        logger.LogWarning("Navidrome liefert für {Id} keinen Stream: {Status} {Body}",
            songId, (int)response.StatusCode, Truncate(await response.Content.ReadAsStringAsync(ct)));
        response.Dispose();
        return null;
    }

    public async Task<bool> ScrobbleAsync(string songId, bool submission, CancellationToken ct)
    {
        var ms = time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        try
        {
            using var response = await http.GetAsync(
                Url("scrobble", $"id={Uri.EscapeDataString(songId)}&submission={(submission ? "true" : "false")}&time={ms}&f=json"), ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var ok = json.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString() == "ok";
            if (!ok)
                logger.LogWarning("Navidrome-Scrobble für {Id} abgelehnt: {Body}", songId, Truncate(json.RootElement.ToString()));
            return ok;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Navidrome-Scrobble fehlgeschlagen: {Message}", e.Message);
            return false;
        }
    }

    /// <summary>Subsonic-URL mit Salt und Token = md5(Passwort + Salt); das Passwort selbst geht nie über die Leitung.</summary>
    internal Uri Url(string method, string query)
    {
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var token = Token(O.Password, salt);
        return new Uri($"{O.Url.TrimEnd('/')}/rest/{method}?u={Uri.EscapeDataString(O.User)}&t={token}&s={salt}&v={ApiVersion}&c={ClientName}&{query}");
    }

#pragma warning disable CA5351 // Die Subsonic-API schreibt MD5 vor.
    internal static string Token(string password, string salt) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password + salt)));
#pragma warning restore CA5351

    private static string Truncate(string text) => text.Length > 300 ? text[..300] : text;
}
