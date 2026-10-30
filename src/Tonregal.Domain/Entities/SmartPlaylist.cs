// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>
/// Regeln einer Smart Playlist. Tonregal schreibt sie als <c>.nsp</c>-Datei in die Library, Navidrome liest sie ein
/// und füllt sie selbst; alle gesetzten Regeln müssen zugleich gelten.
/// </summary>
public class SmartPlaylist
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Comment { get; set; }

    /// <summary>BPM-Bereich, Grenzen eingeschlossen; Stücke ohne BPM fallen heraus, sobald eine Grenze gesetzt ist.</summary>
    public double? BpmMin { get; set; }
    public double? BpmMax { get; set; }

    /// <summary>Tonarten, von denen eine passen muss (Schreibweise wie im Tag, z. B. <c>Am</c>, <c>F#</c>).</summary>
    public List<string> Keys { get; set; } = [];

    /// <summary>Teil des Genres, Groß-/Kleinschreibung egal.</summary>
    public string? Genre { get; set; }

    /// <summary>Nur Stücke, die Navidrome in den letzten so vielen Tagen neu gesehen hat.</summary>
    public int? AddedWithinDays { get; set; }

    /// <summary>Nur Stücke, die in Navidrome noch nie gespielt wurden.</summary>
    public bool NeverPlayed { get; set; }

    /// <summary>Nur Stücke mit mindestens so vielen Sternen (1 bis 5); bewertet wird in Navidrome.</summary>
    public int? MinRating { get; set; }

    public SmartPlaylistSort Sort { get; set; }
    public int? Limit { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
