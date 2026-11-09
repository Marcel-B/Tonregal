// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application.Abstractions;

/// <summary>Was ein Abgleich mit Navidrome getan hat.</summary>
/// <param name="Imported">Bewertungen aus Navidrome für Stücke, die Tonregal nie bewertet hatte.</param>
/// <param name="Pushed">Bewertungen aus Tonregal, die Navidrome jetzt hat.</param>
/// <param name="Pending">Bewertungen, die noch auf Navidrome warten (Stück noch nicht gescannt, Navidrome nicht erreichbar).</param>
public sealed record RatingSyncResult(int Imported, int Pushed, int Pending);

/// <summary>
/// Sterne je Stück, in Tonregals Datenbank. Die API spricht Datei-IDs, weil die Player Dateien spielen; MP3 und FLAC
/// desselben Stücks teilen sich eine Bewertung. Navidrome bekommt jede Bewertung als Kopie, damit seine Smart Playlists
/// sie weiter auswerten.
/// </summary>
public interface ITrackRatings
{
    /// <summary>Sterne je Datei-ID; unbewertete und unbekannte Dateien fehlen.</summary>
    Task<IReadOnlyDictionary<int, int>> ForFilesAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct);

    /// <summary>Setzt die Bewertung (0 = keine, 1 bis 5 Sterne); <c>false</c>, wenn es die Datei nicht gibt.</summary>
    Task<bool> SetAsync(int fileId, int rating, CancellationToken ct);

    /// <summary>
    /// Übernimmt Navidromes Bewertungen für Stücke, die in Tonregal nie bewertet wurden, und schickt offene an Navidrome.
    /// Ohne Navidrome tut er nichts.
    /// </summary>
    Task<RatingSyncResult> SyncAsync(CancellationToken ct);
}
