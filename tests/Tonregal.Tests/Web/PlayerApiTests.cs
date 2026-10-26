// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
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
using Tonregal.Web.Api;
using Tonregal.Web.Library;

namespace Tonregal.Tests.Web;

/// <summary>Gegen ein Fake-Navidrome (HttpMessageHandler) und eine kleine navidrome.db.</summary>
public sealed class PlayerApiTests : IDisposable
{
    private const string FirstSong = "Testband/2024 - Testalbum/01 - Erster Song.mp3";
    private static readonly byte[] NavidromeAudio = [1, 2, 3, 4];
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
            CreateNavidromeDatabase(Path.Combine(_library.Library, FirstSong));
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

    private void CreateNavidromeDatabase(string songPath)
    {
        var database = Path.Combine(_library.MusicRoot, "navidrome", "navidrome.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE media_file (id TEXT PRIMARY KEY, path TEXT); INSERT INTO media_file VALUES ('nd-1', $path)";
        command.Parameters.AddWithValue("$path", songPath);
        command.ExecuteNonQuery();
    }

    private async Task<int> FileIdAsync(string path)
    {
        var scans = _factory!.Services.GetRequiredService<LibraryScanService>();
        for (var i = 0; i < 100 && scans.Status.LastRunAt is null; i++)
            await Task.Delay(50, Ct);
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TonregalDbContext>().AudioFiles.Where(f => f.Path == path).Select(f => f.Id).SingleAsync(Ct);
    }

    private static HttpRequestMessage Get(string url, long from = 0) => new(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(from, null) } };

    [Fact]
    public async Task Streamt_ueber_Navidrome_mit_Range_und_Token()
    {
        var client = Client();
        var id = await FileIdAsync(FirstSong);

        var response = await client.SendAsync(Get($"/api/player/files/{id}/stream", 2), Ct);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("navidrome", response.Headers.GetValues(PlayerApi.SourceHeader).Single());
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes 2-5/6", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(NavidromeAudio, await response.Content.ReadAsByteArrayAsync(Ct));

        var request = Assert.Single(_navidrome.Requests);
        Assert.Equal("/rest/stream", request.Uri.AbsolutePath);
        var query = HttpUtility.ParseQueryString(request.Uri.Query);
        Assert.Equal(("nd-1", "marcel", "Tonregal"), (query["id"], query["u"], query["c"]));
        Assert.Equal(NavidromeClient.Token("geheim", query["s"]!), query["t"]);
        Assert.Null(query["p"]);
        Assert.Equal("bytes=2-", request.Range);
    }

    [Fact]
    public async Task Scrobbelt_bei_Navidrome()
    {
        var client = Client();
        var id = await FileIdAsync(FirstSong);

        var nowPlaying = await client.PostAsync($"/api/player/files/{id}/scrobble?submission=false", null, Ct);
        var played = await client.PostAsync($"/api/player/files/{id}/scrobble", null, Ct);

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (nowPlaying.StatusCode, played.StatusCode));
        Assert.Equal(["false", "true"], _navidrome.Requests.Select(r => HttpUtility.ParseQueryString(r.Uri.Query)["submission"]));
    }

    [Fact]
    public async Task Kennt_Navidrome_die_Datei_nicht_kommt_sie_direkt()
    {
        var client = Client();
        var id = await FileIdAsync("Testband/2024 - Testalbum/02 - Zweiter Song.flac");

        var response = await client.SendAsync(Get($"/api/player/files/{id}/stream", 4), Ct);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("file", response.Headers.GetValues(PlayerApi.SourceHeader).Single());
        Assert.Equal("audio/flac", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(_navidrome.Requests);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/player/files/{id}/scrobble", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Fehler_von_Navidrome_fallen_auf_die_Datei_zurueck()
    {
        _navidrome.Fail = true;
        var client = Client();
        var id = await FileIdAsync(FirstSong);

        var response = await client.GetAsync($"/api/player/files/{id}/stream", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("file", response.Headers.GetValues(PlayerApi.SourceHeader).Single());
        Assert.Equal(new FileInfo(Path.Combine(_library.Library, FirstSong)).Length, (await response.Content.ReadAsByteArrayAsync(Ct)).Length);
    }

    [Fact]
    public async Task Ohne_Navidrome_spielt_die_Datei()
    {
        var client = Client(navidrome: false);
        var id = await FileIdAsync(FirstSong);

        var response = await client.GetAsync($"/api/player/files/{id}/stream", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("file", response.Headers.GetValues(PlayerApi.SourceHeader).Single());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/player/files/999/stream", Ct)).StatusCode);
    }

    private sealed class FakeNavidrome : HttpMessageHandler
    {
        public List<(Uri Uri, string? Range)> Requests { get; } = [];
        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, request.Headers.Range?.ToString()));
            if (Fail)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""<subsonic-response status="failed"><error code="70"/></subsonic-response>""", null, "text/xml"),
                });
            if (request.RequestUri!.AbsolutePath == "/rest/scrobble")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"subsonic-response":{"status":"ok","version":"1.16.1"}}""", null, "application/json"),
                });

            var content = new ByteArrayContent(NavidromeAudio);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            content.Headers.ContentRange = new ContentRangeHeaderValue(2, 5, 6);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }
}
