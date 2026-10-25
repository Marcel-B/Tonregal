// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Web.Inbox;

namespace Tonregal.Tests.Web;

public class InboxReportTests
{
    [Fact]
    public void Zeigt_Grund_und_die_besten_Treffer_mit_dem_gewaehlten_zuerst()
    {
        var chosen = Candidate("Taking Over Me", 0.95);
        var job = new ImportJob
        {
            SourcePath = "Evanescence/Fallen/08.mp3", State = ImportState.NeedsReview, Format = AudioFormat.Mp3, DurationMs = 230_000,
            TagArtist = "Evanescence", TagTitle = "Talking over me", TagAlbum = "Fallen", MatchScore = 0.99,
            ReviewReason = "Tags passen zu keinem Treffer: „Talking over me“",
            SelectedRecordingId = chosen.RecordingId, SelectedReleaseId = chosen.ReleaseId,
        };
        IReadOnlyList<RecordingCandidate> candidates = [Candidate("A", 0.99), Candidate("B", 0.98), chosen, Candidate("C", 0.5)];

        var text = InboxReport.Format([job], _ => candidates, new DateTimeOffset(2026, 10, 25, 22, 0, 0, TimeSpan.Zero));

        var lines = text.Split(Environment.NewLine);
        Assert.Equal("Evanescence/Fallen/08.mp3", lines[2]);
        Assert.Equal("  Status: Prüfen · Score 99 % · MP3 · 3:50", lines[3]);
        Assert.Equal("  Tags: Evanescence – Talking over me · Fallen", lines[4]);
        Assert.Equal("  Grund: Tags passen zu keinem Treffer: „Talking over me“", lines[5]);
        Assert.Equal("  Treffer (3 von 4):", lines[6]);
        Assert.Equal("    - Evanescence – Taking Over Me · Fallen 2003-03-04 MX CD · 95 % · 3:50  ← gewählt", lines[7]);
        Assert.StartsWith("    - Evanescence – A ·", lines[8]);
    }

    private static RecordingCandidate Candidate(string title, double score) =>
        new(Guid.NewGuid(), null, title, ["Evanescence"], 230_000, Guid.NewGuid(), null, "Fallen", "Evanescence", "Album", "Official",
            2003, 1, 8, 12, "2003-03-04", "MX", "CD", score);
}
