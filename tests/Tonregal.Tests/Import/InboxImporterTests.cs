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
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Tests.Import;

public sealed class InboxImporterTests : IDisposable
{
    private static readonly Guid ReleaseA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid ReleaseB = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000b");
    private static readonly Guid ReleaseC = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000c");

    private readonly TestLibrary _library = new(populate: false);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TonregalDbContext _db;
    private readonly FakeLookup _lookup = new();
    private readonly InboxFileTracker _tracker = new(TimeProvider.System);
    private int _stableSeconds;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Inbox => Path.Combine(_library.MusicRoot, "Inbox");

    public InboxImporterTests()
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

    private InboxImporter Importer() => new(
        _db, new TagLibTagReader(), new FakeFingerprints(), _lookup, _tracker,
        Options.Create(new TonregalOptions { MusicRoot = _library.MusicRoot, InboxStableSeconds = _stableSeconds }),
        Options.Create(new MetadataOptions()), TimeProvider.System, NullLogger<InboxImporter>.Instance);

    /// <summary>Legt eine Testdatei in die Inbox; der Fingerprint der Attrappe ist der Dateiname.</summary>
    private string Drop(string testFile, string relativePath)
    {
        var target = Path.Combine(Inbox, relativePath);
        return _library.Copy(testFile, Path.GetDirectoryName(target)!, Path.GetFileName(target));
    }

    private async Task<List<ImportJob>> ProcessAllAsync()
    {
        var importer = Importer();
        await importer.SyncAsync(Ct);
        while (await importer.ProcessNextAsync(Ct) is not null)
        {
        }

        return await _db.ImportJobs.AsNoTracking().OrderBy(j => j.SourcePath).ToListAsync(Ct);
    }

    private static RecordingCandidate Candidate(
        string title, double score, Guid? release = null, Guid? recording = null, string artist = "Testband", int durationMs = 2000) =>
        new(recording ?? Guid.NewGuid(), Guid.NewGuid(), title, [artist], durationMs,
            release ?? Guid.NewGuid(), Guid.NewGuid(), "Testalbum", artist, "Album", "Official", 2024,
            1, 1, 3, "2024-03-01", "DE", "CD", score);

    [Fact]
    public async Task Sync_legt_Jobs_an_und_ignoriert_Ressourcendateien()
    {
        Drop("song1.mp3", "Album/01.mp3");
        Drop("song2.flac", "lose.flac");
        Drop("song1.mp3", "Album/._01.mp3");
        Drop("song1.mp3", ".versteckt/x.mp3");
        Drop("song1.mp3", "_api/tonwerk-ai/x.mp3");
        File.WriteAllText(Path.Combine(Inbox, "Album", "notizen.txt"), "");

        var result = await Importer().SyncAsync(Ct);
        var again = await Importer().SyncAsync(Ct);

        Assert.Equal(new InboxSyncResult(2, 0, 0, 0), result);
        Assert.Equal(new InboxSyncResult(0, 0, 0, 0), again);
        var jobs = await _db.ImportJobs.OrderBy(j => j.SourcePath).ToListAsync(Ct);
        Assert.Equal(["Album/01.mp3", "lose.flac"], jobs.Select(j => j.SourcePath));
        Assert.All(jobs, j => Assert.Equal(ImportState.New, j.State));
        Assert.Equal("Album", jobs[0].Group);
        Assert.Equal("", jobs[1].Group);
    }

    [Fact]
    public async Task Sync_wartet_bis_die_Dateigroesse_stabil_ist()
    {
        _stableSeconds = 3600;
        Drop("song1.mp3", "01.mp3");

        Assert.Equal(new InboxSyncResult(0, 0, 0, 1), await Importer().SyncAsync(Ct));
        Assert.Equal(new InboxSyncResult(0, 0, 0, 1), await Importer().SyncAsync(Ct));
        Assert.Empty(_db.ImportJobs);
    }

    [Fact]
    public async Task Sicherer_eindeutiger_Treffer_wird_Matched()
    {
        Drop("song1.mp3", "01.mp3");
        var best = Candidate("Erster Song", 0.97, ReleaseA);
        _lookup.Results["01.mp3"] = [best, Candidate("Erster Song", 0.97, ReleaseB, best.RecordingId)];

        var job = Assert.Single(await ProcessAllAsync());

        Assert.Equal(ImportState.Matched, job.State);
        Assert.Null(job.ReviewReason);
        Assert.Equal(0.97, job.MatchScore);
        Assert.Equal((best.RecordingId, ReleaseA), (job.SelectedRecordingId, job.SelectedReleaseId));
        Assert.Equal("01.mp3", job.Fingerprint);
        Assert.Equal(("Erster Song", "Testband", "Testalbum"), (job.TagTitle, job.TagArtist, job.TagAlbum));
        Assert.Equal(AudioFormat.Mp3, job.Format);
        Assert.Equal(64, job.FileHash!.Length);
        Assert.Equal(2, JsonSerializer.Deserialize<List<RecordingCandidate>>(job.Candidates!, InboxImporter.Json)!.Count);
    }

    [Theory]
    [InlineData("niedrig", "Score 72 % unter 90 %")]
    [InlineData("leer", "AcoustID kennt diese Aufnahme nicht")]
    [InlineData("titel", "Tags passen zu keinem Treffer: „Erster Song“")]
    [InlineData("dauer", "Dauer 0:03 passt zu keinem Treffer")]
    [InlineData("mbid", "MusicBrainz-ID in den Tags passt nicht zum Fingerprint")]
    public async Task Unsicherer_Treffer_braucht_Review(string fall, string reason)
    {
        // song2.flac trägt eine MusicBrainz-Recording-ID, song1.mp3 nicht.
        var file = fall == "mbid" ? "song2.flac" : "song1.mp3";
        Drop(file, file);
        _lookup.Results[file] = fall switch
        {
            "niedrig" => [Candidate("Erster Song", 0.72)],
            "leer" => [],
            "dauer" => [Candidate("Erster Song", 0.95, durationMs: 240_000)],
            "mbid" => [Candidate("Zweiter Song", 0.95)],
            _ => [Candidate("Ganz anderer Song", 0.95)],
        };

        var job = Assert.Single(await ProcessAllAsync());

        Assert.Equal(ImportState.NeedsReview, job.State);
        Assert.Equal(reason, job.ReviewReason?.Replace(' ', ' '));
        if (fall != "leer")
            Assert.NotNull(job.SelectedRecordingId);
    }

    [Fact]
    public async Task Tags_entscheiden_wenn_AcoustID_fremde_Songs_mitliefert()
    {
        // AcoustID verknüpft manche Fingerprints auch mit falschen Recordings („Eddie My Love“).
        Drop("song1.mp3", "01.mp3");
        _lookup.Results["01.mp3"] =
        [
            Candidate("Eddie My Love", 0.98, ReleaseA, artist: "The Chordettes"),
            Candidate("Erster Song", 0.98, ReleaseB),
        ];

        var job = Assert.Single(await ProcessAllAsync());

        Assert.Equal(ImportState.Matched, job.State);
        Assert.Equal(ReleaseB, job.SelectedReleaseId);
    }

    [Fact]
    public void Ohne_Titel_in_den_Tags_muss_der_Fingerprint_eindeutig_sein()
    {
        var job = new ImportJob { SourcePath = "x.mp3", DurationMs = 2000 };

        InboxImporter.Decide(job, new AudioTags(), [Candidate("Erster Song", 0.95), Candidate("Ganz anderer Song", 0.93)], 0.9);

        Assert.Equal(ImportState.NeedsReview, job.State);
        Assert.Equal("Fingerprint passt auch zu „Ganz anderer Song“ von Testband", job.ReviewReason);
    }

    [Fact]
    public async Task Andere_Fassung_derselben_Aufnahme_ist_kein_Widerspruch()
    {
        Drop("song1.mp3", "01.mp3");
        _lookup.Results["01.mp3"] =
        [
            Candidate("Erster Song", 0.98, ReleaseA),
            Candidate("Erster Song (single edit)", 0.97, ReleaseB),
            Candidate("Erster Song", 0.96, ReleaseC, artist: "Testband & Gast"),
        ];

        var job = Assert.Single(await ProcessAllAsync());

        Assert.Equal(ImportState.Matched, job.State);
        Assert.Equal(ReleaseA, job.SelectedReleaseId);
    }

    [Fact]
    public async Task MusicBrainz_IDs_aus_den_Tags_haben_Vorrang()
    {
        // song2.flac trägt die Recording-ID 6f0e1a5c-….
        Drop("song2.flac", "02.flac");
        var tagged = Guid.Parse("6f0e1a5c-6a43-4f4e-9b5c-2f6c2b2f1a11");
        _lookup.Results["02.flac"] = [Candidate("Zweiter Song", 0.97, ReleaseA), Candidate("Zweiter Song", 0.96, ReleaseB, tagged)];

        var job = Assert.Single(await ProcessAllAsync());

        Assert.Equal(ImportState.Matched, job.State);
        Assert.Equal((tagged, ReleaseB), (job.SelectedRecordingId, job.SelectedReleaseId));
    }

    [Fact]
    public async Task Ordner_bekommt_das_Release_der_Mehrheit()
    {
        Drop("song1.mp3", "Album/01.mp3");
        Drop("song1.mp3", "Album/02.mp3");
        Drop("song1.mp3", "Album/03.mp3");
        _lookup.Results["01.mp3"] = [Candidate("Erster Song", 0.95, ReleaseA), Candidate("Erster Song", 0.95, ReleaseB)];
        _lookup.Results["02.mp3"] = [Candidate("Erster Song", 0.95, ReleaseC), Candidate("Erster Song", 0.95, ReleaseB)];
        _lookup.Results["03.mp3"] = [Candidate("Erster Song", 0.95, ReleaseB)];

        var jobs = await ProcessAllAsync();

        Assert.All(jobs, j => Assert.Equal(ImportState.Matched, j.State));
        Assert.All(jobs, j => Assert.Equal(ReleaseB, j.SelectedReleaseId));
    }

    [Fact]
    public async Task Ohne_AcoustID_bleibt_der_Job_offen()
    {
        Drop("song1.mp3", "01.mp3");
        _lookup.Unavailable = true;
        var importer = Importer();
        await importer.SyncAsync(Ct);

        await Assert.ThrowsAsync<ExternalToolUnavailableException>(() => importer.ProcessNextAsync(Ct));
        var job = await _db.ImportJobs.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(ImportState.Fingerprinted, job.State);
        Assert.Equal("AcoustID-API-Key fehlt", job.Error);

        // Mit Key geht es beim nächsten Lauf ohne neuen Fingerprint weiter.
        _lookup.Unavailable = false;
        _lookup.Results["01.mp3"] = [Candidate("Erster Song", 0.97)];
        var jobs = await ProcessAllAsync();
        Assert.Equal(ImportState.Matched, Assert.Single(jobs).State);
        Assert.Null(jobs[0].Error);
    }

    [Fact]
    public async Task Unlesbare_Datei_braucht_Review()
    {
        Directory.CreateDirectory(Inbox);
        File.WriteAllText(Path.Combine(Inbox, "kaputt.mp3"), "kein mp3");

        var job = Assert.Single(await ProcessAllAsync());

        Assert.Equal(ImportState.NeedsReview, job.State);
        Assert.Equal("Datei nicht lesbar", job.ReviewReason);
        Assert.NotNull(job.Error);
    }

    [Fact]
    public async Task Geaenderte_Datei_beginnt_von_vorn_verschwundene_wird_verworfen()
    {
        var changed = Drop("song2.flac", "02.flac");
        var removed = Drop("song1.mp3", "01.mp3");
        _lookup.Results["02.flac"] = [Candidate("Zweiter Song", 0.97)];
        _lookup.Results["01.mp3"] = [Candidate("Erster Song", 0.97)];
        await ProcessAllAsync();

        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
        File.Delete(removed);
        var result = await Importer().SyncAsync(Ct);

        Assert.Equal(new InboxSyncResult(0, 1, 1, 0), result);
        var jobs = await _db.ImportJobs.AsNoTracking().OrderBy(j => j.SourcePath).ToListAsync(Ct);
        Assert.Equal((ImportState.Rejected, "Datei liegt nicht mehr in der Inbox"), (jobs[0].State, jobs[0].Error));
        Assert.Equal(ImportState.New, jobs[1].State);
        Assert.Null(jobs[1].Fingerprint);
    }

    [Theory]
    [InlineData("A Victory Of Love", "A Victory of Love", true)]
    [InlineData("Forever Young (Remastered 2019)", "Forever Young", true)]
    [InlineData("Looking For The Summer", "Lookin' for the Summer", true)]
    [InlineData("Sounds Like a Melody", "Big in Japan", false)]
    public void Titelvergleich(string a, string b, bool same) =>
        Assert.Equal(same, InboxImporter.SameTitle(a, b));

    private sealed class FakeFingerprints : IFingerprintService
    {
        public Task<Fingerprint> ComputeAsync(string path, CancellationToken ct) =>
            Task.FromResult(new Fingerprint(Path.GetFileName(path), TimeSpan.FromSeconds(3)));

        public Task<string?> GetVersionAsync(CancellationToken ct) => Task.FromResult<string?>("fake");
    }

    private sealed class FakeLookup : IMetadataLookup
    {
        public Dictionary<string, IReadOnlyList<RecordingCandidate>> Results { get; } = [];
        public bool Unavailable { get; set; }

        public Task<IReadOnlyList<RecordingCandidate>> LookupAsync(Fingerprint fingerprint, CancellationToken ct) =>
            Unavailable
                ? throw new ExternalToolUnavailableException("AcoustID-API-Key fehlt")
                : Task.FromResult(Results.GetValueOrDefault(fingerprint.Value) ?? []);
    }
}
