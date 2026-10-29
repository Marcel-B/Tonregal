// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;

namespace Tonregal.Application.Abstractions;

/// <summary>Navidromes Subsonic-API, soweit Player und Smart Playlists sie brauchen.</summary>
public interface INavidromeClient
{
    /// <summary>Navidrome-ID der Datei (Pfad relativ zur Library) oder <c>null</c>, wenn Navidrome sie (noch) nicht kennt.</summary>
    Task<string?> FindSongIdAsync(string libraryPath, CancellationToken ct);

    /// <summary>Audio-Stream von Navidrome; <c>null</c>, wenn Navidrome ihn nicht liefert.</summary>
    Task<HttpResponseMessage?> StreamAsync(string songId, RangeHeaderValue? range, CancellationToken ct);

    /// <summary>„Läuft gerade“ (<paramref name="submission"/> = false) oder gehört, für Play-Counts.</summary>
    Task<bool> ScrobbleAsync(string songId, bool submission, CancellationToken ct);

    /// <summary>
    /// Löscht die aus einer Datei eingelesene Playlist (Pfad relativ zur Library); Navidrome selbst behält sie sonst,
    /// auch wenn die Datei weg ist. <c>false</c>, wenn Navidrome sie nicht kennt oder nicht erreichbar ist.
    /// </summary>
    Task<bool> DeletePlaylistAsync(string libraryPath, CancellationToken ct);

    /// <summary>
    /// Dateien der aus einer Datei eingelesenen Playlist in Navidromes Reihenfolge, als Pfade relativ zur Library.
    /// Navidrome wertet eine Smart Playlist beim Abruf neu aus. <c>null</c>, wenn Navidrome sie (noch) nicht kennt
    /// oder nicht erreichbar ist.
    /// </summary>
    Task<IReadOnlyList<string>?> PlaylistFilesAsync(string libraryPath, CancellationToken ct);
}
