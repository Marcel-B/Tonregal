// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Lyrics;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Tests.Metadata;
using Tonregal.Web.Library;

namespace Tonregal.Tests.Web;

/// <summary>Gegen ein Fake-lrclib.net (<see cref="StubHandler"/>).</summary>
public sealed class LyricsApiTests : IDisposable
{
    private const string FirstSong = "Testband/2024 - Testalbum/01 - Erster Song.mp3";
    private const string Synced = "[00:01.00]Erste Zeile\n[00:04.50]Zweite Zeile\n";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestLibrary _library = new();
    private WebApplicationFactory<Program>? _factory;
    private Func<HttpRequestMessage, HttpResponseMessage> _lrclib = _ => StubHandler.Json("""{"code":404}""", HttpStatusCode.NotFound);
    private StubHandler? _handler;

    public void Dispose()
    {
        _factory?.Dispose();
        _library.Dispose();
    }

    private string Sidecar => Path.Combine(_library.AlbumFolder, "01 - Erster Song.lrc");

    private HttpClient Client(params (string Key, string Value)[] settings)
    {
        _handler = new StubHandler(r => _lrclib(r));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing").UseSetting("Tonregal:MusicRoot", _library.MusicRoot);
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);
            b.ConfigureTestServices(services => services
                .AddHttpClient<LrcLibClient>()
                .ConfigurePrimaryHttpMessageHandler(() => _handler));
        });
        return _factory.CreateClient();
    }

    private async Task<int> TrackIdAsync(string path = FirstSong)
    {
        var scans = _factory!.Services.GetRequiredService<LibraryScanService>();
        for (var i = 0; i < 100 && scans.Status.LastRunAt is null; i++)
            await Task.Delay(50, Ct);
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TonregalDbContext>().AudioFiles
            .Where(f => f.Path == path).Select(f => f.TrackId).SingleAsync(Ct);
    }

    private static async Task<SongLyrics> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SongLyrics>(Json, Ct))!;
    }

    [Fact]
    public async Task Holt_synchronisierten_Text_von_lrclib_und_legt_lrc_ab()
    {
        _lrclib = _ => StubHandler.Json($$"""{"id":1,"trackName":"Erster Song","artistName":"Testband","albumName":"Testalbum","duration":3,"instrumental":false,"plainLyrics":"Erste Zeile\nZweite Zeile","syncedLyrics":{{JsonSerializer.Serialize(Synced)}}}""");
        var client = Client();
        var id = await TrackIdAsync();

        var lyrics = await ReadAsync(await client.GetAsync($"/api/player/tracks/{id}/lyrics", Ct));
        var again = await ReadAsync(await client.GetAsync($"/api/player/tracks/{id}/lyrics", Ct));

        Assert.Equal(LyricsSource.Lrclib, lyrics.Source);
        Assert.True(lyrics.Synced);
        Assert.Equal([new LyricLine(1000, "Erste Zeile"), new LyricLine(4500, "Zweite Zeile")], lyrics.Lines);
        Assert.Equal(Synced, File.ReadAllText(Sidecar));
        Assert.Equal(lyrics.Lines, again.Lines);

        var request = Assert.Single(_handler!.Requests).Request;
        Assert.Equal("/api/get", request.RequestUri!.AbsolutePath);
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        Assert.Equal(("Testband", "Erster Song", "Testalbum"), (query["artist_name"], query["track_name"], query["album_name"]));
    }

    [Fact]
    public async Task Ohne_Treffer_wird_nicht_bei_jedem_Abspielen_neu_gesucht()
    {
        _lrclib = r => r.RequestUri!.AbsolutePath.EndsWith("/search")
            ? StubHandler.Json("[]")
            : StubHandler.Json("""{"code":404}""", HttpStatusCode.NotFound);
        var client = Client();
        var id = await TrackIdAsync();

        var lyrics = await ReadAsync(await client.GetAsync($"/api/player/tracks/{id}/lyrics", Ct));
        await ReadAsync(await client.GetAsync($"/api/player/tracks/{id}/lyrics", Ct));

        Assert.Equal(LyricsSource.None, lyrics.Source);
        Assert.Empty(lyrics.Lines);
        Assert.Equal(["/api/get", "/api/search"], _handler!.Requests.Select(r => r.Request.RequestUri!.AbsolutePath));
        Assert.False(File.Exists(Sidecar));

        // Eine später abgelegte .lrc zählt sofort.
        File.WriteAllText(Sidecar, "Von Hand\n");
        var manual = await ReadAsync(await client.GetAsync($"/api/player/tracks/{id}/lyrics", Ct));
        Assert.Equal((LyricsSource.Sidecar, "Von Hand"), (manual.Source, manual.Lines.Single().Text));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Vorhandene_lrc_gilt_ohne_Anfrage()
    {
        File.WriteAllText(Sidecar, Synced);
        var client = Client();
        var id = await TrackIdAsync();

        var lyrics = await ReadAsync(await client.GetAsync($"/api/player/tracks/{id}/lyrics", Ct));

        Assert.Equal((LyricsSource.Sidecar, true, 2), (lyrics.Source, lyrics.Synced, lyrics.Lines.Count));
        Assert.Empty(_handler!.Requests);
    }

    [Fact]
    public async Task Eingebetteter_Text_ohne_lrclib_Treffer()
    {
        using (var file = TagLib.File.Create(Path.Combine(_library.AlbumFolder, "02 - Zweiter Song.flac")))
        {
            file.Tag.Lyrics = "Eingebettet\nZweite";
            file.Save();
        }

        var client = Client(("Tonregal:Lyrics:Online", "false"));
        var id = await TrackIdAsync("Testband/2024 - Testalbum/02 - Zweiter Song.flac");

        var lyrics = await ReadAsync(await client.GetAsync($"/api/player/tracks/{id}/lyrics", Ct));

        Assert.Equal((LyricsSource.Tag, false), (lyrics.Source, lyrics.Synced));
        Assert.Equal(["Eingebettet", "Zweite"], lyrics.Lines.Select(l => l.Text));
        Assert.Empty(_handler!.Requests);
    }

    [Fact]
    public async Task Neu_suchen_ersetzt_die_lrc()
    {
        File.WriteAllText(Sidecar, "Falscher Text\n");
        _lrclib = _ => StubHandler.Json($$"""{"id":2,"duration":3,"instrumental":false,"plainLyrics":null,"syncedLyrics":{{JsonSerializer.Serialize(Synced)}}}""");
        var client = Client();
        var id = await TrackIdAsync();

        var before = await ReadAsync(await client.GetAsync($"/api/player/tracks/{id}/lyrics", Ct));
        var after = await ReadAsync(await client.PostAsync($"/api/player/tracks/{id}/lyrics/refresh", null, Ct));

        Assert.Equal(LyricsSource.Sidecar, before.Source);
        Assert.Equal((LyricsSource.Lrclib, true), (after.Source, after.Synced));
        Assert.Equal(Synced, File.ReadAllText(Sidecar));
    }

    [Fact]
    public async Task Fehler_und_unbekannte_Tracks()
    {
        _lrclib = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var client = Client();
        var id = await TrackIdAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/api/player/tracks/{id}/lyrics", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/player/tracks/9999/lyrics", Ct)).StatusCode);

        // Nichts gespeichert: beim nächsten Mal wird wieder gefragt.
        _lrclib = _ => StubHandler.Json("""{"id":3,"duration":3,"instrumental":true,"plainLyrics":null,"syncedLyrics":null}""");
        var lyrics = await ReadAsync(await client.GetAsync($"/api/player/tracks/{id}/lyrics", Ct));
        Assert.True(lyrics.Instrumental);
    }
}
