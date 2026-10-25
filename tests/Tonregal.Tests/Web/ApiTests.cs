// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Tonregal.Web.Api;

namespace Tonregal.Tests.Web;

public sealed class ApiTests : IDisposable
{
    private readonly string _musicRoot = Directory.CreateTempSubdirectory("tonregal-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests()
    {
        File.WriteAllText(Path.Combine(_musicRoot, ".tonregal-mount"), "");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Testing")
            .UseSetting("Tonregal:MusicRoot", _musicRoot));
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_musicRoot, recursive: true);
    }

    [Fact]
    public async Task Health_meldet_gemountete_Library()
    {
        var health = await _factory.CreateClient().GetFromJsonAsync<HealthResponse>("/api/health", TestContext.Current.CancellationToken);

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.True(health.LibraryMounted);
    }

    [Fact]
    public async Task Stats_auf_leerer_Datenbank()
    {
        var stats = await _factory.CreateClient().GetFromJsonAsync<LibraryStats>("/api/stats", TestContext.Current.CancellationToken);

        Assert.Equal(new LibraryStats(0, 0, 0, 0, 0, 0), stats);
        Assert.True(File.Exists(Path.Combine(_musicRoot, "app", "tonregal.db")));
    }
}
