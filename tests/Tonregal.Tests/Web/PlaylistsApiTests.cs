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
using Tonregal.Web.Library;

namespace Tonregal.Tests.Web;

public sealed class PlaylistsApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestLibrary _library = new();
    private WebApplicationFactory<Program>? _factory;

    private string Folder => Path.Combine(_library.Library, "Playlists");

    public void Dispose()
    {
        _factory?.Dispose();
        _library.Dispose();
    }

    private HttpClient Client()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseEnvironment("Testing").UseSetting("Tonregal:MusicRoot", _library.MusicRoot));
        return _factory.CreateClient();
    }

    /// <summary>Wartet auf den ersten Scan; die Tracks nach Titel.</summary>
    private async Task<(int Id, string Title, int AlbumId)[]> TracksAsync()
    {
        var scans = _factory!.Services.GetRequiredService<LibraryScanService>();
        for (var i = 0; i < 100 && scans.Status.LastRunAt is null; i++)
            await Task.Delay(50, Ct);
        await using var scope = _factory.Services.CreateAsyncScope();
        return [.. (await scope.ServiceProvider.GetRequiredService<TonregalDbContext>().Tracks
                .Select(t => new { t.Id, t.Title, t.AlbumId }).ToListAsync(Ct))
            .OrderBy(t => t.Title, StringComparer.Ordinal).Select(t => (t.Id, t.Title, t.AlbumId))];
    }

    private async Task<PlaylistSummary> CreateAsync(HttpClient client, string name, string? comment = null)
    {
        var response = await client.PostAsJsonAsync("/api/playlists", new { name, comment }, Json, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PlaylistSummary>(Json, Ct))!;
    }

    [Fact]
    public async Task Legt_an_fuellt_sortiert_und_schreibt_die_m3u8()
    {
        var client = Client();
        var tracks = await TracksAsync();
        var (dritter, erster, sampler, zweiter) = (tracks[0], tracks[1], tracks[2], tracks[3]);
        Assert.Equal(["Dritter Song", "Erster Song", "Sampler-Stück", "Zweiter Song (feat. Gast)"], tracks.Select(t => t.Title));

        var created = await CreateAsync(client, "  Sonntag  ", "zum Frühstück");
        Assert.Equal(("Sonntag", "zum Frühstück", 0), (created.Name, created.Comment, created.TrackCount));

        var add = await client.PostAsJsonAsync($"/api/playlists/{created.Id}/tracks", new { trackIds = new[] { zweiter.Id, sampler.Id, zweiter.Id } }, Json, Ct);
        var added = (await add.Content.ReadFromJsonAsync<PlaylistAddResult>(Json, Ct))!;
        Assert.Equal((2, 0, 2), (added.Added, added.Skipped, added.Playlist.TrackCount));
        Assert.Equal([zweiter.AlbumId, sampler.AlbumId], added.Playlist.AlbumIds);

        var again = await client.PostAsJsonAsync($"/api/playlists/{created.Id}/tracks", new { trackIds = new[] { erster.Id, zweiter.Id } }, Json, Ct);
        Assert.Equal((1, 1), ((await again.Content.ReadFromJsonAsync<PlaylistAddResult>(Json, Ct))!.Added, 1));

        var detail = await client.GetFromJsonAsync<PlaylistDetail>($"/api/playlists/{created.Id}", Json, Ct);
        Assert.Equal(["Zweiter Song (feat. Gast)", "Sampler-Stück", "Erster Song"], detail!.Items.Select(i => i.Title));
        Assert.True(detail.Playlist.DurationMs > 0);

        var file = Path.Combine(Folder, $"tonregal-liste-{created.Id}.m3u8");
        var lines = File.ReadAllLines(file);
        Assert.Equal(["#EXTM3U", "#PLAYLIST:Sonntag"], lines[..2]);
        Assert.Equal("../Testband/2024 - Testalbum/02 - Zweiter Song.flac", lines[3]);
        Assert.Equal("../Various Artists/2020 - Sampler/01 - Sampler-Stück.opus", lines[5]);
        Assert.StartsWith("#EXTINF:", lines[6]);
        Assert.EndsWith("Erster Song", lines[6]);

        // Neue Reihenfolge, „Sampler-Stück“ fällt heraus, „Dritter Song“ kommt dazu.
        var set = await client.PutAsJsonAsync($"/api/playlists/{created.Id}/tracks", new { trackIds = new[] { erster.Id, dritter.Id, zweiter.Id } }, Json, Ct);
        var reordered = (await set.Content.ReadFromJsonAsync<PlaylistDetail>(Json, Ct))!;
        Assert.Equal(["Erster Song", "Dritter Song", "Zweiter Song (feat. Gast)"], reordered.Items.Select(i => i.Title));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/playlists/{created.Id}/tracks/{dritter.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/playlists/{created.Id}/tracks/{dritter.Id}", Ct)).StatusCode);
        detail = await client.GetFromJsonAsync<PlaylistDetail>($"/api/playlists/{created.Id}", Json, Ct);
        Assert.Equal(["Erster Song", "Zweiter Song (feat. Gast)"], detail!.Items.Select(i => i.Title));

        // Danach hängt ein neues Stück hinten an, die Positionen bleiben lückenlos.
        await client.PostAsJsonAsync($"/api/playlists/{created.Id}/tracks", new { trackIds = new[] { sampler.Id } }, Json, Ct);
        detail = await client.GetFromJsonAsync<PlaylistDetail>($"/api/playlists/{created.Id}", Json, Ct);
        Assert.Equal(["Erster Song", "Zweiter Song (feat. Gast)", "Sampler-Stück"], detail!.Items.Select(i => i.Title));
        Assert.Contains("01 - Sampler-Stück.opus", File.ReadAllLines(file)[^1]);

        var renamed = await client.PutAsJsonAsync($"/api/playlists/{created.Id}", new { name = "Sonntagmorgen" }, Json, Ct);
        Assert.Equal("Sonntagmorgen", (await renamed.Content.ReadFromJsonAsync<PlaylistSummary>(Json, Ct))!.Name);
        Assert.Equal("#PLAYLIST:Sonntagmorgen", File.ReadAllLines(file)[1]);

        var listed = Assert.Single((await client.GetFromJsonAsync<PlaylistSummary[]>("/api/playlists", Json, Ct))!);
        Assert.Equal((created.Id, 3, (string?)null), (listed.Id, listed.TrackCount, listed.Comment));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/playlists/{created.Id}", Ct)).StatusCode);
        Assert.False(File.Exists(file));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/playlists/{created.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Prueft_Namen_und_unbekannte_Stuecke()
    {
        var client = Client();
        await TracksAsync();
        var first = await CreateAsync(client, "Abends");

        var taken = await client.PostAsJsonAsync("/api/playlists", new { name = "ABENDS" }, Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        var empty = await client.PostAsJsonAsync("/api/playlists", new { name = " " }, Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var unknown = await client.PostAsJsonAsync($"/api/playlists/{first.Id}/tracks", new { trackIds = new[] { 9999 } }, Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("Track 9999 gibt es nicht", await unknown.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/playlists/99/tracks", new { trackIds = Array.Empty<int>() }, Json, Ct)).StatusCode);
    }

    [Fact]
    public async Task Raeumt_beim_Start_verwaiste_Dateien_weg()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "tonregal-liste-42.m3u8"), "#EXTM3U\n");
        File.WriteAllText(Path.Combine(Folder, "eigene.m3u8"), "#EXTM3U\n");

        var client = Client();
        await client.GetFromJsonAsync<PlaylistSummary[]>("/api/playlists", Json, Ct);

        Assert.DoesNotContain("tonregal-liste-42.m3u8", Directory.GetFiles(Folder).Select(Path.GetFileName));
        Assert.Contains("eigene.m3u8", Directory.GetFiles(Folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Ein_Stueck_bleibt_in_der_Playlist_wenn_seine_Datei_ein_neues_Album_bekommt()
    {
        var client = Client();
        var tracks = await TracksAsync();
        var erster = tracks.Single(t => t.Title == "Erster Song");
        var playlist = await CreateAsync(client, "Bleibt");
        await client.PostAsJsonAsync($"/api/playlists/{playlist.Id}/tracks", new { trackIds = new[] { erster.Id } }, Json, Ct);

        // Neuer Albumtitel im Tag: der Scan hängt die Datei an einen neuen Track, der alte verschwindet.
        var path = Path.Combine(_library.AlbumFolder, "01 - Erster Song.mp3");
        using (var tagged = TagLib.File.Create(path))
        {
            tagged.Tag.Album = "Anderes Album";
            tagged.Save();
        }
        await using (var scope = _factory!.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ILibraryScanner>().ScanAsync(null, Ct);

        var detail = await client.GetFromJsonAsync<PlaylistDetail>($"/api/playlists/{playlist.Id}", Json, Ct);
        var item = Assert.Single(detail!.Items);
        Assert.Equal(("Erster Song", "Anderes Album"), (item.Title, item.Album));
        Assert.NotEqual(erster.Id, item.TrackId);
    }
}
