// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using TagLib;
using File = TagLib.File;

namespace Tonregal.Infrastructure.Tagging;

/// <summary>
/// Liest Tags und Audio-Eigenschaften mit TagLibSharp. Scheitert TagLib (kaputter Bild-Frame,
/// Nullbytes vor dem Ton, ID3-Tag vor einer WAV-Hülle), liest <paramref name="ffmpeg"/> die Datei;
/// findet auch ffprobe keinen Ton, sagt die Meldung, was stattdessen in der Datei steckt.
/// </summary>
public sealed class TagLibTagReader(FfmpegAudio? ffmpeg = null) : ITagReader
{
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav" };

    public AudioFileInfo Read(string path)
    {
        if (!TryOpen(path, out var opened, out var error))
            return ReadWithFfprobe(path, error);
        using var file = opened;
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

    /// <summary>Was TagLib nicht öffnet, hat für Tonregal kein Cover (meist ist gerade der Bild-Frame kaputt).</summary>
    public EmbeddedPicture? ReadCover(string path)
    {
        if (!TryOpen(path, out var opened, out _))
            return null;
        using var file = opened;
        var pictures = file.Tag.Pictures;
        var picture = pictures.FirstOrDefault(p => p.Type == PictureType.FrontCover) ?? pictures.FirstOrDefault();
        return picture is null
            ? null
            : new EmbeddedPicture(picture.Data.Data, string.IsNullOrEmpty(picture.MimeType) ? "image/jpeg" : picture.MimeType);
    }

    public string? ReadLyrics(string path)
    {
        if (!TryOpen(path, out var opened, out _))
            return null;
        using var file = opened;
        return NullIfEmpty(file.Tag.Lyrics);
    }

    /// <summary>Ob TagLib die Datei öffnet, also Tags in sie schreiben kann.</summary>
    public static bool CanOpen(string path)
    {
        if (!TryOpen(path, out var file, out _))
            return false;
        file.Dispose();
        return true;
    }

    private static bool TryOpen(string path, [NotNullWhen(true)] out File? file, [NotNullWhen(false)] out Exception? error)
    {
        try
        {
            file = File.Create(path, ReadStyle.Average);
            error = null;
            return true;
        }
        catch (Exception e) when (e is UnsupportedFormatException or CorruptFileException)
        {
            file = null;
            error = e;
            return false;
        }
    }

    private AudioFileInfo ReadWithFfprobe(string path, Exception error)
    {
        var name = Path.GetFileName(path);
        ProbedAudio? probed;
        try
        {
            probed = ffmpeg?.Probe(path);
        }
        catch (ExternalToolUnavailableException)
        {
            probed = null;
        }

        if (probed is null)
            throw new InvalidDataException($"{name}: {error.Message.TrimEnd('.')}. {Describe(path)}", error);
        if (probed.Format == AudioFormat.Unknown)
        {
            throw new InvalidDataException(
                $"{name}: Die Datei enthält {probed.Description}, das Tonregal nicht führt (nur MP3, FLAC, AAC, Apple Lossless, Ogg, Opus und WAV).",
                error);
        }

        var t = probed.Tags;
        var tags = new AudioTags
        {
            Title = Tag(t, "title"),
            Artists = SplitArtists(Tag(t, "artist") is { } artist ? [artist] : []),
            ArtistSort = Tag(t, "artist-sort", "artistsort", "TSOP"),
            AlbumArtist = Tag(t, "album_artist", "albumartist"),
            AlbumArtistSort = Tag(t, "album_artist-sort", "albumartistsort", "TSO2"),
            Album = Tag(t, "album"),
            Year = Tag(t, "date", "year", "TDRC", "TYER") is { Length: >= 4 } date && int.TryParse(date[..4], out var year) && year > 0 ? year : null,
            Disc = Number(Tag(t, "disc", "discnumber", "TPOS"), 0),
            DiscCount = Number(Tag(t, "disc", "discnumber", "TPOS"), 1) ?? Number(Tag(t, "disctotal", "totaldiscs"), 0),
            Track = Number(Tag(t, "track", "tracknumber", "TRCK"), 0),
            Genre = Tag(t, "genre"),
            Bpm = ParseDouble(Tag(t, "TBPM", "bpm", "tmpo")),
            InitialKey = Tag(t, "TKEY", "initialkey", "key"),
            MusicBrainzRecordingId = ParseGuid(Tag(t, "musicbrainz_trackid", "MusicBrainz Track Id")),
            MusicBrainzReleaseId = ParseGuid(Tag(t, "musicbrainz_albumid", "MusicBrainz Album Id")),
            MusicBrainzArtistId = ParseGuid(Tag(t, "musicbrainz_artistid", "MusicBrainz Artist Id")),
            AcoustId = ParseGuid(Tag(t, "acoustid_id", "Acoustid Id")),
            ReplayGainTrackGain = ParseGain(Tag(t, "replaygain_track_gain")),
            ReplayGainAlbumGain = ParseGain(Tag(t, "replaygain_album_gain")),
            IsCompilation = Tag(t, "compilation", "TCMP", "cpil") is "1" or "true",
        };
        return new AudioFileInfo(tags, new AudioProperties(probed.Format, probed.Duration, probed.Bitrate, probed.SampleRate, probed.Channels));
    }

    private static string Describe(string path)
    {
        try
        {
            return AudioSniffer.Describe(path);
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static string? Tag(IReadOnlyDictionary<string, string> tags, params string[] names) =>
        names.Select(n => tags.TryGetValue(n, out var value) ? NullIfEmpty(value) : null).FirstOrDefault(v => v is not null);

    /// <summary>Teil <paramref name="part"/> von „3/12“.</summary>
    private static int? Number(string? value, int part) =>
        value?.Split('/') is { } parts && parts.Length > part && int.TryParse(parts[part].Trim(), out var n) && n > 0 ? n : null;

    private static double? ParseGain(string? value) =>
        value is null ? null
            : double.TryParse(value.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var gain)
                ? gain
                : null;

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
