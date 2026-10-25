// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;

namespace Tonregal.Application.Abstractions;

/// <summary>Tags einer Audiodatei (ID3v2.4 / Vorbis / MP4), formatunabhängig.</summary>
public sealed record AudioTags
{
    public string? Title { get; init; }
    public IReadOnlyList<string> Artists { get; init; } = [];
    public string? ArtistSort { get; init; }
    public string? AlbumArtist { get; init; }
    public string? AlbumArtistSort { get; init; }
    public string? Album { get; init; }
    public int? Year { get; init; }
    public int? Disc { get; init; }
    public int? DiscCount { get; init; }
    public int? Track { get; init; }
    public string? Genre { get; init; }
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

public sealed record AudioFileInfo(AudioTags Tags, AudioProperties Properties);

public sealed record EmbeddedPicture(byte[] Data, string MimeType);

public interface ITagReader
{
    /// <exception cref="InvalidDataException">Datei ist kein lesbares Audioformat.</exception>
    AudioFileInfo Read(string path);

    /// <summary>Eingebettetes Cover (Front-Cover bevorzugt) oder <c>null</c>.</summary>
    EmbeddedPicture? ReadCover(string path);
}
