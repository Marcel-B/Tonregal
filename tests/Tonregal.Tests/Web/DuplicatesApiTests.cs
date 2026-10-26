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
}
