// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>Ein Song, unabhängig von der physischen Datei.</summary>
public class Track
{
    public int Id { get; set; }
    public int AlbumId { get; set; }
    public Album Album { get; set; } = null!;
    public int Disc { get; set; } = 1;
    public int Number { get; set; }
    public required string Title { get; set; }
    public List<Artist> Artists { get; set; } = [];
    public int DurationMs { get; set; }
    public Guid? MusicBrainzRecordingId { get; set; }
    public Guid? AcoustId { get; set; }
    public double? Bpm { get; set; }
    public string? InitialKey { get; set; }
    public double? ReplayGainTrack { get; set; }
    public string? Genre { get; set; }

    /// <summary>1 bis 5 Sterne; <c>null</c> = unbewertet. Tonregal hält sie selbst, Navidrome bekommt eine Kopie.</summary>
    public int? Rating { get; set; }

    /// <summary>
    /// Zuletzt in Tonregal bewertet oder aus Navidrome übernommen. <c>null</c> heißt: Tonregal hatte nie eine Bewertung,
    /// eine aus Navidrome darf sie übernehmen. Auch eine zurückgenommene Bewertung setzt den Zeitpunkt.
    /// </summary>
    public DateTimeOffset? RatedAt { get; set; }

    /// <summary>Die Bewertung muss noch an Navidrome (dort für Smart Playlists), z. B. weil es das Stück noch nicht kannte.</summary>
    public bool RatingPending { get; set; }

    public List<AudioFile> Files { get; set; } = [];
}
