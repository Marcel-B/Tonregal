// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;

namespace Tonregal.Application.Abstractions;

/// <summary>Tags einer Audiodatei, wie sie gelesen bzw. geschrieben werden (ID3v2.4 / Vorbis / MP4).</summary>
public sealed record AudioTags
{
    public string? Title { get; init; }
    public IReadOnlyList<string> Artists { get; init; } = [];
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public int? Year { get; init; }
    public int? Disc { get; init; }
    public int? DiscCount { get; init; }
    public int? Track { get; init; }
    public string? Genre { get; init; }
    public IReadOnlyList<string> Moods { get; init; } = [];
    public double? Bpm { get; init; }
    public string? InitialKey { get; init; }
    public Guid? MusicBrainzRecordingId { get; init; }
    public Guid? MusicBrainzReleaseId { get; init; }
    public Guid? MusicBrainzArtistId { get; init; }
    public Guid? AcoustId { get; init; }
    public double? ReplayGainTrackGain { get; init; }
    public double? ReplayGainAlbumGain { get; init; }
    public bool IsCompilation { get; init; }
}

public sealed record AudioProperties(
    AudioFormat Format, TimeSpan Duration, int Bitrate, int SampleRate, int Channels);

public interface ITagService
{
    (AudioTags Tags, AudioProperties Properties) Read(string path);

    /// <summary>Schreibt Tags sicher: in Kopie schreiben, verifizieren, atomar umbenennen.</summary>
    Task WriteAsync(string path, AudioTags tags, byte[]? cover, CancellationToken ct);
}
