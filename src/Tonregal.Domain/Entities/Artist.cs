// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>Album-Artist oder Track-Artist; "Various Artists" ist ein normaler Eintrag mit fester Rolle.</summary>
public class Artist
{
    public const string VariousArtists = "Various Artists";

    public int Id { get; set; }
    public required string Name { get; set; }
    public required string SortName { get; set; }
    public Guid? MusicBrainzArtistId { get; set; }

    public List<Album> Albums { get; set; } = [];
    public List<Track> Tracks { get; set; } = [];
}
