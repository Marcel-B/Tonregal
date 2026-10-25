// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.ValueObjects;

namespace Tonregal.Application.Abstractions;

public sealed record RecordingCandidate(
    Guid RecordingId,
    Guid? AcoustId,
    string Title,
    IReadOnlyList<string> Artists,
    Guid? ReleaseId,
    Guid? ReleaseGroupId,
    string? ReleaseTitle,
    int? Year,
    int? Disc,
    int? TrackNumber,
    double Score);

/// <summary>AcoustID- und MusicBrainz-Abgleich, rate-limitiert und gecacht.</summary>
public interface IMetadataLookup
{
    Task<IReadOnlyList<RecordingCandidate>> LookupAsync(Fingerprint fingerprint, CancellationToken ct);
}

/// <summary>Cover Art Archive.</summary>
public interface ICoverArtSource
{
    Task<byte[]?> GetFrontCoverAsync(Guid releaseId, CancellationToken ct);
}
