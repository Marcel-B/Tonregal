// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.ValueObjects;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;
using LibraryPathRule = Tonregal.Domain.ValueObjects.LibraryPath;

namespace Tonregal.Tests.Library;

public sealed class ReleaseAlignerTests : IDisposable
{
    private static readonly Guid Eu = TagLibTagWriterTests.Track().ReleaseId;
    private static readonly Guid Us = Guid.Parse("11111111-0000-0000-0000-0000000000a2");

    private readonly TestLibrary _library = new(populate: false);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TagLibTagReader _reader = new();
    private readonly Dictionary<(Guid Release, Guid Recording), ReleaseTrack> _releases = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TonregalOptions Options => new() { MusicRoot = _library.MusicRoot };

    public ReleaseAlignerTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.Migrate();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _library.Dispose();
    }

    private TonregalDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TonregalDbContext>().UseSqlite(_connection).Options);

    private ReleaseAligner Aligner(TonregalDbContext db) => new(
        db, _reader, new TagLibTagWriter(), new FakeLookup(_releases), Microsoft.Extensions.Options.Options.Create(Options),
        TimeProvider.System, NullLogger<ReleaseAligner>.Instance);

    /// <summary>Legt eine Datei mit den Tags des Tracks nach der Ordnerregel in die Library.</summary>
    private string Place(string testFile, ReleaseTrack track)
    {
        var relative = LibraryPathRule.For(
            track.AlbumArtist, track.Year, track.Album, track.Kind, track.Disc, track.DiscCount, track.Track, track.Title, Path.GetExtension(testFile));
        var path = Path.Combine(_library.Library, relative);
        _library.Copy(testFile, Path.GetDirectoryName(path)!, Path.GetFileName(path));
        new TagLibTagWriter().Write(path, track, null);
        return relative;
    }

    private async Task ScanAsync()
    {
        await using var db = CreateContext();
        await new LibraryScanner(db, _reader, Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System, NullLogger<LibraryScanner>.Instance)
            .ScanAsync(null, Ct);
    }

    [Fact]
    public async Task Bringt_alle_Tracks_auf_das_Release_der_Mehrheit()
    {
        var first = TagLibTagWriterTests.Track();
        var second = first with { RecordingId = Guid.NewGuid(), Title = "Song Two", Track = 2 };
        var third = first with { RecordingId = Guid.NewGuid(), Title = "Song Three", Track = 5, ReleaseId = Us, Year = null, Country = "US" };
        Place("song1.mp3", first);
        Place("song1.mp3", second);
        var stray = Place("song2.flac", third);
        var strayFolder = Path.GetDirectoryName(Path.Combine(_library.Library, stray))!;
        File.Copy(Path.Combine(TestLibrary.TestData, "cover.jpg"), Path.Combine(strayFolder, "cover.jpg"));
        _releases[(Eu, third.RecordingId)] = third with { ReleaseId = Eu, Year = 1984, Country = "DE", Track = 3 };
        await ScanAsync();

        await using var db = CreateContext();
        var album = await db.Albums.SingleAsync(Ct);
        Assert.Equal(2, await Aligner(db).CountReleasesAsync(album.Id, Ct));

        var result = await Aligner(db).AlignAsync(album.Id, Ct);

        Assert.Equal(new ReleaseAlignResult(Eu, 1, 0), result);
        var moved = Path.Combine(_library.Library, "Alphaville", "1984 - Forever Young", "03 - Song Three.flac");
        var tags = _reader.Read(moved).Tags;
        Assert.Equal((Eu, 3, 1984), (tags.MusicBrainzReleaseId, tags.Track, tags.Year));
        Assert.False(Directory.Exists(strayFolder));
        Assert.True(File.Exists(Path.Combine(_library.Library, "Alphaville", "1984 - Forever Young", "cover.jpg")));
        Assert.Contains(await db.AudioFiles.Select(f => f.Path).ToListAsync(Ct), p => p == "Alphaville/1984 - Forever Young/03 - Song Three.flac");
        Assert.Equal(1, await Aligner(db).CountReleasesAsync(album.Id, Ct));
    }

    [Fact]
    public async Task Ueberspringt_Aufnahmen_die_nicht_auf_dem_Release_stehen()
    {
        var first = TagLibTagWriterTests.Track();
        Place("song1.mp3", first);
        Place("song1.mp3", first with { RecordingId = Guid.NewGuid(), Title = "Song Two", Track = 2 });
        Place("song2.flac", first with { RecordingId = Guid.NewGuid(), Title = "Bonus", Track = 12, ReleaseId = Us });
        await ScanAsync();

        await using var db = CreateContext();
        var album = await db.Albums.SingleAsync(Ct);
        var result = await Aligner(db).AlignAsync(album.Id, Ct);

        Assert.Equal(new ReleaseAlignResult(Eu, 0, 1), result);
    }

    private sealed class FakeLookup(Dictionary<(Guid, Guid), ReleaseTrack> releases) : IMetadataLookup
    {
        public Task<IReadOnlyList<RecordingCandidate>> LookupAsync(Fingerprint fingerprint, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ReleaseTrack?> GetReleaseTrackAsync(Guid releaseId, Guid recordingId, CancellationToken ct) =>
            Task.FromResult(releases.GetValueOrDefault((releaseId, recordingId)));

        public Task<Guid?> GetReleaseGroupIdAsync(Guid releaseId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RecordingCandidate>> SearchByTagsAsync(string artist, string title, string? album, int durationMs, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
