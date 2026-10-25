// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>Ein Release; entspricht einer Ordnerebene in der Library.</summary>
public class Album
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public int AlbumArtistId { get; set; }
    public Artist AlbumArtist { get; set; } = null!;
    public int? Year { get; set; }
    public Guid? MusicBrainzReleaseId { get; set; }
    public int DiscCount { get; set; } = 1;
    public string? CoverPath { get; set; }
    public AlbumKind Kind { get; set; } = AlbumKind.Album;

    public List<Track> Tracks { get; set; } = [];
}
