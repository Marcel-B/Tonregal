// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Navidrome;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Library;

namespace Tonregal.Tests.Web;

/// <summary>Bewertungen gegen ein Fake-Navidrome und eine kleine navidrome.db mit Navidromes Tabellen, dazu die Zufallswiedergabe.</summary>
public sealed class ListeningApiTests : IDisposable
{
    private const string FirstSong = "Testband/2024 - Testalbum/01 - Erster Song.mp3";
    private const string SecondSong = "Testband/2024 - Testalbum/02 - Zweiter Song.flac";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestLibrary _library = new();
    private readonly FakeNavidrome _navidrome = new();
    private WebApplicationFactory<Program>? _factory;

    public void Dispose()
    {
        _factory?.Dispose();
        _library.Dispose();
    }

    private HttpClient Client(bool navidrome = true)
    {
        if (navidrome)
            CreateNavidromeDatabase();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing").UseSetting("Tonregal:MusicRoot", _library.MusicRoot);
            if (navidrome)
            {
                b.UseSetting("Tonregal:Navidrome:Url", "http://navidrome.test:4533/");
                b.UseSetting("Tonregal:Navidrome:User", "marcel");
                b.UseSetting("Tonregal:Navidrome:Password", "geheim");
            }

            b.ConfigureTestServices(services => services
                .AddHttpClient<INavidromeClient, NavidromeClient>()
                .ConfigurePrimaryHttpMessageHandler(() => _navidrome));
        });
        return _factory.CreateClient();
    }

    /// <summary>
    /// Navidrome kennt die ersten beiden Stücke (eines mit absolutem, eines mit relativem Pfad). Bewertet hat marcel das
    /// erste mit 4 Sternen, ein anderer Benutzer das zweite.
    /// </summary>
    private void CreateNavidromeDatabase()
    {
        var database = Path.Combine(_library.MusicRoot, "navidrome", "navidrome.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE media_file (id TEXT PRIMARY KEY, path TEXT);
            CREATE TABLE "user" (id TEXT PRIMARY KEY, user_name TEXT);
            CREATE TABLE annotation (user_id TEXT, item_id TEXT, item_type TEXT, play_count INTEGER, rating INTEGER DEFAULT 0, starred BOOL);
            INSERT INTO media_file VALUES ('nd-1', $first), ('nd-2', $second);
            INSERT INTO "user" VALUES ('u-1', 'Marcel'), ('u-2', 'gast');
            INSERT INTO annotation VALUES ('u-1', 'nd-1', 'media_file', 3, 4, 0), ('u-2', 'nd-2', 'media_file', 1, 5, 0), ('u-1', 'nd-2', 'media_file', 2, 0, 0);
            """;
        command.Parameters.AddWithValue("$first", Path.Combine(_library.Library, FirstSong));
        command.Parameters.AddWithValue("$second", SecondSong);
        command.ExecuteNonQuery();
    }

    private async Task<Dictionary<string, int>> FileIdsAsync()
    {
        var scans = _factory!.Services.GetRequiredService<LibraryScanService>();
        for (var i = 0; i < 100 && scans.Status.LastRunAt is null; i++)
            await Task.Delay(50, Ct);
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TonregalDbContext>().AudioFiles.ToDictionaryAsync(f => f.Path, f => f.Id, Ct);
    }

    [Fact]
    public async Task Liest_die_Bewertungen_des_eigenen_Benutzers()
    {
        var client = Client();
        var ids = await FileIdsAsync();

        var ratings = await client.GetFromJsonAsync<Dictionary<string, int>>(
            $"/api/player/ratings?files={ids[FirstSong]}&files={ids[SecondSong]}&files=999", Ct);

        Assert.Equal(new Dictionary<string, int> { [ids[FirstSong].ToString()] = 4 }, ratings);
        Assert.Empty(_navidrome.Requests);
    }

    [Fact]
    public async Task Bewertet_in_Navidrome()
    {
        var client = Client();
        var ids = await FileIdsAsync();

        var rated = await client.PutAsJsonAsync($"/api/player/files/{ids[SecondSong]}/rating", new { rating = 5 }, Ct);
        var cleared = await client.PutAsJsonAsync($"/api/player/files/{ids[FirstSong]}/rating", new { rating = 0 }, Ct);

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (rated.StatusCode, cleared.StatusCode));
        Assert.All(_navidrome.Requests, r => Assert.Equal("/rest/setRating", r.AbsolutePath));
        Assert.Equal([("nd-2", "5"), ("nd-1", "0")], _navidrome.Requests.Select(r => HttpUtility.ParseQueryString(r.Query)).Select(q => (q["id"], q["rating"])));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/player/files/{ids[FirstSong]}/rating", new { rating = 6 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/player/files/999/rating", new { rating = 3 }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Ohne_Navidrome_keine_Bewertungen()
    {
        var client = Client(navidrome: false);
        var ids = await FileIdsAsync();

        Assert.Empty((await client.GetFromJsonAsync<Dictionary<string, int>>($"/api/player/ratings?files={ids[FirstSong]}", Ct))!);
        var rated = await client.PutAsJsonAsync($"/api/player/files/{ids[FirstSong]}/rating", new { rating = 3 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, rated.StatusCode);
    }

    [Fact]
    public async Task Mischt_Interpret_und_Bibliothek()
    {
        var client = Client(navidrome: false);
        await FileIdsAsync();
        int band;
        await using (var scope = _factory!.Services.CreateAsyncScope())
            band = await scope.ServiceProvider.GetRequiredService<TonregalDbContext>().Artists.Where(a => a.Name == "Testband").Select(a => a.Id).SingleAsync(Ct);

        var all = (await client.GetFromJsonAsync<PlaylistItem[]>("/api/player/shuffle", Json, Ct))!;
        var artist = (await client.GetFromJsonAsync<PlaylistItem[]>($"/api/player/shuffle?artist={band}", Json, Ct))!;
        var two = (await client.GetFromJsonAsync<PlaylistItem[]>("/api/player/shuffle?limit=2", Json, Ct))!;

        Assert.Equal(4, all.Length);
        Assert.Equal(all.Length, all.Select(i => i.TrackId).Distinct().Count());
        Assert.Equal(["Dritter Song", "Erster Song", "Zweiter Song (feat. Gast)"], artist.Select(i => i.Title).Order());
        Assert.All(artist, i => Assert.Equal("Testalbum", i.Album));
        Assert.Equal(2, two.Length);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/player/shuffle?artist=999", Ct)).StatusCode);
    }

    private sealed class FakeNavidrome : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"subsonic-response":{"status":"ok","version":"1.16.1"}}""", null, "application/json"),
            });
        }
    }
}
