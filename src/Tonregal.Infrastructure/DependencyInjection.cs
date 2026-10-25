// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Infrastructure.Persistence;

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
        return services;
    }
}
