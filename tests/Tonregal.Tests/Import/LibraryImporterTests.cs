// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Domain.ValueObjects;
using Tonregal.Infrastructure.Import;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;
using Tonregal.Tests.Library;

namespace Tonregal.Tests.Import;

public sealed class LibraryImporterTests : IDisposable
{
    private const string AlbumFolder = "Alphaville/1984 - Forever Young";

    private readonly TestLibrary _library = new(populate: false);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TonregalDbContext _db;
    private readonly TagLibTagReader _reader = new();
    private readonly FakeCovers _covers = new();
    private ReleaseTrack _track = TagLibTagWriterTests.Track();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Inbox => Path.Combine(_library.MusicRoot, "Inbox");
    private TonregalOptions Options => new() { MusicRoot = _library.MusicRoot };

    public LibraryImporterTests()
    {
        _connection.Open();
        _db = new TonregalDbContext(new DbContextOptionsBuilder<TonregalDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _library.Dispose();
    }

    private LibraryImporter Importer() => new(
        _db, _reader, new TagLibTagWriter(), new FakeLookup(() => _track), _covers,
        Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System, NullLogger<LibraryImporter>.Instance);

    private async Task<ImportJob> MatchedJobAsync(string testFile, string relativePath)
    {
        var target = Path.Combine(Inbox, relativePath);
        _library.Copy(testFile, Path.GetDirectoryName(target)!, Path.GetFileName(target));
        var candidate = new RecordingCandidate(
            _track.RecordingId, Guid.Parse("22222222-0000-0000-0000-000000000001"), _track.Title, _track.Artists, 2000,
            _track.ReleaseId, _track.ReleaseGroupId, _track.Album, _track.AlbumArtist, "Album", "Official", 1984,
            1, 1, 10, "1984-09-27", "DE", "CD", 0.98);
        var job = new ImportJob
        {
            SourcePath = relativePath,
            State = ImportState.Matched,
            SelectedRecordingId = candidate.RecordingId,
            SelectedReleaseId = candidate.ReleaseId,
            Candidates = JsonSerializer.Serialize(new[] { candidate }, InboxImporter.Json),
        };
        _db.ImportJobs.Add(job);
        await _db.SaveChangesAsync(Ct);
        return job;
    }

    [Fact]
    public async Task Uebernimmt_Datei_mit_Tags_und_Cover_und_raeumt_die_Inbox_auf()
    {
        var job = await MatchedJobAsync("song1.mp3", "Forever Young/01.mp3");
        File.WriteAllText(Path.Combine(Inbox, "Forever Young", ".DS_Store"), "");

        var result = await Importer().ImportAsync(job.Id, Ct);

        Assert.Null(result.Error);
        Assert.Equal(ImportState.Imported, result.State);
        Assert.Equal($"{AlbumFolder}/01 - A Victory of Love.mp3", result.LibraryPath);
        var target = Path.Combine(_library.Library, result.LibraryPath!);
        var tags = _reader.Read(target).Tags;
        Assert.Equal(("A Victory of Love", "Forever Young", _track.RecordingId), (tags.Title, tags.Album, tags.MusicBrainzRecordingId));
        Assert.Equal(Guid.Parse("22222222-0000-0000-0000-000000000001"), tags.AcoustId);
        // song1.mp3 hat ein eingebettetes Cover; das landet als cover.jpg im Albumordner.
        Assert.Equal(_reader.ReadCover(target)!.Data, File.ReadAllBytes(Path.Combine(_library.Library, AlbumFolder, "cover.jpg")));
        Assert.Equal(0, _covers.Calls);
        Assert.False(Directory.Exists(Path.Combine(Inbox, "Forever Young")));
        Assert.True(Directory.Exists(Inbox));
        Assert.Single(Directory.GetFiles(Path.Combine(_library.Library, AlbumFolder), "*.mp3"));
    }

    [Fact]
    public async Task Holt_fehlendes_Cover_aus_dem_Cover_Art_Archive()
    {
        var job = await MatchedJobAsync("song2.flac", "lose.flac");

        var result = await Importer().ImportAsync(job.Id, Ct);

        var target = Path.Combine(_library.Library, result.LibraryPath!);
        Assert.Equal(1, _covers.Calls);
        Assert.Equal(FakeCovers.Cover.Data, _reader.ReadCover(target)!.Data);
        Assert.True(File.Exists(Path.Combine(_library.Library, AlbumFolder, "cover.jpg")));
        Assert.False(File.Exists(Path.Combine(Inbox, "lose.flac")));
    }

    [Fact]
    public async Task Ohne_Cover_Art_Archive_wird_trotzdem_uebernommen()
    {
        _covers.Unavailable = true;
        var job = await MatchedJobAsync("song2.flac", "lose.flac");

        var result = await Importer().ImportAsync(job.Id, Ct);

        Assert.Equal(ImportState.Imported, result.State);
        Assert.Null(_reader.ReadCover(Path.Combine(_library.Library, result.LibraryPath!)));
    }

    [Fact]
    public async Task Belegtes_Ziel_laesst_die_Inbox_Datei_unveraendert()
    {
        var job = await MatchedJobAsync("song1.mp3", "01.mp3");
        var source = Path.Combine(Inbox, "01.mp3");
        var before = File.ReadAllBytes(source);
        _library.Copy("song3.m4a", Path.Combine(_library.Library, AlbumFolder), "01 - A Victory of Love.mp3");

        var result = await Importer().ImportAsync(job.Id, Ct);

        Assert.Equal(ImportState.Matched, result.State);
        Assert.Equal($"In der Library liegt schon {AlbumFolder}/01 - A Victory of Love.mp3", result.Error);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_library.Library, AlbumFolder)), f => Path.GetFileName(f).StartsWith('.'));
    }

    [Fact]
    public async Task Nur_erkannte_Jobs_lassen_sich_uebernehmen()
    {
        var job = await MatchedJobAsync("song1.mp3", "01.mp3");
        job.State = ImportState.NeedsReview;
        await _db.SaveChangesAsync(Ct);

        var result = await Importer().ImportAsync(job.Id, Ct);

        Assert.Equal(ImportState.NeedsReview, result.State);
        Assert.NotNull(result.Error);
        Assert.True(File.Exists(Path.Combine(Inbox, "01.mp3")));
    }

    [Fact]
    public async Task Compilation_landet_unter_Various_Artists_und_der_Scan_verknuepft_den_Job()
    {
        _track = TagLibTagWriterTests.Track(AlbumKind.Compilation) with { Disc = 2, DiscCount = 2, Track = 3 };
        var job = await MatchedJobAsync("song4.opus", "Sampler/03.opus");

        var result = await Importer().ImportAsync(job.Id, Ct);
        await new LibraryScanner(_db, _reader, Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System, NullLogger<LibraryScanner>.Instance)
            .ScanAsync(null, Ct);

        Assert.Equal("Various Artists/1984 - Forever Young/2-03 - A Victory of Love.opus", result.LibraryPath);
        var file = await _db.AudioFiles.SingleAsync(Ct);
        Assert.Equal(file.Id, (await _db.ImportJobs.AsNoTracking().SingleAsync(Ct)).AudioFileId);
        Assert.Equal(FileSource.Inbox, file.Source);
        Assert.Equal(AlbumKind.Compilation, (await _db.Albums.SingleAsync(Ct)).Kind);
    }

    private sealed class FakeLookup(Func<ReleaseTrack> track) : IMetadataLookup
    {
        public Task<IReadOnlyList<RecordingCandidate>> LookupAsync(Fingerprint fingerprint, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ReleaseTrack?> GetReleaseTrackAsync(Guid releaseId, Guid recordingId, CancellationToken ct) =>
            Task.FromResult<ReleaseTrack?>(track());
    }

    private sealed class FakeCovers : ICoverArtSource
    {
        public static readonly EmbeddedPicture Cover = new(File.ReadAllBytes(Path.Combine(TestLibrary.TestData, "cover.jpg")), "image/jpeg");
        public int Calls { get; private set; }
        public bool Unavailable { get; set; }

        public Task<EmbeddedPicture?> GetFrontCoverAsync(Guid releaseId, CancellationToken ct)
        {
            Calls++;
            return Unavailable
                ? throw new ExternalToolUnavailableException("Cover Art Archive: HTTP 503")
                : Task.FromResult<EmbeddedPicture?>(Cover);
        }
    }
}
