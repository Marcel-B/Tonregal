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
using Tonregal.Web.Inbox;

namespace Tonregal.Tests.Web;

/// <summary>Echte Pipeline mit fpcalc, aber ohne AcoustID-Key: Jobs bleiben nach dem Fingerprint stehen.</summary>
public sealed class InboxApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestLibrary _library = new(populate: false);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public InboxApiTests()
    {
        _library.Copy("song1.mp3", Path.Combine(_library.MusicRoot, "Inbox", "Testalbum"), "01.mp3");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Testing")
            .UseSetting("Tonregal:MusicRoot", _library.MusicRoot)
            .UseSetting("Tonregal:InboxStableSeconds", "0")
            .UseSetting("Tonregal:Metadata:AcoustIdApiKey", ""));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _library.Dispose();
    }

    private async Task<InboxStatus> WaitForRunAsync()
    {
        var service = _factory.Services.GetRequiredService<InboxService>();
        for (var i = 0; i < 200 && (service.Status.LastRunAt is null || service.Status.Running); i++)
            await Task.Delay(50, Ct);
        Assert.NotNull(service.Status.LastRunAt);
        return service.Status;
    }

    [Fact]
    public async Task Inbox_listet_Jobs_und_meldet_fehlenden_Key()
    {
        var status = await WaitForRunAsync();

        Assert.Contains("API-Key", status.LastError);
        var inbox = await _client.GetFromJsonAsync<InboxResponse>("/api/inbox", Json, Ct);
        var job = Assert.Single(inbox!.Jobs);
        Assert.Equal(("Testalbum/01.mp3", "Testalbum"), (job.Path, job.Group));
        Assert.Equal(ImportState.Fingerprinted, job.State);
        Assert.Equal("Erster Song", job.TagTitle);
        Assert.Contains("API-Key", job.Error);
        Assert.Null(job.Selected);

        var candidates = await _client.GetFromJsonAsync<ImportJobCandidates>($"/api/inbox/{job.Id}/candidates", Json, Ct);
        Assert.Empty(candidates!.Candidates);
    }

    [Fact]
    public async Task Retry_setzt_den_Job_zurueck()
    {
        await WaitForRunAsync();
        var job = (await _client.GetFromJsonAsync<InboxResponse>("/api/inbox", Json, Ct))!.Jobs[0];

        var response = await _client.PostAsync($"/api/inbox/{job.Id}/retry", null, Ct);
        var missing = await _client.PostAsync("/api/inbox/999/retry", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ImportState.New, (await response.Content.ReadFromJsonAsync<ImportJobDto>(Json, Ct))!.State);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Verworfene_Datei_bekommt_keinen_neuen_Job()
    {
        await WaitForRunAsync();
        var job = (await _client.GetFromJsonAsync<InboxResponse>("/api/inbox", Json, Ct))!.Jobs[0];

        var reject = await _client.PostAsync($"/api/inbox/{job.Id}/reject", null, Ct);
        var select = await _client.PostAsJsonAsync($"/api/inbox/{job.Id}/select", new SelectCandidateRequest(Guid.NewGuid(), Guid.NewGuid()), Ct);
        var import = await _client.PostAsJsonAsync("/api/inbox/import", new ImportRequest([job.Id]), Ct);
        _factory.Services.GetRequiredService<InboxService>().RequestRun();
        await Task.Delay(500, Ct);
        await WaitForRunAsync();

        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, select.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, import.StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<InboxResponse>("/api/inbox", Json, Ct))!.Jobs);
        Assert.True(File.Exists(Path.Combine(_library.MusicRoot, "Inbox", "Testalbum", "01.mp3")));
    }

    [Fact]
    public async Task Bericht_listet_Problemfaelle_und_alle_lassen_sich_neu_pruefen()
    {
        await WaitForRunAsync();

        var report = await _client.GetStringAsync("/api/inbox/report", Ct);
        var retry = await _client.PostAsync("/api/inbox/retry-all", null, Ct);

        Assert.StartsWith("Tonregal Inbox – 1 Datei(en) mit Problemen", report);
        Assert.Contains("Testalbum/01.mp3", report);
        Assert.Contains("  Fehler: AcoustID-API-Key fehlt", report);
        Assert.Contains("  Tags: Testband – Erster Song · Testalbum", report);
        Assert.Equal(1, (await retry.Content.ReadFromJsonAsync<RetryAllResponse>(Json, Ct))!.Count);
    }

    [Fact]
    public async Task Upload_legt_Dateien_mit_Ordner_in_die_Inbox()
    {
        async Task<HttpResponseMessage> UploadAsync(string path, string testFile = "song2.flac")
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(TestLibrary.TestData, testFile), Ct)), "file", Path.GetFileName(path));
            form.Add(new StringContent(path), "path");
            return await _client.PostAsync("/api/inbox/upload", form, Ct);
        }

        var first = await UploadAsync("Neues Album/02 - Song.flac");
        var second = await UploadAsync("Neues Album/02 - Song.flac");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("Neues Album/02 - Song.flac", (await first.Content.ReadFromJsonAsync<UploadResponse>(Json, Ct))!.Path);
        Assert.Equal("Neues Album/02 - Song (2).flac", (await second.Content.ReadFromJsonAsync<UploadResponse>(Json, Ct))!.Path);
        Assert.True(File.Exists(Path.Combine(_library.MusicRoot, "Inbox", "Neues Album", "02 - Song.flac")));
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync("../ausserhalb.flac")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync("Album/notizen.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync("_api/x.flac")).StatusCode);
        Assert.False(File.Exists(Path.Combine(_library.MusicRoot, "ausserhalb.flac")));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_library.MusicRoot, "Inbox", "Neues Album")), f => Path.GetFileName(f).StartsWith('.'));
    }

    [Fact]
    public async Task Altbestand_hat_eine_eigene_Liste_und_einen_Stand()
    {
        await WaitForRunAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/inbox/legacy/scan", null, Ct)).StatusCode);

        _library.Copy("song2.flac", Path.Combine(_library.MusicRoot, "Altbestand", "Alt"), "02.flac");
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsync("/api/inbox/legacy/scan", null, Ct)).StatusCode);
        LegacySummary? summary = null;
        for (var i = 0; i < 200 && summary?.Total != 1; i++)
        {
            await Task.Delay(50, Ct);
            summary = await _client.GetFromJsonAsync<LegacySummary>("/api/inbox/legacy", Json, Ct);
        }

        Assert.Equal(1, summary!.Total);
        Assert.True(summary.Exists);
        var legacy = await _client.GetFromJsonAsync<InboxResponse>("/api/inbox?source=Legacy", Json, Ct);
        Assert.Equal("Alt/02.flac", Assert.Single(legacy!.Jobs).Path);
        var inbox = await _client.GetFromJsonAsync<InboxResponse>("/api/inbox", Json, Ct);
        Assert.Equal("Testalbum/01.mp3", Assert.Single(inbox!.Jobs).Path);
    }
}
