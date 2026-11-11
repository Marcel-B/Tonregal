// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;
using Tonregal.Web.Api;
using Tonregal.Web.Library;

namespace Tonregal.Tests.Web;

public sealed class TagEditorApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestLibrary _library = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly TagLibTagReader _reader = new();

    public TagEditorApiTests()
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

    private async Task<AlbumDetail> TestalbumAsync()
    {
        var scans = _factory.Services.GetRequiredService<LibraryScanService>();
        for (var i = 0; i < 100 && scans.Status.LastRunAt is null; i++)
            await Task.Delay(50, Ct);
        var albums = await _client.GetFromJsonAsync<List<AlbumListItem>>("/api/library/albums", Json, Ct);
        return await AlbumAsync(albums!.Single(a => a.Title == "Testalbum").Id);
    }

    private async Task<AlbumDetail> AlbumAsync(int id) =>
        (await _client.GetFromJsonAsync<AlbumDetail>($"/api/library/albums/{id}", Json, Ct))!;

    private async Task<TagEditResult> PutAsync(string url, object body)
    {
        var response = await _client.PutAsJsonAsync(url, body, Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TagEditResult>(Json, Ct))!;
    }

    private string Full(string relative) => Path.Combine(_library.Library, relative);

    [Fact]
    public async Task Album_umbenennen_schreibt_die_Dateien_und_zieht_Ordner_Cover_und_Index_nach()
    {
        var album = await TestalbumAsync();

        var result = await PutAsync($"/api/library/albums/{album.Id}/tags", new { album = "Neues Album", year = 2025, genre = "Rock" });

        Assert.Equal(3, result.Changed);
        Assert.Equal(3, result.Moved);
        Assert.Empty(result.Errors);
        var folder = Path.Combine(_library.Library, "Testband", "2025 - Neues Album");
        Assert.True(File.Exists(Path.Combine(folder, "01 - Erster Song.mp3")));
        Assert.True(File.Exists(Path.Combine(folder, "cover.jpg")));
        Assert.False(File.Exists(Path.Combine(_library.AlbumFolder, "01 - Erster Song.mp3")));
        var tags = _reader.Read(Path.Combine(folder, "02 - Zweiter Song (feat. Gast).flac")).Tags;
        Assert.Equal(("Neues Album", 2025, "Rock"), (tags.Album, tags.Year, tags.Genre));

        var edited = await AlbumAsync(result.AlbumId!.Value);
        Assert.Equal(("Neues Album", 2025), (edited.Title, edited.Year));
        Assert.Equal(3, edited.Tracks.Count);
        Assert.All(edited.Tracks, t => Assert.Equal("Rock", t.Genre));
        Assert.All(edited.Tracks, t => Assert.StartsWith("Testband/2025 - Neues Album/", t.Files[0].Path));
        Assert.False(edited.CanFetchCover);
        var cover = await _client.GetAsync($"/api/library/albums/{edited.Id}/cover", Ct);
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
    }

    [Fact]
    public async Task Track_umbenennen_behaelt_Bewertung_Playlist_und_Songtext()
    {
        var album = await TestalbumAsync();
        var track = album.Tracks.Single(t => t.Title == "Erster Song");
        var lrc = Path.ChangeExtension(Full(track.Files[0].Path), ".lrc");
        await File.WriteAllTextAsync(lrc, "[00:01.00]Hallo", Ct);
        (await _client.PutAsJsonAsync($"/api/player/files/{track.Files[0].Id}/rating", new { rating = 4 }, Ct)).EnsureSuccessStatusCode();
        var playlist = await (await _client.PostAsJsonAsync("/api/playlists", new { name = "Lieblinge" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        var playlistId = playlist.GetProperty("id").GetInt32();
        (await _client.PutAsJsonAsync($"/api/playlists/{playlistId}/tracks", new { trackIds = new[] { track.Id } }, Ct)).EnsureSuccessStatusCode();

        var result = await PutAsync($"/api/library/tracks/{track.Id}/tags", new { title = "Erstes Lied", number = 7 });

        Assert.Equal((1, 1), (result.Changed, result.Moved));
        Assert.Equal(album.Id, result.AlbumId);
        var moved = Path.Combine(_library.AlbumFolder, "07 - Erstes Lied.mp3");
        Assert.True(File.Exists(moved));
        Assert.True(File.Exists(Path.ChangeExtension(moved, ".lrc")));
        Assert.False(File.Exists(lrc));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TonregalDbContext>();
        var edited = await db.Tracks.Include(t => t.Files).SingleAsync(t => t.Title == "Erstes Lied", Ct);
        Assert.Equal((7, 4), (edited.Number, edited.Rating));
        Assert.Equal("Testband/2024 - Testalbum/07 - Erstes Lied.mp3", Assert.Single(edited.Files).Path);
        Assert.True(await db.PlaylistEntries.AnyAsync(e => e.PlaylistId == playlistId && e.TrackId == edited.Id, Ct));
    }

    [Fact]
    public async Task Track_in_ein_anderes_Album_verlegen()
    {
        var album = await TestalbumAsync();
        var track = album.Tracks.Single(t => t.Title == "Dritter Song");

        var result = await PutAsync($"/api/library/tracks/{track.Id}/tags", new { album = "Andere Platte", year = 2023 });

        Assert.NotEqual(album.Id, result.AlbumId);
        var other = await AlbumAsync(result.AlbumId!.Value);
        Assert.Equal(("Andere Platte", 2023), (other.Title, other.Year));
        Assert.Equal("Testband/2023 - Andere Platte/03 - Dritter Song.m4a", Assert.Single(other.Tracks).Files[0].Path);
        Assert.Equal(2, (await AlbumAsync(album.Id)).Tracks.Count);
        Assert.True(File.Exists(Path.Combine(_library.AlbumFolder, "cover.jpg")));
    }

    [Fact]
    public async Task Neuer_Album_Interpret_nimmt_seine_Tracks_mit_Gaeste_bleiben()
    {
        var album = await TestalbumAsync();

        var result = await PutAsync($"/api/library/albums/{album.Id}/tags", new { albumArtist = "Die Testband" });

        var edited = await AlbumAsync(result.AlbumId!.Value);
        Assert.Equal("Die Testband", edited.AlbumArtist.Name);
        Assert.Equal(["Die Testband", "Gast"], edited.Tracks.Single(t => t.Number == 2).Artists.Order());
        Assert.True(Directory.Exists(Path.Combine(_library.Library, "Die Testband", "2024 - Testalbum")));
        Assert.False(Directory.Exists(_library.AlbumFolder));
        var artists = await _client.GetFromJsonAsync<List<ArtistSummary>>("/api/library/artists", Json, Ct);
        Assert.DoesNotContain(artists!, a => a.Name == "Testband");
    }

    [Fact]
    public async Task Ohne_Aenderung_bleibt_alles_wie_es_ist()
    {
        var album = await TestalbumAsync();
        var before = File.GetLastWriteTimeUtc(Full(album.Tracks[0].Files[0].Path));

        var result = await PutAsync($"/api/library/albums/{album.Id}/tags", new { album = "Testalbum", year = 2024 });

        Assert.Equal((album.Id, 0, 0), (result.AlbumId!.Value, result.Changed, result.Moved));
        Assert.Equal(before, File.GetLastWriteTimeUtc(Full(album.Tracks[0].Files[0].Path)));
    }

    [Fact]
    public async Task Cover_hochladen_bettet_es_ein_und_legt_es_in_den_Ordner()
    {
        var album = await TestalbumAsync();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        using var form = new MultipartFormDataContent { { new ByteArrayContent(png), "file", "neu.png" } };

        var response = await _client.PutAsync($"/api/library/albums/{album.Id}/cover", form, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(_library.AlbumFolder, "cover.png"), Ct));
        Assert.False(File.Exists(Path.Combine(_library.AlbumFolder, "cover.jpg")));
        Assert.All(album.Tracks, t => Assert.Equal(png, _reader.ReadCover(Full(t.Files[0].Path))!.Data));
        var cover = await _client.GetAsync($"/api/library/albums/{album.Id}/cover", Ct);
        Assert.Equal("image/png", cover.Content.Headers.ContentType?.MediaType);

        using var text = new MultipartFormDataContent { { new StringContent("kein Bild"), "file", "a.txt" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsync($"/api/library/albums/{album.Id}/cover", text, Ct)).StatusCode);
    }

    [Fact]
    public async Task Unzulaessige_Werte_werden_abgelehnt()
    {
        var album = await TestalbumAsync();
        var track = album.Tracks[0];

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync($"/api/library/tracks/{track.Id}/tags", new { title = " " }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync($"/api/library/albums/{album.Id}/tags", new { title = "X" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync($"/api/library/tracks/{track.Id}/tags", new { year = 12345 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/library/tracks/9999/tags", new { title = "X" }, Ct)).StatusCode);
    }
}
