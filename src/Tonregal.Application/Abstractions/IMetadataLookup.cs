// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.ValueObjects;

namespace Tonregal.Application.Abstractions;

/// <summary>Ein Treffer: eine Aufnahme auf einem bestimmten Release.</summary>
public sealed record RecordingCandidate(
    Guid RecordingId,
    Guid? AcoustId,
    string Title,
    IReadOnlyList<string> Artists,
    int? DurationMs,
    Guid? ReleaseId,
    Guid? ReleaseGroupId,
    string? ReleaseTitle,
    string? ReleaseArtist,
    string? ReleaseType,
    string? ReleaseStatus,
    int? Year,
    int? Disc,
    int? TrackNumber,
    int? TrackCount,
    string? ReleaseDate,
    string? Country,
    string? MediumFormat,
    double Score);

/// <summary>AcoustID- und MusicBrainz-Abgleich, rate-limitiert und gecacht.</summary>
public interface IMetadataLookup
{
    /// <summary>Kandidaten, bester zuerst; leer, wenn AcoustID nichts kennt.</summary>
    /// <exception cref="ExternalToolUnavailableException">Kein API-Key oder Dienst nicht erreichbar.</exception>
    Task<IReadOnlyList<RecordingCandidate>> LookupAsync(Fingerprint fingerprint, CancellationToken ct);

    /// <summary>
    /// Kandidaten allein aus den Tags (MusicBrainz-Suche nach Titel und Artist), für Dateien ohne
    /// brauchbaren Fingerprint. Releases mit passendem Albumtitel und passender Länge zuerst; der
    /// Score ist der Such-Score von MusicBrainz, keine Audio-Übereinstimmung.
    /// </summary>
    /// <exception cref="ExternalToolUnavailableException">MusicBrainz nicht erreichbar.</exception>
    Task<IReadOnlyList<RecordingCandidate>> SearchByTagsAsync(string artist, string title, string? album, int durationMs, CancellationToken ct);

    /// <summary>Vollständige Angaben zu einer Aufnahme auf einem Release; <c>null</c>, wenn sie dort nicht vorkommt.</summary>
    /// <exception cref="ExternalToolUnavailableException">MusicBrainz nicht erreichbar.</exception>
    Task<ReleaseTrack?> GetReleaseTrackAsync(Guid releaseId, Guid recordingId, CancellationToken ct);

    /// <summary>Release-Gruppe (das Album über alle Ausgaben) eines Releases; <c>null</c>, wenn MusicBrainz das Release nicht kennt.</summary>
    /// <exception cref="ExternalToolUnavailableException">MusicBrainz nicht erreichbar.</exception>
    Task<Guid?> GetReleaseGroupIdAsync(Guid releaseId, CancellationToken ct);
}

/// <summary>Cover Art Archive.</summary>
public interface ICoverArtSource
{
    /// <summary>
    /// Front-Cover des Releases, sonst das Standard-Cover seiner Release-Gruppe, oder <c>null</c>,
    /// wenn es keins gibt. Viele Länder-Ausgaben haben kein eigenes Bild.
    /// </summary>
    Task<EmbeddedPicture?> GetFrontCoverAsync(Guid releaseId, Guid? releaseGroupId, CancellationToken ct);
}
