// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tonregal.Infrastructure.Persistence;

/// <summary>Nur für <c>dotnet ef migrations</c>.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TonregalDbContext>
{
    public TonregalDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TonregalDbContext>().UseSqlite("Data Source=design.db").Options);
}
