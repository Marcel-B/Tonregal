// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>
/// Eine selbst zusammengestellte Playlist: feste Stücke in fester Reihenfolge, anders als eine <see cref="SmartPlaylist"/>.
/// Tonregal hält sie in der Datenbank und schreibt zusätzlich eine <c>.m3u8</c>-Datei in die Library.
/// </summary>
public class Playlist
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<PlaylistEntry> Entries { get; set; } = [];
}

/// <summary>
/// Ein Stück einer Playlist. Es hängt am Track, nicht an der Datei, damit eine bessere Datei desselben Songs
/// (FLAC statt MP3) die Playlist nicht verändert; jeder Track steht höchstens einmal darin.
/// </summary>
public class PlaylistEntry
{
    public int PlaylistId { get; set; }
    public Playlist Playlist { get; set; } = null!;
    public int TrackId { get; set; }
    public Track Track { get; set; } = null!;

    /// <summary>Lückenlos ab 0.</summary>
    public int Position { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}
