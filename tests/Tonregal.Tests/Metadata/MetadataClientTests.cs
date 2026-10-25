// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Domain.ValueObjects;
using Tonregal.Infrastructure.Metadata;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Tests.Metadata;

public sealed class MetadataClientTests : IDisposable
{
    private static readonly Guid RecordingId = Guid.Parse("6f0e1a5c-6a43-4f4e-9b5c-2f6c2b2f1a11");
    private static readonly Fingerprint Fingerprint = new("AQADtEmUaEkS", TimeSpan.FromSeconds(2.04));

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TonregalDbContext _db;
    private readonly IOptions<MetadataOptions> _options = Options.Create(new MetadataOptions { AcoustIdApiKey = "test-key" });
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public MetadataClientTests()
    {
        _connection.Open();
        _db = new TonregalDbContext(new DbContextOptionsBuilder<TonregalDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private MusicBrainzClient MusicBrainz(StubHandler handler) =>
        new(handler.Client(MusicBrainzClient.BaseAddress), _db, _options, new NoDelayTimeProvider());

    private AcoustIdClient AcoustId(StubHandler handler, string? key = "test-key") =>
        new(handler.Client(AcoustIdClient.BaseAddress), Options.Create(new MetadataOptions { AcoustIdApiKey = key }), new NoDelayTimeProvider());

    [Fact]
    public async Task AcoustId_sendet_Fingerprint_per_POST_und_sortiert_nach_Score()
    {
        var handler = new StubHandler(_ => StubHandler.File("acoustid-lookup.json"));

        var matches = await AcoustId(handler).LookupAsync(Fingerprint, Ct);

        Assert.Equal([0.97, 0.42], matches.Select(m => m.Score));
        Assert.Equal([RecordingId], matches[0].RecordingIds);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.acoustid.org/v2/lookup", request.RequestUri!.ToString());
        Assert.Contains("client=test-key", body);
        Assert.Contains("duration=2", body);
        Assert.Contains("fingerprint=AQADtEmUaEkS", body);
    }

    [Fact]
    public async Task AcoustId_ohne_Key_oder_mit_Fehler()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"status":"error","error":{"code":4,"message":"invalid API key"}}""", HttpStatusCode.BadRequest));

        var missing = await Assert.ThrowsAsync<ExternalToolUnavailableException>(() => AcoustId(handler, key: null).LookupAsync(Fingerprint, Ct));
        var invalid = await Assert.ThrowsAsync<ExternalToolUnavailableException>(() => AcoustId(handler).LookupAsync(Fingerprint, Ct));

        Assert.Contains("API-Key fehlt", missing.Message);
        Assert.Contains("invalid API key", invalid.Message);
    }

    [Fact]
    public async Task MusicBrainz_liest_Recording_und_cacht_es()
    {
        var handler = new StubHandler(_ => StubHandler.File("musicbrainz-recording.json"));
        var client = MusicBrainz(handler);

        var recording = await client.GetRecordingAsync(RecordingId, Ct);
        var again = await client.GetRecordingAsync(RecordingId, Ct);

        Assert.NotNull(recording);
        Assert.Equal("Zweiter Song", recording.Title);
        Assert.Equal("Testband feat. Gast", recording!.ArtistCredit.Format());
        Assert.Equal(3, recording.Releases!.Count);
        Assert.Equal(2019, recording.Releases[0].Year);
        Assert.Equal(["Compilation"], recording.Releases[0].ReleaseGroup!.SecondaryTypes);
        Assert.Equal(5, recording.Releases[0].Media![0].Tracks![0].Position);
        Assert.Equal(recording.Title, again?.Title);
        var (request, _) = Assert.Single(handler.Requests);
        Assert.Equal(
            $"https://musicbrainz.org/ws/2/recording/{RecordingId}?inc=artist-credits+releases+release-groups+media&fmt=json",
            request.RequestUri!.ToString());
        Assert.Equal(1, await _db.MetadataCache.CountAsync(Ct));
    }

    [Fact]
    public async Task MusicBrainz_wiederholt_bei_503_und_liefert_null_bei_404()
    {
        var calls = 0;
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("recording")
            ? ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : StubHandler.File("musicbrainz-recording.json")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = MusicBrainz(handler);

        Assert.NotNull(await client.GetRecordingAsync(RecordingId, Ct));
        Assert.Equal(2, calls);
        Assert.Null(await client.GetReleaseAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Lookup_liefert_Kandidaten_mit_Studioalbum_zuerst()
    {
        var acoustId = new StubHandler(_ => StubHandler.File("acoustid-lookup.json"));
        var musicBrainz = new StubHandler(_ => StubHandler.File("musicbrainz-recording.json"));
        var lookup = new MetadataLookup(AcoustId(acoustId), MusicBrainz(musicBrainz), _options, NullLogger<MetadataLookup>.Instance);

        var candidates = await lookup.LookupAsync(Fingerprint, Ct);

        // Der zweite AcoustID-Treffer liegt unter MinimumScore und wird gar nicht nachgeschlagen.
        Assert.Single(musicBrainz.Requests);
        Assert.Equal(["Testalbum", "Zweiter Song", "Best of Testband"], candidates.Select(c => c.ReleaseTitle));
        var best = candidates[0];
        Assert.Equal(RecordingId, best.RecordingId);
        Assert.Equal(Guid.Parse("dddddddd-0000-0000-0000-000000000001"), best.AcoustId);
        Assert.Equal(["Testband", "Gast"], best.Artists);
        Assert.Equal("Testband", best.ReleaseArtist);
        Assert.Equal("Album", best.ReleaseType);
        Assert.Equal(2024, best.Year);
        Assert.Equal((1, 2, 3), (best.Disc, best.TrackNumber, best.TrackCount));
        Assert.Equal(("2024-03-01", "XE", "Digital Media"), (best.ReleaseDate, best.Country, best.MediumFormat));
        Assert.Equal(0.97, best.Score);
    }

    [Fact]
    public async Task Lookup_zeigt_jedes_Release_nur_einmal()
    {
        // Zwei Recordings derselben Aufnahme, die an denselben Releases hängen.
        var acoustId = new StubHandler(_ => StubHandler.Json("""
            { "status": "ok", "results": [ { "id": "dddddddd-0000-0000-0000-000000000001", "score": 0.98, "recordings": [
                { "id": "6f0e1a5c-6a43-4f4e-9b5c-2f6c2b2f1a11" }, { "id": "eeeeeeee-0000-0000-0000-000000000009" } ] } ] }
            """));
        var musicBrainz = new StubHandler(_ => StubHandler.File("musicbrainz-recording.json"));
        var lookup = new MetadataLookup(AcoustId(acoustId), MusicBrainz(musicBrainz), _options, NullLogger<MetadataLookup>.Instance);

        var candidates = await lookup.LookupAsync(Fingerprint, Ct);

        Assert.Equal(2, musicBrainz.Requests.Count);
        Assert.Equal(["Testalbum", "Zweiter Song", "Best of Testband"], candidates.Select(c => c.ReleaseTitle));
    }

    [Fact]
    public async Task ReleaseTrack_mit_Disc_Track_Artists_und_IDs()
    {
        var musicBrainz = new StubHandler(_ => StubHandler.File("musicbrainz-release.json"));
        var lookup = new MetadataLookup(AcoustId(new StubHandler(_ => throw new InvalidOperationException())), MusicBrainz(musicBrainz), _options, NullLogger<MetadataLookup>.Instance);
        var release = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

        var track = await lookup.GetReleaseTrackAsync(release, RecordingId, Ct);
        var missing = await lookup.GetReleaseTrackAsync(release, Guid.NewGuid(), Ct);

        Assert.NotNull(track);
        Assert.Null(missing);
        Assert.Equal(("Zweiter Song (Live)", "Testalbum (Deluxe)", "Testband"), (track.Title, track.Album, track.AlbumArtist));
        Assert.Equal(["Testband", "Gast"], track.Artists);
        Assert.Equal((2, 2, 1, 1), (track.Disc, track.DiscCount, track.Track, track.TrackCount));
        Assert.Equal((AlbumKind.Album, "album", "official", "XE", 2024), (track.Kind, track.ReleaseType, track.ReleaseStatus, track.Country, track.Year));
        Assert.Equal(Guid.Parse("cccccccc-0000-0000-0000-000000000021"), track.TrackId);
        Assert.Equal([Guid.Parse("ffffffff-0000-0000-0000-000000000001")], track.AlbumArtistIds);
        Assert.Single(musicBrainz.Requests);
        Assert.Contains("inc=artist-credits+recordings+release-groups+media", musicBrainz.Requests[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Cover_der_Release_Gruppe_wenn_die_Ausgabe_keins_hat()
    {
        var release = Guid.NewGuid();
        var group = Guid.NewGuid();
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/release-group/")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/jpeg") } } }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new CoverArtArchiveClient(handler.Client(CoverArtArchiveClient.BaseAddress), new NoDelayTimeProvider());

        var cover = await client.GetFrontCoverAsync(release, group, Ct);
        var none = await client.GetFrontCoverAsync(release, null, Ct);

        Assert.Equal([1, 2, 3], cover!.Data);
        Assert.Null(none);
        Assert.Equal(
            [$"/release/{release}/front-1200", $"/release-group/{group}/front-1200", $"/release/{release}/front-1200"],
            handler.Requests.Select(r => r.Request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task RequestGate_haelt_Mindestabstand()
    {
        var gate = new RequestGate(TimeSpan.FromMilliseconds(200), TimeProvider.System);
        var stopwatch = Stopwatch.StartNew();

        await gate.WaitAsync(Ct);
        await gate.WaitAsync(Ct);
        await gate.WaitAsync(Ct);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(380), $"nur {stopwatch.Elapsed}");
    }
}
