// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;

namespace Tonregal.Application.Abstractions;

/// <summary>Was die Seite „Smart Playlists“ anlegt oder ändert; leere Listen und Texte heißen „keine Regel“.</summary>
public sealed record SmartPlaylistInput(
    string Name,
    string? Comment,
    double? BpmMin,
    double? BpmMax,
    IReadOnlyList<string>? Keys,
    string? Genre,
    int? AddedWithinDays,
    bool NeverPlayed,
    SmartPlaylistSort Sort,
    int? Limit);

/// <param name="Path">Die <c>.nsp</c>-Datei relativ zur Library.</param>
/// <param name="Matching">Passende Stücke in Tonregals Bibliothek; Hör-Regeln kennt nur Navidrome, sie zählen hier nicht.</param>
/// <param name="Nsp">Inhalt der Datei, wie Navidrome sie liest.</param>
public sealed record SmartPlaylistInfo(
    int Id,
    string Name,
    string? Comment,
    double? BpmMin,
    double? BpmMax,
    IReadOnlyList<string> Keys,
    string? Genre,
    int? AddedWithinDays,
    bool NeverPlayed,
    SmartPlaylistSort Sort,
    int? Limit,
    DateTimeOffset UpdatedAt,
    string Path,
    int Matching,
    string Nsp);

public sealed record KeyCount(string Key, int Tracks);

/// <summary>
/// Smart Playlists in der Datenbank und als <c>.nsp</c>-Dateien unter <c>Library/Playlists</c>. Ungültige Eingaben
/// werfen <see cref="ArgumentException"/> (Parametername = Feld), ein vergebener Name <see cref="InvalidOperationException"/>.
/// </summary>
public interface ISmartPlaylists
{
    Task<IReadOnlyList<SmartPlaylistInfo>> ListAsync(CancellationToken ct);

    /// <summary>Tonarten der Bibliothek mit Anzahl, als Auswahl für die Regeln.</summary>
    Task<IReadOnlyList<KeyCount>> KeysAsync(CancellationToken ct);

    Task<SmartPlaylistInfo> CreateAsync(SmartPlaylistInput input, CancellationToken ct);

    /// <returns><c>null</c>, wenn es die Playlist nicht gibt.</returns>
    Task<SmartPlaylistInfo?> UpdateAsync(int id, SmartPlaylistInput input, CancellationToken ct);

    /// <summary>Löscht Eintrag und Datei und, soweit erreichbar, die eingelesene Playlist in Navidrome.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct);

    /// <summary>Schreibt alle Dateien neu, die fehlen oder abweichen, und entfernt fremde Reste von Tonregal.</summary>
    Task WriteFilesAsync(CancellationToken ct);
}
