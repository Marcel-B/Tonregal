// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Tonregal.Application;
using Tonregal.Infrastructure;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Web.Api;
using Tonregal.Web.Hubs;
using Tonregal.Web.Library;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) =>
{
    var options = services.GetRequiredService<IOptions<TonregalOptions>>().Value;
    logger
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console();
    if (Directory.Exists(options.MusicRoot))
        logger.WriteTo.File(Path.Combine(options.Logs, "tonregal-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30);
});

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<LibraryScanService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LibraryScanService>());
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
    app.Logger.LogInformation("Tonregal gestartet, Library {Library}, Datenbank {Database}", options.Library, db.Database.GetDbConnection().DataSource);
}

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGroup("/api").MapTonregalApi().MapLibraryApi();
app.MapHub<StatusHub>("/hub");
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
