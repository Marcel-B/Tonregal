// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Tonregal.Web.Analysis;
using Tonregal.Web.Api;

namespace Tonregal.Tests.Web;

public sealed class AnalysisApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly TestLibrary _library = new(populate: false);
    private readonly WebApplicationFactory<Program> _factory;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AnalysisApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Testing")
            .UseSetting("Tonregal:MusicRoot", _library.MusicRoot)
            .UseSetting("Tonregal:Analysis:RunAtNight", "false")
            .UseSetting("Tonregal:Analysis:NightStart", "23:30"));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _library.Dispose();
    }

    [Fact]
    public async Task Zeigt_Einstellungen_und_laesst_sich_stoppen()
    {
        var client = _factory.CreateClient();

        var analysis = await client.GetFromJsonAsync<AnalysisResponse>("/api/analysis", Json, Ct);
        Assert.Equal(new AnalysisSettings(false, "23:30", "06:00", false), analysis!.Settings);

        var stopped = await (await client.PostAsync("/api/analysis/stop", null, Ct)).Content.ReadFromJsonAsync<AnalysisStatus>(Json, Ct);
        Assert.False(stopped!.Requested);
    }
}
