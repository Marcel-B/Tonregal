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
}

/// <summary>Cover Art Archive.</summary>
public interface ICoverArtSource
{
    /// <summary>Front-Cover des Releases oder <c>null</c>, wenn es keins gibt.</summary>
    Task<EmbeddedPicture?> GetFrontCoverAsync(Guid releaseId, CancellationToken ct);
}
