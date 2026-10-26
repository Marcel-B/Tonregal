// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;

namespace Tonregal.Application.Abstractions;

/// <summary>Navidromes Subsonic-API, soweit der Player sie braucht.</summary>
public interface INavidromeClient
{
    /// <summary>Navidrome-ID der Datei (Pfad relativ zur Library) oder <c>null</c>, wenn Navidrome sie (noch) nicht kennt.</summary>
    Task<string?> FindSongIdAsync(string libraryPath, CancellationToken ct);

    /// <summary>Audio-Stream von Navidrome; <c>null</c>, wenn Navidrome ihn nicht liefert.</summary>
    Task<HttpResponseMessage?> StreamAsync(string songId, RangeHeaderValue? range, CancellationToken ct);

    /// <summary>„Läuft gerade“ (<paramref name="submission"/> = false) oder gehört, für Play-Counts.</summary>
    Task<bool> ScrobbleAsync(string songId, bool submission, CancellationToken ct);
}
