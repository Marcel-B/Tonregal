// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Web.Analysis;

namespace Tonregal.Web.Api;

public sealed record AnalysisSettings(bool RunAtNight, string NightStart, string NightEnd, bool BusyCheck);

public sealed record AnalysisResponse(AnalysisStatus Status, AnalysisSettings Settings);

public static class AnalysisApi
{
    public static RouteGroupBuilder MapAnalysisApi(this RouteGroupBuilder api)
    {
        var analysis = api.MapGroup("/analysis");

        analysis.MapGet("/", (AnalysisService service, IOptions<AnalysisOptions> options) =>
            new AnalysisResponse(service.Status, Settings(options.Value)));
        analysis.MapPost("/start", (AnalysisService service) => TypedResults.Accepted("/api/analysis", service.Start()));
        analysis.MapPost("/stop", (AnalysisService service) => service.Stop());

        return api;
    }

    private static AnalysisSettings Settings(AnalysisOptions o) =>
        new(o.RunAtNight, o.NightStart.ToString("HH:mm"), o.NightEnd.ToString("HH:mm"), !string.IsNullOrWhiteSpace(o.BusyCommand));
}
