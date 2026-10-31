// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prometheus;
using Serilog;
using Serilog.Events;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Analysis;
using Tonregal.Web.Api;
using Tonregal.Web.Backup;
using Tonregal.Web.Hubs;
using Tonregal.Web.Inbox;
using Tonregal.Web.Library;
using Tonregal.Web.Monitoring;

var builder = WebApplication.CreateBuilder(args);
var metrics = MonitoringSetup.MetricsEnabled(builder.Configuration);
if (metrics)
    MonitoringSetup.RegisterMetrics();

builder.Services.AddSerilog((services, logger) =>
{
    var options = services.GetRequiredService<IOptions<TonregalOptions>>().Value;
    logger
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteToLoki(builder.Configuration);
    if (Directory.Exists(options.MusicRoot))
        logger.WriteTo.File(Path.Combine(options.Logs, "tonregal-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30);
});

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<LibraryScanService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LibraryScanService>());
builder.Services.AddSingleton<InboxService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<InboxService>());
builder.Services.AddSingleton<AnalysisService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalysisService>());
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
builder.Services.AddProblemDetails();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var options = scope.ServiceProvider.GetRequiredService<IOptions<TonregalOptions>>().Value;
    if (!Directory.Exists(options.MusicRoot))
    {
        // Ohne gemountete SSD nicht starten, sonst entsteht Library und DB auf der internen Platte.
        // launchd startet den Dienst über KeepAlive/PathState neu, sobald der Pfad wieder da ist.
        if (!app.Environment.IsDevelopment())
            throw new DirectoryNotFoundException($"MusicRoot {options.MusicRoot} fehlt – ist die SSD gemountet?");
        Directory.CreateDirectory(options.MusicRoot);
    }

    var db = scope.ServiceProvider.GetRequiredService<TonregalDbContext>();
    var dbDirectory = Path.GetDirectoryName(db.Database.GetDbConnection().DataSource);
    if (!string.IsNullOrEmpty(dbDirectory))
        Directory.CreateDirectory(dbDirectory);
    await db.Database.MigrateAsync();
    try
    {
        await scope.ServiceProvider.GetRequiredService<ISmartPlaylists>().WriteFilesAsync(CancellationToken.None);
    }
    catch (IOException e)
    {
        app.Logger.LogWarning("Smart Playlists nicht geschrieben: {Message}", e.Message);
    }
    app.Logger.LogInformation("Tonregal gestartet, Library {Library}, Datenbank {Database}", options.Library, db.Database.GetDbConnection().DataSource);
    app.Logger.LogInformation("Monitoring: /metrics {Metrics}, Logs an Loki {Loki}",
        metrics ? "an" : "aus", string.IsNullOrEmpty(app.Configuration[MonitoringSetup.LokiUrlKey]) ? "aus" : app.Configuration[MonitoringSetup.LokiUrlKey]);
}

// Prometheus fragt alle paar Sekunden; diese Anfragen gehören nicht ins Protokoll.
app.UseSerilogRequestLogging(o => o.GetLevel = (context, _, error) =>
    error is not null || context.Response.StatusCode >= 500 ? LogEventLevel.Error
    : context.Request.Path == "/metrics" ? LogEventLevel.Verbose
    : LogEventLevel.Information);
if (metrics)
{
    MonitoringSetup.UseServices(app.Services);
    app.UseHttpMetrics();
}
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGroup("/api").MapTonregalApi().MapLibraryApi().MapMatchApi().MapInboxApi().MapDuplicatesApi().MapAnalysisApi().MapBackupApi().MapPlayerApi().MapIngestApi().MapSettingsApi().MapSmartPlaylistsApi().MapListeningApi();
app.MapHub<StatusHub>("/hub");
if (metrics)
    app.MapMetrics();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
