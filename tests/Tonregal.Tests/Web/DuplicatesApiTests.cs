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
using Tonregal.Web.Api;
using Tonregal.Web.Library;

namespace Tonregal.Tests.Web;

public sealed class DuplicatesApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestLibrary _library = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DuplicatesApiTests()
    {
        _library.Copy("song1.mp3", Path.Combine(_library.Library, "Testband", "Kopien"), "01 - Erster Song.mp3");
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

    private async Task WaitForScanAsync()
    {
        var scans = _factory.Services.GetRequiredService<LibraryScanService>();
        for (var i = 0; i < 100 && scans.Status.LastRunAt is null; i++)
            await Task.Delay(50, Ct);
        Assert.NotNull(scans.Status.LastResult);
    }

    [Fact]
    public async Task Gruppe_anzeigen_vorhoeren_und_entscheiden()
    {
        await WaitForScanAsync();

        var groups = await _client.GetFromJsonAsync<List<DuplicateGroupDto>>("/api/duplicates", Json, Ct);
        var group = Assert.Single(groups!);
        Assert.Equal(DuplicateKind.ExactHash, group.Kind);
        Assert.Equal(2, group.Files.Count);
        Assert.All(group.Files, f => Assert.Equal("Erster Song", f.Title));
        var keep = group.Files.Single(f => f.Id == group.SuggestedFileId);
        var drop = group.Files.Single(f => f.Id != group.SuggestedFileId);
        Assert.Equal("Testband/2024 - Testalbum/01 - Erster Song.mp3", keep.Path);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/library/files/{drop.Id}/audio");
        request.Headers.Range = new RangeHeaderValue(0, 99);
        using var audio = await _client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, audio.StatusCode);
        Assert.Equal("audio/mpeg", audio.Content.Headers.ContentType?.MediaType);
        Assert.Equal(100, (await audio.Content.ReadAsByteArrayAsync(Ct)).Length);

        var invalid = await _client.PostAsJsonAsync($"/api/duplicates/{group.Id}/resolve", new ResolveDuplicateRequest([]), Ct);
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
        var resolved = await _client.PostAsJsonAsync($"/api/duplicates/{group.Id}/resolve", new ResolveDuplicateRequest([keep.Id]), Ct);
        Assert.Equal(HttpStatusCode.NoContent, resolved.StatusCode);

        Assert.Empty((await _client.GetFromJsonAsync<List<DuplicateGroupDto>>("/api/duplicates", Json, Ct))!);
        Assert.False(File.Exists(Path.Combine(_library.Library, drop.Path)));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/library/files/{drop.Id}/audio", Ct)).StatusCode);
        var stats = await _client.GetFromJsonAsync<LibraryStats>("/api/stats", Json, Ct);
        Assert.Equal((4, 0), (stats!.Files, stats.OpenDuplicates));
    }

    [Fact]
    public async Task Alle_Vorschlaege_auf_einmal_uebernehmen()
    {
        await WaitForScanAsync();
        _library.Copy("song2.flac", Path.Combine(_library.Library, "Testband", "Kopien"), "02 - Zweiter Song.flac");
        var scans = _factory.Services.GetRequiredService<LibraryScanService>();
        var before = scans.Status.LastRunAt;
        await _client.PostAsync("/api/duplicates/scan", null, Ct);
        for (var i = 0; i < 100 && scans.Status.LastRunAt == before; i++)
            await Task.Delay(50, Ct);

        var groups = (await _client.GetFromJsonAsync<List<DuplicateGroupDto>>("/api/duplicates", Json, Ct))!;
        Assert.Equal(2, groups.Count);
        var drops = groups.SelectMany(g => g.Files.Where(f => f.Id != g.SuggestedFileId)).ToList();

        var response = await _client.PostAsJsonAsync(
            "/api/duplicates/resolve-suggested", new ResolveSuggestedRequest([.. groups.Select(g => g.Id), 999]), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ResolveSuggestedResult>(Json, Ct))!;
        Assert.Equal((2, 2), (result.Resolved, result.Trashed));
        Assert.Equal(999, Assert.Single(result.Skipped).GroupId);

        Assert.Empty((await _client.GetFromJsonAsync<List<DuplicateGroupDto>>("/api/duplicates", Json, Ct))!);
        Assert.All(drops, f => Assert.False(File.Exists(Path.Combine(_library.Library, f.Path))));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(_library.Library, ".trash"), "*", SearchOption.AllDirectories)
            .Count(f => f.Contains("Erster Song") || f.Contains("Zweiter Song")));
    }

    [Fact]
    public async Task Alle_Vorschlaege_ohne_SSD_aendert_nichts()
    {
        await WaitForScanAsync();
        var group = Assert.Single((await _client.GetFromJsonAsync<List<DuplicateGroupDto>>("/api/duplicates", Json, Ct))!);
        File.Delete(Path.Combine(_library.MusicRoot, ".tonregal-mount"));

        var response = await _client.PostAsJsonAsync("/api/duplicates/resolve-suggested", new ResolveSuggestedRequest([group.Id]), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single((await _client.GetFromJsonAsync<List<DuplicateGroupDto>>("/api/duplicates", Json, Ct))!);
    }
}
