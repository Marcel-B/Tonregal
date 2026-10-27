// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Tagging;
using Tonregal.Web.Api;
using Tonregal.Web.Library;

namespace Tonregal.Tests.Web;

public sealed class IngestApiTests : IDisposable
{
    private const string Key = "geheimer-key";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TestLibrary _library = new(populate: false);
    private WebApplicationFactory<Program>? _factory;

    public void Dispose()
    {
        _factory?.Dispose();
        _library.Dispose();
    }

    private HttpClient Client(string? key = Key, bool populated = false, bool configured = true)
    {
        if (populated)
        {
            _library.Dispose();
            _library = new TestLibrary();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing").UseSetting("Tonregal:MusicRoot", _library.MusicRoot);
            if (configured)
                b.UseSetting("Tonregal:Ingest:Keys:tonwerk-ai", Key);
        });
        var client = _factory.CreateClient();
        if (key is not null)
            client.DefaultRequestHeaders.Add(IngestApi.KeyHeader, key);
        return client;
    }

    private static MultipartFormDataContent Form(object metadata, string audio = "song1.mp3", bool cover = false)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(File.ReadAllBytes(Path.Combine(TestLibrary.TestData, audio))), "file", "render-final" + Path.GetExtension(audio) },
            { new StringContent(JsonSerializer.Serialize(metadata, Json)), "metadata" },
        };
        if (cover)
        {
            var image = new ByteArrayContent(File.ReadAllBytes(Path.Combine(TestLibrary.TestData, "cover.jpg")));
            image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(image, "cover", "cover.jpg");
        }

        return form;
    }

    private static async Task<IngestJobDto> WaitForImportAsync(HttpClient client, int id)
    {
        IngestJobDto? job = null;
        for (var i = 0; i < 200; i++)
        {
            job = await client.GetFromJsonAsync<IngestJobDto>($"/api/ingest/{id}", Json, Ct);
            if (job!.State != ImportState.Matched || job.Error is not null)
                break;
            await Task.Delay(50, Ct);
        }

        return job!;
    }

    [Fact]
    public async Task Uebernimmt_ein_Stueck_mit_Metadaten_und_Cover_in_die_Library()
    {
        var client = Client();
        var metadata = new
        {
            externalId = "song-42", title = "Nachtfahrt", artist = "Tonwerk", album = "Projekt Nacht", year = 2026, track = 3,
            genre = "Synthwave", bpm = 128.0, key = "Am", source = "tonwerk-ai",
        };

        var response = await client.PostAsync("/api/ingest", Form(metadata, cover: true), Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<IngestJobDto>(Json, Ct))!;
        Assert.Equal($"/api/ingest/{accepted.Id}", response.Headers.Location?.ToString());
        Assert.Equal("song-42", accepted.ExternalId);

        var job = await WaitForImportAsync(client, accepted.Id);
        Assert.Equal((ImportState.Imported, null), (job.State, job.Error));
        Assert.Equal("Tonwerk/2026 - Projekt Nacht/03 - Nachtfahrt.mp3", job.LibraryPath);
        var tags = new TagLibTagReader().Read(Path.Combine(_library.Library, job.LibraryPath!)).Tags;
        Assert.Equal(("Nachtfahrt", "Tonwerk", "Projekt Nacht", 2026, 3), (tags.Title, tags.AlbumArtist, tags.Album, tags.Year, tags.Track));
        Assert.Equal((128d, "Am", "Synthwave"), (tags.Bpm, tags.InitialKey, tags.Genre));
        Assert.True(File.Exists(Path.Combine(_library.Library, "Tonwerk", "2026 - Projekt Nacht", "cover.jpg")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_library.MusicRoot, "Inbox"), "*", SearchOption.AllDirectories));

        // Derselbe Aufruf noch einmal legt nichts neu an.
        var again = await client.PostAsync("/api/ingest", Form(metadata, cover: true), Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(accepted.Id, (await again.Content.ReadFromJsonAsync<IngestJobDto>(Json, Ct))!.Id);
        Assert.Single(Directory.EnumerateFiles(_library.Library, "*.mp3", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Ohne_Album_wird_es_eine_Single()
    {
        var client = Client();

        var response = await client.PostAsync("/api/ingest", Form(new { externalId = "x/1", title = "Skizze: 1", artist = "Tonwerk" }, "song2.flac"), Ct);

        var job = await WaitForImportAsync(client, (await response.Content.ReadFromJsonAsync<IngestJobDto>(Json, Ct))!.Id);
        Assert.Equal("Tonwerk/Skizze_ 1/Skizze_ 1.flac", job.LibraryPath);
        Assert.Equal("x/1", job.ExternalId);
    }

    [Fact]
    public async Task Byte_gleiche_Datei_in_der_Library_wird_verworfen()
    {
        var client = Client(populated: true);
        var scans = _factory!.Services.GetRequiredService<LibraryScanService>();
        for (var i = 0; i < 100 && scans.Status.LastRunAt is null; i++)
            await Task.Delay(50, Ct);

        var response = await client.PostAsync("/api/ingest", Form(new { externalId = "dup", title = "T", artist = "A" }), Ct);

        var job = (await response.Content.ReadFromJsonAsync<IngestJobDto>(Json, Ct))!;
        Assert.Equal(ImportState.Rejected, job.State);
        Assert.Contains("01 - Erster Song.mp3", job.Error);
        Assert.False(Directory.Exists(Path.Combine(_library.MusicRoot, "Inbox", "_api")) &&
            Directory.EnumerateFiles(Path.Combine(_library.MusicRoot, "Inbox", "_api"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Prueft_Key_und_Pflichtfelder()
    {
        var metadata = new { externalId = "a", title = "T", artist = "A" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(key: null).PostAsync("/api/ingest", Form(metadata), Ct)).StatusCode);
        _factory!.Dispose();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(key: "falsch").PostAsync("/api/ingest", Form(metadata), Ct)).StatusCode);
        _factory!.Dispose();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Client(configured: false).PostAsync("/api/ingest", Form(metadata), Ct)).StatusCode);
        _factory!.Dispose();

        var client = Client();
        var missing = await client.PostAsync("/api/ingest", Form(new { externalId = "a", title = "T" }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("artist", await missing.Content.ReadAsStringAsync(Ct));
        var foreign = await client.PostAsync("/api/ingest", Form(new { externalId = "a", title = "T", artist = "A", source = "andere" }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/ingest/999", Ct)).StatusCode);
    }
}
