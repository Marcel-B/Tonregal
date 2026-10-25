// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Duplicates;
using Tonregal.Infrastructure.Fingerprinting;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Import;
using Tonregal.Infrastructure.Metadata;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Infrastructure;

public static class DependencyInjection
{
    /// <param name="contentRoot">Basis für relative <see cref="TonregalOptions.MusicRoot"/>-Angaben.</param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        services.Configure<TonregalOptions>(configuration.GetSection(TonregalOptions.SectionName));
        services.PostConfigure<TonregalOptions>(o => o.MusicRoot = Path.GetFullPath(o.MusicRoot, contentRoot));

        var connectionString = configuration.GetConnectionString("Tonregal");
        services.AddDbContext<TonregalDbContext>((sp, o) =>
            o.UseSqlite(connectionString
                ?? $"Data Source={sp.GetRequiredService<IOptions<TonregalOptions>>().Value.Database}"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITagReader, TagLibTagReader>();
        services.AddSingleton<ITagWriter, TagLibTagWriter>();
        services.AddScoped<ILibraryScanner, LibraryScanner>();
        services.AddSingleton<InboxFileTracker>();
        services.AddScoped<IInboxImporter, InboxImporter>();
        services.AddScoped<ILibraryImporter, LibraryImporter>();
        services.AddScoped<IDuplicateService, DuplicateService>();
        services.AddMetadataServices(configuration);
        return services;
    }

    private static void AddMetadataServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MetadataOptions>(configuration.GetSection(MetadataOptions.SectionName));
        services.AddSingleton<IFingerprintService, FpcalcFingerprintService>();
        services.AddScoped<IMetadataLookup, MetadataLookup>();

        // Limits der Dienste: MusicBrainz 1 Anfrage/s, AcoustID 3/s; Cover Art Archive hat keins, trotzdem schonend.
        AddRateLimitedClient<MusicBrainzClient>(services, MusicBrainzClient.HttpClientName, MusicBrainzClient.BaseAddress, TimeSpan.FromSeconds(1.1));
        AddRateLimitedClient<AcoustIdClient>(services, AcoustIdClient.HttpClientName, AcoustIdClient.BaseAddress, TimeSpan.FromSeconds(0.34));
        AddRateLimitedClient<CoverArtArchiveClient>(services, CoverArtArchiveClient.HttpClientName, CoverArtArchiveClient.BaseAddress, TimeSpan.FromSeconds(0.2));
        services.AddScoped<ICoverArtSource>(sp => sp.GetRequiredService<CoverArtArchiveClient>());
    }

    private static void AddRateLimitedClient<TClient>(IServiceCollection services, string name, Uri baseAddress, TimeSpan interval)
        where TClient : class
    {
        services.AddKeyedSingleton(name, (sp, _) => new RequestGate(interval, sp.GetRequiredService<TimeProvider>()));
        services.AddHttpClient<TClient>((sp, client) =>
            {
                var contact = sp.GetRequiredService<IOptions<MetadataOptions>>().Value.Contact;
                var version = typeof(DependencyInjection).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
                client.BaseAddress = baseAddress;
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"Tonregal/{version} ( {contact} )");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            })
            .AddHttpMessageHandler(sp => new RateLimitedHandler(sp.GetRequiredKeyedService<RequestGate>(name)));
    }
}
