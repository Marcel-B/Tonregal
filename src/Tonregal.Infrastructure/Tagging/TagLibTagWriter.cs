// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Application.Abstractions;
using TagLib;
using File = TagLib.File;

namespace Tonregal.Infrastructure.Tagging;

/// <summary>
/// Schreibt Tags mit TagLibSharp, MP3 immer als ID3v2.4. Feldnamen wie MusicBrainz Picard, damit
/// Navidrome, Picard und DJ-Software dieselben Werte sehen.
/// </summary>
public sealed class TagLibTagWriter : ITagWriter
{
    static TagLibTagWriter()
    {
        TagLib.Id3v2.Tag.DefaultVersion = 4;
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
    }

    public void Write(string path, ReleaseTrack track, EmbeddedPicture? cover)
    {
        using var file = Open(path);
        // MP3/WAV: ID3v2 anlegen, falls die Datei noch keins hat; Vorbis/MP4 haben ihr Tag schon.
        if (file is TagLib.Mpeg.AudioFile or TagLib.Riff.File)
            file.GetTag(TagTypes.Id3v2, true);

        var tag = file.Tag;
        tag.Title = track.Title;
        tag.Performers = [.. track.Artists];
        tag.PerformersSort = track.ArtistSort is { } sort ? [sort] : [];
        tag.AlbumArtists = [track.AlbumArtist];
        tag.AlbumArtistsSort = track.AlbumArtistSort is { } albumSort ? [albumSort] : [];
        tag.Album = track.Album;
        tag.Year = (uint)(track.Year ?? 0);
        tag.Disc = (uint)track.Disc;
        tag.DiscCount = (uint)track.DiscCount;
        tag.Track = (uint)track.Track;
        tag.TrackCount = (uint)track.TrackCount;

        tag.MusicBrainzTrackId = track.RecordingId.ToString();
        tag.MusicBrainzReleaseId = track.ReleaseId.ToString();
        tag.MusicBrainzReleaseGroupId = track.ReleaseGroupId?.ToString();
        tag.MusicBrainzArtistId = Join(track.ArtistIds);
        tag.MusicBrainzReleaseArtistId = Join(track.AlbumArtistIds);
        tag.MusicBrainzReleaseType = track.ReleaseType;
        tag.MusicBrainzReleaseStatus = track.ReleaseStatus;
        tag.MusicBrainzReleaseCountry = track.Country;

        SetCustom(file, "MusicBrainz Release Track Id", "MUSICBRAINZ_RELEASETRACKID", "MusicBrainz Release Track Id", track.TrackId?.ToString());
        SetCustom(file, "Acoustid Id", "ACOUSTID_ID", "Acoustid Id", track.AcoustId?.ToString());
        SetCompilation(file, track.IsCompilation);

        if (cover is not null && tag.Pictures.Length == 0)
        {
            tag.Pictures =
            [
                new Picture(new ByteVector(cover.Data)) { Type = PictureType.FrontCover, MimeType = cover.MimeType, Description = "Cover" },
            ];
        }

        file.Save();
    }

    public void WriteBasic(string path, string title, IReadOnlyList<string> artists, string albumArtist, string album)
    {
        using var file = Open(path);
        if (file is TagLib.Mpeg.AudioFile or TagLib.Riff.File)
            file.GetTag(TagTypes.Id3v2, true);

        var tag = file.Tag;
        tag.Title = title;
        tag.Performers = [.. artists];
        tag.AlbumArtists = [albumArtist];
        tag.Album = album;
        file.Save();
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

    private static string? Join(IReadOnlyList<Guid> ids) =>
        ids.Count == 0 ? null : string.Join('/', ids.Select(id => id.ToString()));

    /// <summary>Freies Feld je Format, Gegenstück zu <see cref="TagLibTagReader"/>; <c>null</c> löscht es.</summary>
    private static void SetCustom(File file, string id3, string xiph, string apple, string? value)
    {
        if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3Tag)
        {
            if (value is null)
            {
                if (TagLib.Id3v2.UserTextInformationFrame.Get(id3Tag, id3, false) is { } frame)
                    id3Tag.RemoveFrame(frame);
            }
            else
            {
                TagLib.Id3v2.UserTextInformationFrame.Get(id3Tag, id3, true).Text = [value];
            }
        }

        if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiphTag)
        {
            if (value is null)
                xiphTag.RemoveField(xiph);
            else
                xiphTag.SetField(xiph, value);
        }

        if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag appleTag)
            appleTag.SetDashBox("com.apple.iTunes", apple, value ?? "");
    }

    private static void SetCompilation(File file, bool compilation)
    {
        if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3Tag)
            id3Tag.SetTextFrame("TCMP", compilation ? "1" : null);
        if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiphTag)
        {
            if (compilation)
                xiphTag.SetField("COMPILATION", "1");
            else
                xiphTag.RemoveField("COMPILATION");
        }

        if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag appleTag)
            appleTag.IsCompilation = compilation;
    }
}
