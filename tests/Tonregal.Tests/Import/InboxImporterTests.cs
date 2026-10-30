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
            "mbid" => [Candidate("Ganz anderer Song", 0.95)],
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

    [Theory]
    [InlineData("Titel 12")]
    [InlineData("Track 03")]
    [InlineData("07")]
    [InlineData("Vts 03 1")]
    public void Platzhalter_Titel_zaehlt_als_fehlender_Titel_und_der_Interpret_muss_passen(string title)
    {
        var job = new ImportJob { SourcePath = "x.mp3", DurationMs = 2000 };
        var tags = new AudioTags { Title = title, Artists = ["Evanescence"] };

        InboxImporter.Decide(job, tags, [Candidate("Swingin' Shepherd Blues", 1.0, artist: "Deborah Henson-Conant")], 0.9);
        Assert.Equal(ImportState.NeedsReview, job.State);
        Assert.Equal("Interpret passt zu keinem Treffer: „Evanescence“", job.ReviewReason);

        InboxImporter.Decide(job, tags, [Candidate("Whisper", 1.0, artist: "Evanescence")], 0.9);
        Assert.Equal(ImportState.Matched, job.State);
    }

    [Fact]
    public void Treffer_auf_einem_Release_geht_vor_einer_Aufnahme_ohne_Release()
    {
        var job = new ImportJob { SourcePath = "x.mp3", DurationMs = 2000 };
        var tags = new AudioTags { Title = "Find a Way", Artists = ["Testband"] };
        var release = Guid.NewGuid();

        InboxImporter.Decide(job, tags, [Candidate("Find a Way", 1.0) with { ReleaseId = null }, Candidate("Find a Way", 0.98, release)], 0.9);

        Assert.Equal(ImportState.Matched, job.State);
        Assert.Equal(release, job.SelectedReleaseId);
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
    public async Task Fremde_Recording_ID_mit_passendem_Titel_gilt_als_Treffer()
    {
        // song2.flac trägt die Recording-ID 6f0e1a5c-…, AcoustID kennt die Aufnahme unter einer anderen.
        Drop("song2.flac", "02.flac");
        var fingerprint = Guid.NewGuid();
        _lookup.Results["02.flac"] = [Candidate("Zweiter Song", 0.95, ReleaseA, fingerprint)];

        var job = Assert.Single(await ProcessAllAsync());

        Assert.Equal(ImportState.Matched, job.State);
        Assert.Null(job.ReviewReason);
        Assert.Equal(fingerprint, job.SelectedRecordingId);
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

    [Fact]
    public async Task Byte_gleiche_Datei_aus_der_Library_wandert_in_den_Papierkorb()
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(TestLibrary.TestData, "song1.mp3"))));
        var album = new Album { Title = "Testalbum", AlbumArtist = new Artist { Name = "Testband", SortName = "Testband" } };
        _db.AudioFiles.Add(new AudioFile { Path = "Testband/2024 - Testalbum/01.mp3", FileHash = hash, Track = new Track { Title = "Erster Song", Album = album } });
        await _db.SaveChangesAsync(Ct);
        Drop("song1.mp3", "Doppelt/01.mp3");
        Drop("song2.flac", "Neu/02.flac");

        var jobs = await ProcessAllAsync();

        Assert.Equal((ImportState.Rejected, "Liegt schon identisch in der Library: Testband/2024 - Testalbum/01.mp3"), (jobs[0].State, jobs[0].Error));
        Assert.NotEqual(ImportState.Rejected, jobs[1].State);
        Assert.False(Directory.Exists(Path.Combine(Inbox, "Doppelt")));
        var trashed = Directory.GetFiles(Path.Combine(_library.Library, ".trash"), "*.mp3", SearchOption.AllDirectories);
        Assert.EndsWith(Path.Combine("Inbox", "Doppelt", "01.mp3"), Assert.Single(trashed));
        Assert.Equal(new InboxSyncResult(0, 0, 0, 0), await Importer().SyncAsync(Ct));
    }

    [Fact]
    public async Task Altbestand_wird_eingelesen_aber_nie_veraendert()
    {
        var legacy = Path.Combine(_library.MusicRoot, "Altbestand");
        _library.Copy("song1.mp3", Path.Combine(legacy, "Band", "Album"), "01.mp3");
        _library.Copy("song2.flac", Path.Combine(legacy, "Band", "Album"), "02.flac");
        Drop("song3.m4a", "Neu/03.m4a");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(TestLibrary.TestData, "song1.mp3"))));
        var album = new Album { Title = "Testalbum", AlbumArtist = new Artist { Name = "Testband", SortName = "Testband" } };
        _db.AudioFiles.Add(new AudioFile { Path = "Testband/2024 - Testalbum/01.mp3", FileHash = hash, Track = new Track { Title = "Erster Song", Album = album } });
        await _db.SaveChangesAsync(Ct);

        var importer = Importer();
        Assert.Equal(new InboxSyncResult(2, 0, 0, 0), await importer.SyncLegacyAsync(Ct));
        await importer.SyncAsync(Ct);

        // Die Inbox geht vor, auch wenn ihr Job jünger ist.
        Assert.Equal(FileSource.Inbox, (await importer.ProcessNextAsync(Ct))!.Source);
        while (await importer.ProcessNextAsync(Ct) is not null)
        {
        }

        var jobs = await _db.ImportJobs.AsNoTracking().Where(j => j.Source == FileSource.Legacy).OrderBy(j => j.SourcePath).ToListAsync(Ct);
        Assert.Equal(["Band/Album/01.mp3", "Band/Album/02.flac"], jobs.Select(j => j.SourcePath));
        Assert.Equal(ImportState.Rejected, jobs[0].State);
        Assert.StartsWith(ImportJob.AlreadyInLibrary, jobs[0].Error);
        // Schon vorhanden: bleibt trotzdem im Altbestand liegen, nichts im Papierkorb.
        Assert.True(File.Exists(Path.Combine(legacy, "Band", "Album", "01.mp3")));
        Assert.False(Directory.Exists(Path.Combine(_library.Library, ".trash")));

        // Unveränderte Dateien bekommen keinen neuen Job, auch nicht nach dem Ablehnen.
        Assert.Equal(new InboxSyncResult(0, 0, 0, 0), await importer.SyncLegacyAsync(Ct));
    }

    [Theory]
    [InlineData("Garbage - Use Me.mp3", null, null, "Use Me", "Garbage")]
    [InlineData("Garbage - Use Me.mp3", "Titel 3", "Garbage", "Use Me", "Garbage")]
    [InlineData("03 - Song.mp3", null, "Band", "Song", "Band")]
    [InlineData("03 Song.mp3", null, "Band", "Song", "Band")]
    [InlineData("13 x Forever.mp3", null, "Garbage", "13 x Forever", "Garbage")]
    [InlineData("Teil 1 - Teil 2.mp3", null, "Band", "Teil 1 - Teil 2", "Band")]
    [InlineData("Garbage - Use Me.mp3", "Echter Titel", "Garbage", "Echter Titel", "Garbage")]
    [InlineData("Track 07.mp3", null, "Band", null, "Band")]
    [InlineData("Nightwish - Vts 03 1.mp3", "Vts 03 1", "Nightwish", "Vts 03 1", "Nightwish")]
    public void Titel_und_Artist_aus_dem_Dateinamen(string file, string? title, string? artist, string? expectedTitle, string? expectedArtist)
    {
        var tags = InboxImporter.WithFileName(new AudioTags { Title = title, Artists = artist is null ? [] : [artist] }, $"/inbox/{file}");

        Assert.Equal(expectedTitle, tags.Title);
        Assert.Equal(expectedArtist, tags.Artists.FirstOrDefault());
    }

    [Fact]
    public void Etwas_niedrigerer_Score_reicht_wenn_die_Tags_passen()
    {
        var job = new ImportJob { SourcePath = "x.mp3", DurationMs = 2000 };
        var tags = new AudioTags { Title = "Good Enough", Artists = ["Evanescence"] };

        InboxImporter.Decide(job, tags, [Candidate("Good Enough", 0.85, artist: "Evanescence")], 0.9);
        Assert.Equal(ImportState.Matched, job.State);

        InboxImporter.Decide(job, tags, [Candidate("Something Else", 0.85, artist: "Evanescence")], 0.9);
        Assert.Equal("Score 85 % unter 90 %", job.ReviewReason?.Replace(' ', ' '));
    }

    [Fact]
    public void Treffer_fremder_Interpreten_zaehlen_nicht_wenn_der_Interpret_aus_den_Tags_dabei_ist()
    {
        var job = new ImportJob { SourcePath = "x.mp3", DurationMs = 2000 };

        InboxImporter.Decide(job, new AudioTags { Title = "Thirteen", Artists = ["Garbage"] },
            [Candidate("Thirteen", 1.0, artist: "Elliott Smith"), Candidate("Thirteen", 1.0, artist: "Garbage")], 0.9);
        Assert.Equal(ImportState.Matched, job.State);

        InboxImporter.Decide(job, new AudioTags { Artists = ["Evanescence"] },
            [Candidate("Swingin' Shepherd Blues", 1.0, artist: "Deborah Henson-Conant"), Candidate("My Immortal (band version)", 1.0, artist: "Evanescence")], 0.9);
        Assert.Equal(ImportState.Matched, job.State);
    }

    [Fact]
    public async Task Ohne_AcoustID_Treffer_oder_Fingerprint_kommt_ein_Vorschlag_aus_den_Tags()
    {
        // song1.mp3: Testband – Erster Song
        Drop("song1.mp3", "Album/01.mp3");
        Drop("song1.mp3", "Album/kaputt.mp3");
        File.AppendAllText(Path.Combine(Inbox, "Album", "kaputt.mp3"), "x");
        var found = Candidate("Erster Song", 1.0);
        _lookup.Search["Testband|Erster Song"] = [Candidate("Erster Song", 1.0, durationMs: 300_000), found];

        var jobs = await ProcessAllAsync();

        Assert.All(jobs, job =>
        {
            Assert.Equal(ImportState.NeedsReview, job.State);
            Assert.Equal(found.RecordingId, job.SelectedRecordingId);
            Assert.Null(job.MatchScore);
            Assert.Null(job.Error);
        });
        Assert.Equal("AcoustID kennt diese Aufnahme nicht; Vorschlag nur über die Tags gefunden", jobs[0].ReviewReason);
        Assert.Equal("Kein Fingerprint (fpcalc konnte die Datei nicht lesen); Vorschlag nur über die Tags gefunden", jobs[1].ReviewReason);
    }

    [Fact]
    public async Task Vorschlag_aus_den_Tags_nimmt_das_Album_aus_den_Tags_und_braucht_die_passende_Laenge()
    {
        // song1.mp3: Testband – Erster Song · Testalbum, etwa 2 s
        Drop("song1.mp3", "A/01.mp3");
        var otherAlbum = Candidate("Erster Song", 1.0) with { ReleaseTitle = "Live" };
        var album = Candidate("Erster Song", 1.0, durationMs: 9_000);
        _lookup.Search["Testband|Erster Song"] = [otherAlbum, album];

        var job = Assert.Single(await ProcessAllAsync());
        Assert.Equal(album.ReleaseId, job.SelectedReleaseId);

        // Exakter Titel vor Demo oder Remaster, auch wenn die Demo auf dem Album selbst steht.
        _db.ImportJobs.RemoveRange(_db.ImportJobs);
        await _db.SaveChangesAsync(Ct);
        var demo = Candidate("Erster Song (demo)", 1.0);
        var deluxe = Candidate("Erster Song", 1.0) with { ReleaseTitle = "Testalbum (Deluxe)" };
        _lookup.Search["Testband|Erster Song"] = [demo, deluxe];
        job = Assert.Single(await ProcessAllAsync());
        Assert.Equal(deluxe.ReleaseId, job.SelectedReleaseId);

        // Viel zu lang (oder ohne Länge bei MusicBrainz): kein Vorschlag, nur die Liste.
        _db.ImportJobs.RemoveRange(_db.ImportJobs);
        await _db.SaveChangesAsync(Ct);
        _lookup.Search["Testband|Erster Song"] = [Candidate("Erster Song", 1.0, durationMs: 600_000), otherAlbum with { DurationMs = null }];
        job = Assert.Single(await ProcessAllAsync());
        Assert.Null(job.SelectedRecordingId);
        Assert.Matches(@"^AcoustID kennt diese Aufnahme nicht; Dauer 0:0\d passt zu keinem Treffer über die Tags$", job.ReviewReason);
    }

    [Theory]
    [InlineData("A Victory Of Love", "A Victory of Love", true)]
    [InlineData("Forever Young (Remastered 2019)", "Forever Young", true)]
    [InlineData("Looking For The Summer", "Lookin' for the Summer", true)]
    [InlineData("Sounds Like a Melody", "Big in Japan", false)]
    [InlineData("Talking over me", "Taking Over Me", true)]
    [InlineData("Hello", "Hallo", false)]
    [InlineData("My Immortal", "My Last Breath", false)]
    [InlineData("Breaking Up Girl", "Breaking Up the Girl", true)]
    [InlineData("Punch And Judy", "Punch & Judy", true)]
    [InlineData("13 x Forever", "13× Forever", true)]
    public void Titelvergleich(string a, string b, bool same) =>
        Assert.Equal(same, InboxImporter.SameTitle(a, b));

    private sealed class FakeFingerprints : IFingerprintService
    {
        public Task<Fingerprint> ComputeAsync(string path, CancellationToken ct) =>
            Path.GetFileName(path).Contains("kaputt")
                ? throw new InvalidDataException("fpcalc konnte kaputt.mp3 nicht lesen: Empty fingerprint")
                : Task.FromResult(new Fingerprint(Path.GetFileName(path), TimeSpan.FromSeconds(3)));

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

        public Task<ReleaseTrack?> GetReleaseTrackAsync(Guid releaseId, Guid recordingId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Guid?> GetReleaseGroupIdAsync(Guid releaseId, CancellationToken ct) => throw new NotSupportedException();

        /// <summary>Suchergebnisse je „Artist|Titel“.</summary>
        public Dictionary<string, IReadOnlyList<RecordingCandidate>> Search { get; } = [];

        public Task<IReadOnlyList<RecordingCandidate>> SearchByTagsAsync(string artist, string title, string? album, int durationMs, CancellationToken ct) =>
            Task.FromResult(Search.GetValueOrDefault($"{artist}|{title}") ?? []);
    }
}
