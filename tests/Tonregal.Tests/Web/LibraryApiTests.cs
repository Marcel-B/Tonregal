// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Api;
using Tonregal.Web.Library;

namespace Tonregal.Tests.Web;

public sealed class LibraryApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestLibrary _library = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public LibraryApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Testing")
            .UseSetting("Tonregal:MusicRoot", _library.MusicRoot)
            .ConfigureTestServices(services => services.AddScoped<ICoverArtSource, FakeCovers>()));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _library.Dispose();
    }

    /// <summary>Der Start-Scan läuft im Hintergrund; warten, bis er durch ist.</summary>
    private async Task WaitForScanAsync()
    {
        var scans = _factory.Services.GetRequiredService<LibraryScanService>();
        for (var i = 0; i < 100 && scans.Status.LastRunAt is null; i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.NotNull(scans.Status.LastResult);
    }

    [Fact]
    public async Task Artist_Album_Tracks_und_Cover()
    {
        var ct = TestContext.Current.CancellationToken;
        await WaitForScanAsync();

        var artists = await _client.GetFromJsonAsync<List<ArtistSummary>>("/api/library/artists", Json, ct);
        Assert.Equal(["Testband", "Various Artists"], artists!.Select(a => a.Name));

        var artist = await _client.GetFromJsonAsync<ArtistDetail>($"/api/library/artists/{artists![0].Id}", Json, ct);
        var albumSummary = Assert.Single(artist!.Albums);
        Assert.Equal(3, albumSummary.TrackCount);

        var album = await _client.GetFromJsonAsync<AlbumDetail>($"/api/library/albums/{albumSummary.Id}", Json, ct);
        Assert.Equal(["Erster Song", "Zweiter Song (feat. Gast)", "Dritter Song"], album!.Tracks.Select(t => t.Title));
        Assert.Equal(AudioFormat.Flac, album.Tracks[1].Files[0].Format);

        var albums = await _client.GetFromJsonAsync<List<AlbumListItem>>("/api/library/albums", Json, ct);
        var listed = Assert.Single(albums!, a => a.Id == albumSummary.Id);
        Assert.Equal("Testband", listed.AlbumArtist.Name);
        Assert.Equal(3, listed.TrackCount);
        Assert.Equal(albums!.Select(a => a.Id).OrderDescending(), albums!.Select(a => a.Id));

        var cover = await _client.GetAsync($"/api/library/albums/{albumSummary.Id}/cover", ct);
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal("image/jpeg", cover.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unbekanntes_Album_404()
    {
        await WaitForScanAsync();

        var response = await _client.GetAsync("/api/library/albums/999", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Scan_per_POST_anstossen()
    {
        var ct = TestContext.Current.CancellationToken;
        await WaitForScanAsync();

        var response = await _client.PostAsync("/api/library/scan", null, ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Tools_und_Abgleich_ohne_AcoustId_Key()
    {
        var ct = TestContext.Current.CancellationToken;
        await WaitForScanAsync();

        var tools = await _client.GetFromJsonAsync<ToolStatus>("/api/tools", Json, ct);
        Assert.Contains("fpcalc", tools!.Fpcalc);
        Assert.False(tools.AcoustIdConfigured);

        var album = await _client.GetFromJsonAsync<AlbumDetail>("/api/library/albums/1", Json, ct);
        var fileId = album!.Tracks[0].Files[0].Id;
        var response = await _client.GetAsync($"/api/library/files/{fileId}/candidates", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("API-Key fehlt", await response.Content.ReadAsStringAsync(ct));
    }

    private static readonly Guid Release = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    [Fact]
    public async Task Cover_nachholen_legt_cover_jpg_an()
    {
        var ct = TestContext.Current.CancellationToken;
        await WaitForScanAsync();
        int samplerId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TonregalDbContext>();
            var sampler = await db.Albums.SingleAsync(a => a.Title == "Sampler", ct);
            sampler.MusicBrainzReleaseId = Release;
            // Release-Gruppe kommt aus dem MusicBrainz-Cache statt aus dem Netz.
            db.MetadataCache.Add(new MetadataCacheEntry
            {
                Key = $"musicbrainz:release:{Release}",
                Json = await File.ReadAllTextAsync(Path.Combine(TestLibrary.TestData, "musicbrainz-release.json"), ct),
                FetchedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            samplerId = sampler.Id;
        }

        Assert.True((await _client.GetFromJsonAsync<AlbumDetail>($"/api/library/albums/{samplerId}", Json, ct))!.CanFetchCover);
        var response = await _client.PostAsync($"/api/library/albums/{samplerId}/cover", null, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(_library.Library, "Various Artists", "2020 - Sampler", "cover.jpg")));
        Assert.Equal(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), FakeCovers.LastGroup);
        Assert.False((await _client.GetFromJsonAsync<AlbumDetail>($"/api/library/albums/{samplerId}", Json, ct))!.CanFetchCover);
        var cover = await _client.GetAsync($"/api/library/albums/{samplerId}/cover", ct);
        Assert.Equal(FakeCovers.Cover.Data, await cover.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task Titel_als_Originaldatei_herunterladen()
    {
        var ct = TestContext.Current.CancellationToken;
        await WaitForScanAsync();
        var album = await TestalbumAsync(ct);
        var flac = album.Tracks[1].Files[0];

        var response = await _client.GetAsync($"/api/library/files/{flac.Id}/download", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("02 - Zweiter Song.flac", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_library.AlbumFolder, "02 - Zweiter Song.flac"), ct), await response.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/library/files/999/download", ct)).StatusCode);
    }

    [Fact]
    public async Task Album_als_ZIP_herunterladen()
    {
        var ct = TestContext.Current.CancellationToken;
        await WaitForScanAsync();
        var album = await TestalbumAsync(ct);

        var response = await _client.GetAsync($"/api/library/albums/{album.Id}/download", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Testband - Testalbum (2024).zip", response.Content.Headers.ContentDisposition?.FileNameStar);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
        Assert.Equal(
            new[] { "01 - Erster Song.mp3", "02 - Zweiter Song.flac", "03 - Dritter Song.m4a", "cover.jpg" }
                .Select(f => $"Testband - Testalbum (2024)/{f}"),
            zip.Entries.Select(e => e.FullName));
        using var copy = new MemoryStream();
        await using (var entry = await zip.Entries[1].OpenAsync(ct))
            await entry.CopyToAsync(copy, ct);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_library.AlbumFolder, "02 - Zweiter Song.flac"), ct), copy.ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/library/albums/999/download", ct)).StatusCode);
    }

    [Fact]
    public void ZIP_Eintraege_ab_dem_gemeinsamen_Ordner()
    {
        var root = Path.Combine(Path.GetTempPath(), "Album");
        string[] files = [Path.Combine(root, "CD1", "01.flac"), Path.Combine(root, "CD2", "01.flac"), Path.Combine(root, "cover.jpg"), Path.Combine(root, "COVER.jpg")];

        var entries = AlbumZipResult.EntryNames("A - B", files).Select(e => e.Entry);

        Assert.Equal(["A - B/CD1/01.flac", "A - B/CD2/01.flac", "A - B/cover.jpg", "A - B/COVER (2).jpg"], entries);
    }

    [Fact]
    public void Dateinamen_ohne_stoerende_Zeichen()
    {
        Assert.Equal("AC_DC - Back in Black_ (1980)", LibraryApi.SafeFileName("AC/DC - Back in Black? (1980)"));
        Assert.Equal("Album", LibraryApi.SafeFileName("..."));
    }

    private async Task<AlbumDetail> TestalbumAsync(CancellationToken ct)
    {
        var albums = await _client.GetFromJsonAsync<List<AlbumListItem>>("/api/library/albums", Json, ct);
        var id = albums!.Single(a => a.Title == "Testalbum").Id;
        return (await _client.GetFromJsonAsync<AlbumDetail>($"/api/library/albums/{id}", Json, ct))!;
    }

    private sealed class FakeCovers : ICoverArtSource
    {
        public static readonly EmbeddedPicture Cover = new(File.ReadAllBytes(Path.Combine(TestLibrary.TestData, "cover.jpg")), "image/jpeg");
        public static Guid? LastGroup { get; private set; }

        public Task<EmbeddedPicture?> GetFrontCoverAsync(Guid releaseId, Guid? releaseGroupId, CancellationToken ct)
        {
            LastGroup = releaseGroupId;
            return Task.FromResult<EmbeddedPicture?>(Cover);
        }
    }
}
