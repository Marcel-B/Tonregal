// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;

namespace Tonregal.Application.Abstractions;

/// <summary>Alles, was beim Übernehmen in die Tags einer Datei geschrieben wird: ein Track auf einem Release.</summary>
public sealed record ReleaseTrack
{
    public required Guid RecordingId { get; init; }
    public Guid? TrackId { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<string> Artists { get; init; }
    public IReadOnlyList<Guid> ArtistIds { get; init; } = [];
    public string? ArtistSort { get; init; }

    public required Guid ReleaseId { get; init; }
    public Guid? ReleaseGroupId { get; init; }
    public required string Album { get; init; }
    public required string AlbumArtist { get; init; }
    public IReadOnlyList<Guid> AlbumArtistIds { get; init; } = [];
    public string? AlbumArtistSort { get; init; }
    public AlbumKind Kind { get; init; }

    /// <summary>MusicBrainz-Typ, z. B. "album" oder "album; compilation".</summary>
    public string? ReleaseType { get; init; }
    public string? ReleaseStatus { get; init; }
    public string? Country { get; init; }
    public int? Year { get; init; }

    public int Disc { get; init; } = 1;
    public int DiscCount { get; init; } = 1;
    public int Track { get; init; }
    public int TrackCount { get; init; }

    public Guid? AcoustId { get; init; }

    public bool IsCompilation => Kind == AlbumKind.Compilation;
}
