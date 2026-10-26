// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Application;
using Tonregal.Infrastructure.Analysis;

namespace Tonregal.Tests.Analysis;

public sealed class AnalysisToolsTests
{
    [Fact]
    public void Liest_die_Ausgabe_des_Analyse_Scripts()
    {
        var result = LibrosaAnalyzer.Parse("""{"bpm": 117.5, "key": "Am", "keyConfidence": 0.842, "version": "0.11.0"}""");

        Assert.Equal(new(117.5, "Am", 0.842, "0.11.0"), result);
    }

    [Theory]
    [InlineData("01:00", "06:00", "00:59", false)]
    [InlineData("01:00", "06:00", "01:00", true)]
    [InlineData("01:00", "06:00", "06:00", false)]
    [InlineData("23:00", "05:00", "23:30", true)]
    [InlineData("23:00", "05:00", "04:59", true)]
    [InlineData("23:00", "05:00", "12:00", false)]
    public void Nachtfenster_auch_ueber_Mitternacht(string start, string end, string now, bool inside)
    {
        var options = new AnalysisOptions { NightStart = TimeOnly.Parse(start), NightEnd = TimeOnly.Parse(end) };

        Assert.Equal(inside, options.InNightWindow(TimeOnly.Parse(now)));
    }
}
