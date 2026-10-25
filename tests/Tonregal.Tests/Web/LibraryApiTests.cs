// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tonregal.Domain.Entities;
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
            .UseSetting("Tonregal:MusicRoot", _library.MusicRoot));
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
}
