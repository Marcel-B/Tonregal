// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Api;
using Tonregal.Web.Inbox;
using Tonregal.Web.Library;
using Tonregal.Web.Monitoring;

namespace Tonregal.Tests.Web;

/// <summary>Die Tests der Messwerte laufen allein: Die Bestands-Gauges lesen die Datenbank der zuletzt gestarteten App.</summary>
[CollectionDefinition(nameof(MonitoringTests), DisableParallelization = true)]
public sealed class MonitoringCollection;

/// <summary>
/// <c>/metrics</c> und der Versand an Loki. Messwerte sind prozessweit, die Tests vergleichen deshalb Differenzen.
/// </summary>
[Collection(nameof(MonitoringTests))]
public sealed class MonitoringTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestLibrary _library = new();
    private WebApplicationFactory<Program>? _factory;

    public void Dispose()
    {
        _factory?.Dispose();
        _library.Dispose();
    }

    private HttpClient Client(bool metrics = true, string? loki = null)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing")
                .UseSetting("Tonregal:MusicRoot", _library.MusicRoot)
                .UseSetting("Tonregal:InboxStableSeconds", "0")
                .UseSetting("Tonregal:Ingest:Keys:tonwerk-ai", "key")
                .UseSetting(MonitoringSetup.MetricsKey, metrics ? "true" : "false");
            if (loki is not null)
                b.UseSetting(MonitoringSetup.LokiUrlKey, loki).UseSetting(MonitoringSetup.LokiHostKey, "probe");
        });
        return _factory.CreateClient();
    }

    private async Task<string> MetricsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/metrics", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static double Value(string text, string series)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith(series + " ", StringComparison.Ordinal));
        return line is null ? double.NaN : double.Parse(line[(series.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task WaitForScanAsync()
    {
        var scan = _factory!.Services.GetRequiredService<LibraryScanService>();
        for (var i = 0; i < 200 && (scan.Status.LastRunAt is null || scan.Status.Running); i++)
            await Task.Delay(50, Ct);
        Assert.NotNull(scan.Status.LastResult);
    }

    [Fact]
    public async Task Ohne_Monitoring_Metrics_gibt_es_keinen_Endpunkt()
    {
        var client = Client(metrics: false);

        var response = await client.GetAsync("/metrics", Ct);

        // Die SPA-Fallback-Route liefert sonst index.html; /metrics darf es nicht geben.
        Assert.False(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync(Ct)).Contains("# TYPE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Metrics_nennt_alle_Namen_und_den_Bestand()
    {
        var client = Client();
        await WaitForScanAsync();

        var text = await MetricsAsync(client);

        foreach (var name in new[]
                 {
                     "tonregal_inbox_files_total", "tonregal_inbox_waiting", "tonregal_ingest_total", "tonregal_musicbrainz_requests_total",
                     "tonregal_scan_runs_total", "tonregal_scan_duration_seconds", "tonregal_analysis_total", "tonregal_analysis_waiting",
                     "tonregal_tracks", "tonregal_duplicates", "tonregal_library_bytes", "tonregal_backup_runs_total",
                     "tonregal_backup_last_success_timestamp_seconds", "tonregal_music_root_mounted", "http_requests_received_total",
                 })
            Assert.Contains($"# TYPE {name} ", text);

        await using var scope = _factory!.Services.CreateAsyncScope();
        var tracks = await scope.ServiceProvider.GetRequiredService<TonregalDbContext>().Tracks.CountAsync(Ct);
        Assert.True(tracks > 0);
        Assert.Equal(tracks, Value(text, "tonregal_tracks"));
        Assert.Equal(1, Value(text, "tonregal_music_root_mounted"));
        Assert.True(Value(text, "tonregal_scan_runs_total{result=\"success\"}") >= 1);
        // Keine eingebauten ASP.NET-Meter neben UseHttpMetrics.
        Assert.DoesNotContain("microsoft_aspnetcore", text);
    }

    [Fact]
    public async Task Eine_Inbox_Datei_zaehlt_hoch()
    {
        var client = Client();
        await WaitForScanAsync();
        const string series = "tonregal_inbox_files_total{result=\"duplicate\"}";
        var before = Value(await MetricsAsync(client), series);

        // Dieselbe Datei liegt schon in der Library: Sie wird ohne AcoustID als Dublette verworfen.
        _library.Copy("song1.mp3", Path.Combine(_library.MusicRoot, "Inbox", "Nochmal"), "01.mp3");
        _factory!.Services.GetRequiredService<InboxService>().RequestRun();

        var after = before;
        for (var i = 0; i < 200 && after <= before; i++)
        {
            await Task.Delay(50, Ct);
            after = Value(await MetricsAsync(client), series);
        }

        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task Abgewiesene_Uebergabe_zaehlt_als_refused()
    {
        var client = Client();
        const string series = "tonregal_ingest_total{result=\"refused\"}";
        var before = Value(await MetricsAsync(client), series);

        var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "a.mp3" }, { new StringContent("{}"), "metadata" } };
        form.Headers.Add(IngestApi.KeyHeader, "falsch");
        var response = await client.PostAsync("/api/ingest", form, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before + 1, Value(await MetricsAsync(client), series));
    }

    [Theory]
    [InlineData(ImportState.Matched, null, "matched")]
    [InlineData(ImportState.Imported, null, "imported")]
    [InlineData(ImportState.NeedsReview, null, "unmatched")]
    [InlineData(ImportState.NeedsReview, "kaputt", "failed")]
    [InlineData(ImportState.Rejected, ImportJob.AlreadyInLibrary + "x", "duplicate")]
    [InlineData(ImportState.Rejected, ImportJob.VanishedReason, null)]
    public void Ergebnis_einer_Inbox_Datei(ImportState state, string? error, string? result) =>
        Assert.Equal(result, InboxService.ResultOf(new ImportJob { SourcePath = "a.mp3", State = state, Error = error }));

    [Theory]
    [InlineData("Tonregal.Web.Inbox.InboxService", "inbox")]
    [InlineData("Tonregal.Infrastructure.Metadata.MusicBrainzClient", "inbox")]
    [InlineData("Tonregal.Infrastructure.Import.IngestService", "ingest")]
    [InlineData("Tonregal.Web.Library.LibraryScanService", "scan")]
    [InlineData("Tonregal.Infrastructure.Duplicates.DuplicateService", "scan")]
    [InlineData("Tonregal.Web.Analysis.AnalysisService", "analysis")]
    [InlineData("Tonregal.Web.Backup.BackupService", "backup")]
    [InlineData("Tonregal.Infrastructure.Navidrome.NavidromeClient", "navidrome")]
    [InlineData("Microsoft.Hosting.Lifetime", "server")]
    [InlineData(null, "server")]
    public void Quelle_aus_dem_Namensraum(string? context, string source) => Assert.Equal(source, MonitoringSetup.SourceOf(context));

    [Fact]
    public async Task Logzeilen_gehen_mit_vier_Labels_an_Loki()
    {
        using var loki = new FakeLoki();
        Client(metrics: false, loki: loki.Url);
        await WaitForScanAsync();

        JsonElement[] streams = [];
        // Der Sink schickt alle 5 s.
        for (var i = 0; i < 600 && !streams.Any(s => s.GetProperty("stream").GetProperty("source").GetString() == "scan"); i++)
        {
            await Task.Delay(50, Ct);
            streams = loki.Streams();
        }

        Assert.NotEmpty(streams);
        Assert.All(streams, s =>
        {
            var labels = s.GetProperty("stream").EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.Equal(["app", "host", "level", "source"], labels);
            Assert.Equal("tonregal", s.GetProperty("stream").GetProperty("app").GetString());
            Assert.Equal("probe", s.GetProperty("stream").GetProperty("host").GetString());
        });
        var started = streams.SelectMany(s => s.GetProperty("values").EnumerateArray()).Select(v => v[1].GetString()!);
        Assert.Contains(started, line => line.StartsWith("Tonregal gestartet", StringComparison.Ordinal));
        Assert.Contains(streams, s => s.GetProperty("stream").GetProperty("level").GetString() == "info");
    }

    /// <summary>Nimmt Pushes an <c>/loki/api/v1/push</c> an, wie Loki mit 204.</summary>
    private sealed class FakeLoki : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<JsonElement> _streams = [];

        public FakeLoki()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public JsonElement[] Streams()
        {
            lock (_streams)
                return [.. _streams];
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                if (context.Request.Url?.AbsolutePath == "/loki/api/v1/push")
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                    lock (_streams)
                        _streams.AddRange(body.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.Clone()));
                }

                context.Response.StatusCode = 204;
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        public void Dispose() => _listener.Close();
    }
}
