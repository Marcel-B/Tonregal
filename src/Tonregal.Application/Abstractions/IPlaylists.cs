// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application.Abstractions;

/// <summary>Name und Kommentar einer eigenen Playlist; ein leerer Kommentar heißt „keiner“.</summary>
public sealed record PlaylistInput(string Name, string? Comment = null);

/// <param name="AlbumIds">Bis zu vier verschiedene Alben der ersten Stücke, für ein Cover-Mosaik.</param>
public sealed record PlaylistSummary(
    int Id,
    string Name,
    string? Comment,
    int TrackCount,
    long DurationMs,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<int> AlbumIds);

/// <param name="Items">Die Stücke in ihrer Reihenfolge, je Track die bevorzugte Datei (FLAC zuerst).</param>
public sealed record PlaylistDetail(PlaylistSummary Playlist, IReadOnlyList<PlaylistItem> Items);

/// <param name="Added">Neu aufgenommene Stücke.</param>
/// <param name="Skipped">Stücke, die schon in der Playlist standen.</param>
public sealed record PlaylistAddResult(int Added, int Skipped, PlaylistSummary Playlist);

/// <summary>
/// Eigene Playlists. Ungültige Eingaben werfen <see cref="ArgumentException"/> (Parametername = Feld), ein vergebener
/// Name <see cref="InvalidOperationException"/>. Methoden mit <c>null</c>-Antwort kennen die Playlist nicht.
/// </summary>
public interface IPlaylists
{
    Task<IReadOnlyList<PlaylistSummary>> ListAsync(CancellationToken ct);

    Task<PlaylistDetail?> GetAsync(int id, CancellationToken ct);

    Task<PlaylistSummary> CreateAsync(PlaylistInput input, CancellationToken ct);

    Task<PlaylistSummary?> UpdateAsync(int id, PlaylistInput input, CancellationToken ct);

    Task<bool> DeleteAsync(int id, CancellationToken ct);

    /// <summary>Hängt Tracks hinten an; vorhandene werden übersprungen, unbekannte sind ein Fehler.</summary>
    Task<PlaylistAddResult?> AddAsync(int id, IReadOnlyList<int> trackIds, CancellationToken ct);

    /// <summary>
    /// Setzt die Stücke als Ganzes: neue Reihenfolge, fehlende fliegen heraus. Doppelte zählen einmal, unbekannte sind
    /// ein Fehler.
    /// </summary>
    Task<PlaylistDetail?> SetTracksAsync(int id, IReadOnlyList<int> trackIds, CancellationToken ct);

    /// <returns><c>false</c>, wenn Playlist oder Stück darin fehlen.</returns>
    Task<bool> RemoveAsync(int id, int trackId, CancellationToken ct);

    /// <summary>Schreibt alle <c>.m3u8</c>-Dateien, die fehlen oder abweichen, und entfernt verwaiste von Tonregal.</summary>
    Task WriteFilesAsync(CancellationToken ct);
}
