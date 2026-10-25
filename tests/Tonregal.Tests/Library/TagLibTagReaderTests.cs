// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Tests.Library;

public class TagLibTagReaderTests
{
    private readonly TagLibTagReader _reader = new();

    private static string File(string name) => Path.Combine(TestLibrary.TestData, name);

    [Fact]
    public void Liest_ID3v2_Tags_und_Eigenschaften()
    {
        var (tags, props) = _reader.Read(File("song1.mp3"));

        Assert.Equal("Erster Song", tags.Title);
        Assert.Equal(["Testband"], tags.Artists);
        Assert.Equal("Testband", tags.AlbumArtist);
        Assert.Equal("Testalbum", tags.Album);
        Assert.Equal(2024, tags.Year);
        Assert.Equal(1, tags.Track);
        Assert.Equal("Rock", tags.Genre);
        Assert.Equal(120, tags.Bpm);
        Assert.Equal("Am", tags.InitialKey);
        Assert.Equal(AudioFormat.Mp3, props.Format);
        Assert.InRange(props.Duration.TotalSeconds, 2.9, 3.2);
    }

    [Fact]
    public void Liest_Vorbis_Comments_mit_mehreren_Artists_und_MusicBrainz_Id()
    {
        var (tags, props) = _reader.Read(File("song2.flac"));

        Assert.Equal(["Testband", "Gast"], tags.Artists);
        Assert.Equal(2, tags.Track);
        Assert.Equal(Guid.Parse("6f0e1a5c-6a43-4f4e-9b5c-2f6c2b2f1a11"), tags.MusicBrainzRecordingId);
        Assert.Equal(AudioFormat.Flac, props.Format);
        Assert.Equal(22050, props.SampleRate);
    }

    [Fact]
    public void Liest_MP4_und_Opus()
    {
        var m4a = _reader.Read(File("song3.m4a"));
        var opus = _reader.Read(File("song4.opus"));

        Assert.Equal("Dritter Song", m4a.Tags.Title);
        Assert.Equal(AudioFormat.Aac, m4a.Properties.Format);
        Assert.Equal("Sampler", opus.Tags.Album);
        Assert.True(opus.Tags.IsCompilation);
        Assert.Equal(AudioFormat.Opus, opus.Properties.Format);
    }

    [Fact]
    public void Liest_eingebettetes_Cover()
    {
        var cover = _reader.ReadCover(File("song1.mp3"));

        Assert.NotNull(cover);
        Assert.Equal("image/jpeg", cover.MimeType);
        Assert.Null(_reader.ReadCover(File("song2.flac")));
    }

    [Fact]
    public void Kaputte_Datei_wirft_InvalidDataException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3");
        System.IO.File.WriteAllText(path, "kein mp3");
        try
        {
            Assert.Throws<InvalidDataException>(() => _reader.Read(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
