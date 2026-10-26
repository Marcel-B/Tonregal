// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Tonregal.Web.Api;

namespace Tonregal.Tests.Web;

public sealed class BackupApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly TestLibrary _library = new(populate: false);
    private readonly WebApplicationFactory<Program> _factory;

    public BackupApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Testing")
            .UseSetting("Tonregal:MusicRoot", _library.MusicRoot)
            .UseSetting("Tonregal:Backup:Time", "03:30"));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _library.Dispose();
    }

    [Fact]
    public async Task Zeigt_den_naechsten_Lauf_und_die_Historie()
    {
        var backup = await _factory.CreateClient().GetFromJsonAsync<BackupResponse>("/api/backup", Json, TestContext.Current.CancellationToken);

        Assert.NotNull(backup);
        Assert.Empty(backup.Runs);
        var next = Assert.NotNull(backup.Status.NextRunAt);
        Assert.Equal(new TimeOnly(3, 30), TimeOnly.FromDateTime(next.LocalDateTime));
        Assert.InRange(next - DateTimeOffset.Now, TimeSpan.Zero, TimeSpan.FromDays(1));
    }
}
