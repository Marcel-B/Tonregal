// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prometheus;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Sinks.Grafana.Loki;
using Tonregal.Application;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Monitoring;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Web.Monitoring;

/// <summary>
/// Anbindung an den Monitoring-Stack (Prometheus, Loki, Grafana; Tonwerk-AI <c>deploy/monitoring/</c>):
/// <c>Monitoring:Metrics</c> schaltet <c>GET /metrics</c> ein, <c>Monitoring:Loki:Url</c> den Versand der
/// Logzeilen. Beides ist ohne Eintrag aus.
/// </summary>
public static class MonitoringSetup
{
    public const string MetricsKey = "Monitoring:Metrics";
    public const string LokiUrlKey = "Monitoring:Loki:Url";
    public const string LokiHostKey = "Monitoring:Loki:Host";

    // Die T7 hängt per USB; die Library höchstens stündlich durchzählen.
    private static readonly TimeSpan LibrarySizeInterval = TimeSpan.FromHours(1);

    private static readonly Lock Gate = new();
    private static bool _registered;
    private static IServiceProvider? _services;
    private static DateTimeOffset _librarySizedAt = DateTimeOffset.MinValue;
    private static int _sizing;

    public static bool MetricsEnabled(IConfiguration configuration) => configuration.GetValue<bool>(MetricsKey);

    /// <summary>
    /// Vor dem ersten Messwert aufrufen. UseHttpMetrics misst die Anfragen schon; die eingebauten Meter von
    /// ASP.NET Core wiederholten das nur unter anderen Namen. Die Messwerte sind prozessweit, also nur einmal
    /// (Tests starten die App mehrfach).
    /// </summary>
    public static void RegisterMetrics()
    {
        lock (Gate)
        {
            if (_registered)
                return;
            Metrics.SuppressDefaultMetrics(new SuppressDefaultMetricOptions { SuppressMeters = true });
            TonregalMetrics.Register();
            Metrics.DefaultRegistry.AddBeforeCollectCallback(CollectAsync);
            _registered = true;
        }
    }

    /// <summary>Die App, deren Datenbank die Bestands-Gauges beim Scrape lesen (die zuletzt gestartete).</summary>
    public static void UseServices(IServiceProvider services) => _services = services;

    /// <summary>Bestand, Inbox und Mount bei jedem Scrape frisch; die Größe der Library im Hintergrund.</summary>
    private static async Task CollectAsync(CancellationToken ct)
    {
        if (_services is not { } services)
            return;
        try
        {
            var options = services.GetRequiredService<IOptions<TonregalOptions>>().Value;
            var mounted = File.Exists(options.MountMarker);
            TonregalMetrics.MusicRootMounted.Set(mounted ? 1 : 0);

            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TonregalDbContext>();
            TonregalMetrics.Tracks.Set(await db.Tracks.CountAsync(ct));
            TonregalMetrics.Duplicates.Set(await db.DuplicateGroups.CountAsync(ct));
            var states = await db.ImportJobs
                .Where(j => j.Source == FileSource.Inbox && ImportJob.ActiveStates.Contains(j.State))
                .GroupBy(j => j.State)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);
            int Count(params ImportState[] wanted) => states.Where(s => wanted.Contains(s.Key)).Sum(s => s.Count);
            TonregalMetrics.InboxWaiting.WithLabels("new").Set(Count(ImportState.New, ImportState.Fingerprinted));
            TonregalMetrics.InboxWaiting.WithLabels("matched").Set(Count(ImportState.Matched));
            TonregalMetrics.InboxWaiting.WithLabels("review").Set(Count(ImportState.NeedsReview));

            if (mounted && DateTimeOffset.UtcNow - _librarySizedAt > LibrarySizeInterval && Interlocked.Exchange(ref _sizing, 1) == 0)
                _ = Task.Run(() => SizeLibrary(options.Library), CancellationToken.None);
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            // App beendet oder Datenbank gerade nicht lesbar: dann ohne Bestand, der Rest der Messwerte bleibt.
        }
    }

    private static void SizeLibrary(string library)
    {
        try
        {
            long bytes = 0;
            foreach (var file in new DirectoryInfo(library).EnumerateFiles("*", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                bytes += file.Length;
            TonregalMetrics.LibraryBytes.Set(bytes);
            _librarySizedAt = DateTimeOffset.UtcNow;
        }
        catch (IOException)
        {
            // SSD während des Zählens weg: beim nächsten Scrape erneut.
        }
        finally
        {
            Interlocked.Exchange(ref _sizing, 0);
        }
    }

    /// <summary>
    /// Logzeilen zusätzlich an Loki, wenn <c>Monitoring:Loki:Url</c> gesetzt ist. Labels nur <c>app</c>,
    /// <c>host</c>, <c>level</c> und <c>source</c>, wie bei den anderen Apps. Der Sink hat seinen eigenen
    /// HttpClient, der nicht über das Logging schreibt; sonst erzeugte jeder Versand neue Zeilen.
    /// </summary>
    public static LoggerConfiguration WriteToLoki(this LoggerConfiguration logger, IConfiguration configuration)
    {
        if (configuration[LokiUrlKey] is not { Length: > 0 } url)
            return logger;
        var host = configuration[LokiHostKey] is { Length: > 0 } configured ? configured : Environment.MachineName.Split('.')[0];
        return logger
            .Enrich.With<SourceEnricher>()
            .WriteTo.GrafanaLoki(
                url,
                labels: [new LokiLabel { Key = "app", Value = "tonregal" }, new LokiLabel { Key = "host", Value = host }],
                propertiesAsLabels: [SourceEnricher.Property],
                textFormatter: new MessageTemplateTextFormatter("{Message:lj}{NewLine}{Exception}"),
                period: TimeSpan.FromSeconds(5));
    }

    /// <summary>Quelle einer Logzeile aus dem Namensraum, wie Tonwerks <c>LogSources</c>; wenige feste Werte.</summary>
    public static string SourceOf(string? context) => context switch
    {
        null => "server",
        _ when context.Contains("Ingest", StringComparison.Ordinal) => "ingest",
        _ when context.StartsWith("Tonregal.Web.Inbox", StringComparison.Ordinal)
            || context.StartsWith("Tonregal.Infrastructure.Import", StringComparison.Ordinal)
            || context.StartsWith("Tonregal.Infrastructure.Metadata", StringComparison.Ordinal)
            || context.StartsWith("Tonregal.Infrastructure.Fingerprinting", StringComparison.Ordinal) => "inbox",
        _ when context.StartsWith("Tonregal.Web.Library", StringComparison.Ordinal)
            || context.StartsWith("Tonregal.Infrastructure.Library", StringComparison.Ordinal)
            || context.StartsWith("Tonregal.Infrastructure.Duplicates", StringComparison.Ordinal) => "scan",
        _ when context.Contains(".Analysis", StringComparison.Ordinal) => "analysis",
        _ when context.Contains(".Backup", StringComparison.Ordinal) => "backup",
        _ when context.Contains(".Navidrome", StringComparison.Ordinal) => "navidrome",
        _ => "server",
    };

    private sealed class SourceEnricher : ILogEventEnricher
    {
        public const string Property = "source";

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
        {
            var context = logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value)
                && value is ScalarValue { Value: string name } ? name : null;
            logEvent.AddPropertyIfAbsent(factory.CreateProperty(Property, SourceOf(context)));
        }
    }
}
