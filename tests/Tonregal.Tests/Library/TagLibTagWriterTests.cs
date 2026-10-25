// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Tests.Library;

public sealed class TagLibTagWriterTests : IDisposable
{
    private readonly TestLibrary _library = new(populate: false);
    private readonly TagLibTagWriter _writer = new();
    private readonly TagLibTagReader _reader = new();

    public void Dispose() => _library.Dispose();

    public static ReleaseTrack Track(AlbumKind kind = AlbumKind.Album) => new()
    {
        RecordingId = Guid.Parse("11111111-0000-0000-0000-000000000001"),
        TrackId = Guid.Parse("11111111-0000-0000-0000-000000000002"),
        Title = "A Victory of Love",
        Artists = ["Alphaville"],
        ArtistIds = [Guid.Parse("11111111-0000-0000-0000-000000000003")],
        ArtistSort = "Alphaville",
        ReleaseId = Guid.Parse("11111111-0000-0000-0000-000000000004"),
        ReleaseGroupId = Guid.Parse("11111111-0000-0000-0000-000000000005"),
        Album = "Forever Young",
        AlbumArtist = kind == AlbumKind.Compilation ? "Various Artists" : "Alphaville",
        AlbumArtistIds = [Guid.Parse("11111111-0000-0000-0000-000000000003")],
        Kind = kind,
        ReleaseType = "album",
        ReleaseStatus = "official",
        Country = "DE",
        Year = 1984,
        Disc = 1,
        DiscCount = 1,
        Track = 1,
        TrackCount = 10,
        AcoustId = Guid.Parse("11111111-0000-0000-0000-000000000006"),
    };

    [Theory]
    [InlineData("song1.mp3")]
    [InlineData("song2.flac")]
    [InlineData("song3.m4a")]
    [InlineData("song4.opus")]
    [InlineData("song5-riff.mp3")]
    public void Schreibt_Tags_die_der_Reader_wieder_liest(string testFile)
    {
        var path = _library.Copy(testFile, _library.MusicRoot, testFile);
        var before = _reader.Read(path).Tags;

        _writer.Write(path, Track(), null);

        var tags = _reader.Read(path).Tags;
        Assert.Equal("A Victory of Love", tags.Title);
        Assert.Equal(["Alphaville"], tags.Artists);
        Assert.Equal(("Alphaville", "Forever Young", 1984), (tags.AlbumArtist, tags.Album, tags.Year));
        Assert.Equal((1, 1, 1), (tags.Disc, tags.DiscCount, tags.Track));
        Assert.Equal(Track().RecordingId, tags.MusicBrainzRecordingId);
        Assert.Equal(Track().ReleaseId, tags.MusicBrainzReleaseId);
        Assert.Equal(Track().ArtistIds[0], tags.MusicBrainzArtistId);
        Assert.Equal(Track().AcoustId, tags.AcoustId);
        Assert.False(tags.IsCompilation);
        // Was Tonregal nicht kennt, bleibt stehen.
        Assert.Equal(before.Bpm, tags.Bpm);
        Assert.Equal(before.Genre, tags.Genre);
    }

    [Fact]
    public async Task MP3_in_WAV_Huelle_bleibt_nach_dem_Schreiben_fingerprintbar()
    {
        // Alte Ripper: ID3-Tag, dahinter RIFF/WAVE mit MP3-Daten (siehe FpcalcFingerprintService).
        var path = _library.Copy("song5-riff.mp3", _library.MusicRoot, "riff.mp3");
        var fpcalc = new Tonregal.Infrastructure.Fingerprinting.FpcalcFingerprintService(
            Microsoft.Extensions.Options.Options.Create(new Tonregal.Application.MetadataOptions()), _reader);
        var before = await fpcalc.ComputeAsync(path, TestContext.Current.CancellationToken);

        _writer.Write(path, Track(), null);

        var after = await fpcalc.ComputeAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(before.Value, after.Value);
    }

    [Fact]
    public void Compilation_Flag_und_Cover_nur_wenn_noch_keins_da_ist()
    {
        var flac = _library.Copy("song2.flac", _library.MusicRoot, "song2.flac");
        var mp3 = _library.Copy("song1.mp3", _library.MusicRoot, "song1.mp3");
        var embedded = _reader.ReadCover(mp3)!;
        var cover = new EmbeddedPicture(File.ReadAllBytes(Path.Combine(TestLibrary.TestData, "cover.jpg")), "image/jpeg");

        _writer.Write(flac, Track(AlbumKind.Compilation), cover);
        _writer.Write(mp3, Track(), new EmbeddedPicture([1, 2, 3], "image/png"));

        Assert.True(_reader.Read(flac).Tags.IsCompilation);
        Assert.Equal(cover.Data, _reader.ReadCover(flac)!.Data);
        Assert.Equal(embedded.Data, _reader.ReadCover(mp3)!.Data);
    }
}
