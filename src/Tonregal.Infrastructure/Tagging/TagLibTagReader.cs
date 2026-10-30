// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using TagLib;
using File = TagLib.File;

namespace Tonregal.Infrastructure.Tagging;

/// <summary>Liest Tags und Audio-Eigenschaften mit TagLibSharp.</summary>
public sealed class TagLibTagReader : ITagReader
{
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav" };

    public AudioFileInfo Read(string path)
    {
        using var file = Open(path);
        var tag = file.Tag;

        var tags = new AudioTags
        {
            Title = NullIfEmpty(tag.Title),
            Artists = SplitArtists(tag.Performers),
            ArtistSort = NullIfEmpty(tag.PerformersSort.FirstOrDefault()),
            AlbumArtist = NullIfEmpty(tag.FirstAlbumArtist),
            AlbumArtistSort = NullIfEmpty(tag.AlbumArtistsSort.FirstOrDefault()),
            Album = NullIfEmpty(tag.Album),
            Year = tag.Year > 0 ? (int)tag.Year : null,
            Disc = tag.Disc > 0 ? (int)tag.Disc : null,
            DiscCount = tag.DiscCount > 0 ? (int)tag.DiscCount : null,
            Track = tag.Track > 0 ? (int)tag.Track : null,
            Genre = NullIfEmpty(tag.FirstGenre),
            Bpm = tag.BeatsPerMinute > 0 ? tag.BeatsPerMinute : ParseDouble(Custom(file, "TBPM", "BPM", "tmpo")),
            InitialKey = NullIfEmpty(tag.InitialKey) ?? Custom(file, "TKEY", "INITIALKEY", "initialkey"),
            MusicBrainzRecordingId = ParseGuid(tag.MusicBrainzTrackId),
            MusicBrainzReleaseId = ParseGuid(tag.MusicBrainzReleaseId),
            MusicBrainzArtistId = ParseGuid(tag.MusicBrainzArtistId),
            AcoustId = ParseGuid(Custom(file, "Acoustid Id", "ACOUSTID_ID", "Acoustid Id")),
            ReplayGainTrackGain = double.IsNaN(tag.ReplayGainTrackGain) ? null : tag.ReplayGainTrackGain,
            ReplayGainAlbumGain = double.IsNaN(tag.ReplayGainAlbumGain) ? null : tag.ReplayGainAlbumGain,
            IsCompilation = IsCompilation(file),
        };

        var p = file.Properties;
        var properties = new AudioProperties(
            FormatOf(file, path), p.Duration, p.AudioBitrate, p.AudioSampleRate, p.AudioChannels);
        return new AudioFileInfo(tags, properties);
    }

    public EmbeddedPicture? ReadCover(string path)
    {
        using var file = Open(path);
        var pictures = file.Tag.Pictures;
        var picture = pictures.FirstOrDefault(p => p.Type == PictureType.FrontCover) ?? pictures.FirstOrDefault();
        return picture is null
            ? null
            : new EmbeddedPicture(picture.Data.Data, string.IsNullOrEmpty(picture.MimeType) ? "image/jpeg" : picture.MimeType);
    }

    public string? ReadLyrics(string path)
    {
        using var file = Open(path);
        return NullIfEmpty(file.Tag.Lyrics);
    }

    private static File Open(string path)
    {
        try
        {
            return File.Create(path, ReadStyle.Average);
        }
        catch (Exception e) when (e is UnsupportedFormatException or CorruptFileException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Mehrere Artists stehen je nach Format als eigene Werte oder durch ";" getrennt in einem Wert.
    /// "/" wird bewusst nicht getrennt (AC/DC).
    /// </summary>
    private static IReadOnlyList<string> SplitArtists(IEnumerable<string> performers) =>
        performers
            .SelectMany(p => p.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Freies Feld je Format: ID3v2 TXXX bzw. Text-Frame, Vorbis Comment, MP4-Freeform-Atom.</summary>
    private static string? Custom(File file, string id3, string xiph, string apple)
    {
        if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3Tag)
        {
            var value = id3.Length == 4 && id3.All(char.IsUpper)
                ? id3Tag.GetTextAsString(id3)
                : TagLib.Id3v2.UserTextInformationFrame.Get(id3Tag, id3, false)?.Text.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiphTag
            && xiphTag.GetFirstField(xiph) is { Length: > 0 } xiphValue)
            return xiphValue.Trim();

        if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag appleTag
            && appleTag.GetDashBox("com.apple.iTunes", apple) is { Length: > 0 } appleValue)
            return appleValue.Trim();

        return null;
    }

    private static bool IsCompilation(File file)
    {
        if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag appleTag && appleTag.IsCompilation)
            return true;
        return Custom(file, "TCMP", "COMPILATION", "cpil") is "1" or "true";
    }

    private static AudioFormat FormatOf(File file, string path)
    {
        var codec = file.Properties.Codecs.FirstOrDefault(c => c is not null && c.MediaTypes.HasFlag(MediaTypes.Audio));
        var description = codec?.Description ?? "";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => AudioFormat.Mp3,
            ".flac" => AudioFormat.Flac,
            ".m4a" or ".aac" => description.Contains("lossless", StringComparison.OrdinalIgnoreCase) ? AudioFormat.Alac : AudioFormat.Aac,
            ".opus" => AudioFormat.Opus,
            ".ogg" => description.Contains("opus", StringComparison.OrdinalIgnoreCase) ? AudioFormat.Opus : AudioFormat.Ogg,
            ".wav" => AudioFormat.Wav,
            _ => AudioFormat.Unknown,
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var guid) ? guid : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
}
