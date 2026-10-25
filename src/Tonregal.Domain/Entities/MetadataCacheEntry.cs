// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>Gecachte Antwort eines Metadaten-Dienstes (z. B. MusicBrainz-Recording), als JSON.</summary>
public class MetadataCacheEntry
{
    /// <summary>Dienst, Typ und ID, z. B. <c>musicbrainz:recording:{mbid}</c>.</summary>
    public required string Key { get; set; }
    public required string Json { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}
