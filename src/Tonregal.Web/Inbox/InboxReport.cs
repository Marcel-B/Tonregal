// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;

namespace Tonregal.Web.Inbox;

/// <summary>Problemfälle der Inbox als Text zum Kopieren: Tags, Grund, Fehler und die besten Treffer.</summary>
public static class InboxReport
{
    private const int MaxCandidates = 3;
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static bool IsProblem(ImportJob job) => job.State == ImportState.NeedsReview || job.Error is not null;

    public static string Format(IReadOnlyList<ImportJob> jobs, Func<ImportJob, IReadOnlyList<RecordingCandidate>> candidates, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.AppendLine(German, $"Tonregal Inbox – {jobs.Count} Datei(en) mit Problemen, Stand {now.ToLocalTime():dd.MM.yyyy HH:mm}");
        foreach (var job in jobs)
        {
            sb.AppendLine();
            sb.AppendLine(job.SourcePath);
            var status = new List<string> { job.State == ImportState.NeedsReview ? "Prüfen" : job.State.ToString() };
            if (job.MatchScore is { } score)
                status.Add($"Score {score.ToString("P0", German)}");
            status.Add(job.Format.ToString().ToUpperInvariant());
            if (job.DurationMs > 0)
                status.Add(Duration(job.DurationMs));
            sb.AppendLine($"  Status: {string.Join(" · ", status)}");

            var tags = string.Join(" · ", new[] { Join(job.TagArtist, job.TagTitle), job.TagAlbum }.Where(s => !string.IsNullOrEmpty(s)));
            sb.AppendLine($"  Tags: {(tags.Length > 0 ? tags : "keine")}");
            if (job.ReviewReason is { } reason)
                sb.AppendLine($"  Grund: {reason}");
            if (job.Error is { } error)
                sb.AppendLine($"  Fehler: {error.ReplaceLineEndings(" ")}");

            var all = candidates(job);
            if (all.Count == 0)
                continue;
            var top = all
                .OrderByDescending(c => c.RecordingId == job.SelectedRecordingId && c.ReleaseId == job.SelectedReleaseId)
                .ThenByDescending(c => c.Score)
                .Take(MaxCandidates)
                .ToList();
            sb.AppendLine($"  Treffer ({top.Count} von {all.Count}):");
            foreach (var c in top)
            {
                var selected = c.RecordingId == job.SelectedRecordingId && c.ReleaseId == job.SelectedReleaseId ? "  ← gewählt" : "";
                var release = string.Join(" ", new[] { c.ReleaseTitle, c.ReleaseDate ?? c.Year?.ToString(German), c.Country, c.MediumFormat }
                    .Where(s => !string.IsNullOrEmpty(s)));
                var length = c.DurationMs is { } ms ? $" · {Duration(ms)}" : "";
                sb.AppendLine($"    - {Join(string.Join(", ", c.Artists), c.Title)} · {release} · {c.Score.ToString("P0", German)}{length}{selected}");
            }
        }

        return sb.ToString();
    }

    private static string Join(string? artist, string? title) =>
        string.Join(" – ", new[] { artist, title }.Where(s => !string.IsNullOrEmpty(s)));

    private static string Duration(int ms) => $"{ms / 60000}:{ms / 1000 % 60:00}";
}
