// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Navidrome;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Library;

namespace Tonregal.Tests.Web;

public sealed class SmartPlaylistsApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestLibrary _library = new();
    private readonly FakeNavidrome _navidrome = new();
    private WebApplicationFactory<Program>? _factory;

    private string Playlists => Path.Combine(_library.Library, "Playlists");

    public void Dispose()
    {
        _factory?.Dispose();
        _library.Dispose();
    }

    private HttpClient Client(bool navidrome = false)
    {
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

    private static JsonNode Nsp(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    private static object Input(string name, double? bpmMin = null, double? bpmMax = null, string[]? keys = null, string? genre = null,
        int? addedWithinDays = null, bool neverPlayed = false, string sort = "Random", int? limit = null, int? minRating = null) =>
        new { name, bpmMin, bpmMax, keys, genre, addedWithinDays, neverPlayed, sort, limit, minRating };

    /// <summary>Wartet auf den ersten Scan und gibt den Stücken BPM, Tonart und Genre.</summary>
    private async Task AnalyzedAsync()
    {
        var scans = _factory!.Services.GetRequiredService<LibraryScanService>();
        for (var i = 0; i < 100 && scans.Status.LastRunAt is null; i++)
            await Task.Delay(50, Ct);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TonregalDbContext>();
        var tracks = await db.Tracks.OrderBy(t => t.Title).ToListAsync(Ct);
        (double Bpm, string Key, string Genre)[] values = [(90, "Am", "Synthwave"), (124, "C", "Techno"), (128, "Am", "Synth-Pop"), (170, "F#m", "Drum & Bass")];
        foreach (var (track, value) in tracks.Zip(values))
            (track.Bpm, track.InitialKey, track.Genre) = value;
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Schreibt_die_Startlisten_beim_Start_und_raeumt_Reste_weg()
    {
        Directory.CreateDirectory(Playlists);
        File.WriteAllText(Path.Combine(Playlists, "tonregal-99.nsp"), "{}");
        File.WriteAllText(Path.Combine(Playlists, "eigene.nsp"), "{}");

        var listed = await Client().GetFromJsonAsync<SmartPlaylistInfo[]>("/api/smart-playlists", Json, Ct);

        Assert.Equal(["Neu (30 Tage)", "Nie gehört"], listed!.Select(p => p.Name));
        Assert.Equal(["eigene.nsp", "tonregal-1.nsp", "tonregal-2.nsp"], Directory.GetFiles(Playlists).Select(Path.GetFileName).Order());
        var neu = Nsp(Path.Combine(Playlists, "tonregal-1.nsp"));
        Assert.Equal("Neu (30 Tage)", (string?)neu["name"]);
        Assert.Equal(30, (int?)neu["all"]![0]!["inTheLast"]!["dateadded"]);
        Assert.Equal(("dateadded", "desc"), ((string?)neu["sort"], (string?)neu["order"]));
        Assert.Equal(0, (int?)Nsp(Path.Combine(Playlists, "tonregal-2.nsp"))["all"]![0]!["is"]!["playcount"]);
    }

    [Fact]
    public async Task Legt_an_zaehlt_benennt_um_und_loescht_auch_in_Navidrome()
    {
        var client = Client(navidrome: true);
        await AnalyzedAsync();

        var response = await client.PostAsJsonAsync("/api/smart-playlists",
            Input("Warmlaufen", bpmMin: 120, bpmMax: 130, keys: ["Am", " C ", "Am"], genre: "synth", sort: "Bpm", limit: 50), Json, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<SmartPlaylistInfo>(Json, Ct))!;
        Assert.Equal(["Am", "C"], created.Keys);
        Assert.Equal(1, created.Matching);
        var file = Path.Combine(_library.Library, created.Path);
        var nsp = Nsp(file);
        Assert.Equal("[120,130]", nsp["all"]![0]!["inTheRange"]!["bpm"]!.ToJsonString());
        Assert.Equal(["Am", "C"], nsp["all"]![1]!["any"]!.AsArray().Select(r => (string?)r!["is"]!["key"]));
        Assert.Equal("synth", (string?)nsp["all"]![2]!["contains"]!["genre"]);
        Assert.Equal(("bpm", 50), ((string?)nsp["sort"], (int?)nsp["limit"]));
        Assert.Equal(created.Nsp, File.ReadAllText(file));

        var renamed = await client.PutAsJsonAsync($"/api/smart-playlists/{created.Id}", Input("Am-Block", keys: ["Am"]), Json, Ct);
        var updated = (await renamed.Content.ReadFromJsonAsync<SmartPlaylistInfo>(Json, Ct))!;
        Assert.Equal((created.Path, 2), (updated.Path, updated.Matching));
        Assert.Equal("Am-Block", (string?)Nsp(file)["name"]);

        CreateNavidromeDatabase(file);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/smart-playlists/{created.Id}", Ct)).StatusCode);
        Assert.False(File.Exists(file));
        var request = Assert.Single(_navidrome.Requests);
        Assert.Equal("/rest/deletePlaylist", request.AbsolutePath);
        Assert.Equal("pl-1", HttpUtility.ParseQueryString(request.Query)["id"]);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/smart-playlists/{created.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Prueft_Eingaben_und_doppelte_Namen()
    {
        var client = Client();

        var noRule = await client.PostAsJsonAsync("/api/smart-playlists", Input("Leer"), Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noRule.StatusCode);
        Assert.Contains("Mindestens eine Regel", await noRule.Content.ReadAsStringAsync(Ct));
        var reversed = await client.PostAsJsonAsync("/api/smart-playlists", Input("Falsch", bpmMin: 140, bpmMax: 100), Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        var taken = await client.PostAsJsonAsync("/api/smart-playlists", Input("nie GEHÖRT", neverPlayed: true), Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/smart-playlists/99", Input("X", neverPlayed: true), Json, Ct)).StatusCode);
    }

    [Fact]
    public async Task Listet_die_Tonarten_der_Bibliothek()
    {
        var client = Client();
        await AnalyzedAsync();

        var keys = await client.GetFromJsonAsync<KeyCount[]>("/api/smart-playlists/keys", Json, Ct);

        Assert.Equal([new KeyCount("Am", 2), new KeyCount("C", 1), new KeyCount("F#m", 1)], keys!);
    }

    private void CreateNavidromeDatabase(string playlistFile, params (string Id, string Path)[] songs)
    {
        var database = Path.Combine(_library.MusicRoot, "navidrome", "navidrome.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE playlist (id TEXT PRIMARY KEY, path TEXT); INSERT INTO playlist VALUES ('pl-1', $path);
            CREATE TABLE media_file (id TEXT PRIMARY KEY, path TEXT);
            """;
        command.Parameters.AddWithValue("$path", playlistFile);
        command.ExecuteNonQuery();
        foreach (var (id, path) in songs)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO media_file VALUES ($id, $path)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$path", path);
            insert.ExecuteNonQuery();
        }
    }

    private async Task<(int Id, string Path, string Title)[]> FilesAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        return [.. (await scope.ServiceProvider.GetRequiredService<TonregalDbContext>().AudioFiles
                .Select(f => new { f.Id, f.Path, f.Track.Title }).ToListAsync(Ct))
            .OrderBy(f => f.Title).Select(f => (f.Id, f.Path, f.Title))];
    }

    [Fact]
    public async Task Spielt_ohne_Navidrome_nach_Tonregals_eigener_Auswertung()
    {
        var client = Client();
        await AnalyzedAsync();
        var created = await (await client.PostAsJsonAsync("/api/smart-playlists", Input("Moll", keys: ["Am"], sort: "Bpm"), Json, Ct))
            .Content.ReadFromJsonAsync<SmartPlaylistInfo>(Json, Ct);

        var tracks = await client.GetFromJsonAsync<SmartPlaylistTracks>($"/api/smart-playlists/{created!.Id}/tracks", Json, Ct);

        var files = await FilesAsync();
        Assert.False(tracks!.FromNavidrome);
        Assert.Equal([files[0].Id, files[2].Id], tracks.Items.Select(i => i.FileId));
        Assert.Equal(files[0].Title, tracks.Items[0].Title);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/smart-playlists/99/tracks", Ct)).StatusCode);
    }

    [Fact]
    public async Task Spielt_Navidromes_Auswertung_in_ihrer_Reihenfolge()
    {
        var client = Client(navidrome: true);
        await AnalyzedAsync();
        var files = await FilesAsync();
        // Ein Pfad absolut, einer relativ: Navidrome speichert je nach Version beides.
        CreateNavidromeDatabase(Path.Combine(_library.Library, "Playlists", "tonregal-2.nsp"),
            ("nd-a", Path.Combine(_library.Library, files[3].Path)), ("nd-b", files[1].Path), ("nd-x", "Fremd/gibt es nicht.mp3"));
        _navidrome.Playlist = ["nd-b", "nd-x", "nd-a"];

        var tracks = await client.GetFromJsonAsync<SmartPlaylistTracks>("/api/smart-playlists/2/tracks", Json, Ct);

        Assert.True(tracks!.FromNavidrome);
        Assert.Equal([files[1].Id, files[3].Id], tracks.Items.Select(i => i.FileId));
        Assert.Equal("/rest/getPlaylist", Assert.Single(_navidrome.Requests).AbsolutePath);
    }

    [Fact]
    public async Task Bewertung_als_Regel_auch_ohne_Navidromes_Auswertung()
    {
        var client = Client(navidrome: true);
        await AnalyzedAsync();
        var files = await FilesAsync();
        // Navidrome kennt die Playlist noch nicht, aber die Bewertungen: 5 und 3 Sterne.
        CreateNavidromeDatabase(Path.Combine(_library.Library, "Playlists", "andere.nsp"), ("nd-a", files[0].Path), ("nd-b", files[1].Path));
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_library.MusicRoot, "navidrome", "navidrome.db")};Pooling=False"))
        {
            connection.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE "user" (id TEXT PRIMARY KEY, user_name TEXT); INSERT INTO "user" VALUES ('u-1', 'marcel');
                CREATE TABLE annotation (user_id TEXT, item_id TEXT, item_type TEXT, rating INTEGER);
                INSERT INTO annotation VALUES ('u-1', 'nd-a', 'media_file', 5), ('u-1', 'nd-b', 'media_file', 3);
                """;
            command.ExecuteNonQuery();
        }

        var response = await client.PostAsJsonAsync("/api/smart-playlists", Input("Lieblinge", minRating: 4), Json, Ct);
        var created = (await response.Content.ReadFromJsonAsync<SmartPlaylistInfo>(Json, Ct))!;
        var tracks = await client.GetFromJsonAsync<SmartPlaylistTracks>($"/api/smart-playlists/{created.Id}/tracks", Json, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(4, created.MinRating);
        Assert.Equal("[4,5]", Nsp(Path.Combine(_library.Library, created.Path))["all"]![0]!["inTheRange"]!["rating"]!.ToJsonString());
        Assert.False(tracks!.FromNavidrome);
        Assert.Equal([files[0].Id], tracks.Items.Select(i => i.FileId));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/smart-playlists", Input("Zu viel", minRating: 6), Json, Ct)).StatusCode);
    }

    private sealed class FakeNavidrome : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        /// <summary>Song-IDs, die <c>getPlaylist</c> liefert.</summary>
        public string[] Playlist { get; set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var body = request.RequestUri!.AbsolutePath == "/rest/getPlaylist"
                ? JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["subsonic-response"] = new { status = "ok", playlist = new { id = "pl-1", entry = Playlist.Select(id => new { id }) } },
                })
                : """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, null, "application/json") });
        }
    }
}
