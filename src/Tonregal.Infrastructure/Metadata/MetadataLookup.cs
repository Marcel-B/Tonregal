// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.ValueObjects;

namespace Tonregal.Infrastructure.Metadata;

/// <summary>
/// Fingerprint → AcoustID → MusicBrainz-Recordings → ein Kandidat je Aufnahme und Release.
/// Bei gleichem Score stehen offizielle Studioalben vor Singles, Compilations und Live-Releases,
/// ältere Releases vor neueren.
/// </summary>
public sealed class MetadataLookup(
    AcoustIdClient acoustId,
    MusicBrainzClient musicBrainz,
    IOptions<MetadataOptions> options,
    ILogger<MetadataLookup> logger) : IMetadataLookup
{
    private const int MaxMatches = 3;
    private const int MaxRecordingsPerMatch = 5;
    private const int MaxReleasesPerRecording = 8;

    public async Task<IReadOnlyList<RecordingCandidate>> LookupAsync(Fingerprint fingerprint, CancellationToken ct)
    {
        var matches = (await acoustId.LookupAsync(fingerprint, ct))
            .Where(m => m.Score >= options.Value.MinimumScore)
            .Take(MaxMatches)
            .ToList();

        var candidates = new List<(RecordingCandidate Candidate, int Rank, string Date)>();
        var seenRecordings = new HashSet<Guid>();
        foreach (var match in matches)
        {
            foreach (var recordingId in match.RecordingIds.Take(MaxRecordingsPerMatch))
            {
                if (!seenRecordings.Add(recordingId))
                    continue;
                var recording = await musicBrainz.GetRecordingAsync(recordingId, ct);
                if (recording is null)
                {
                    logger.LogDebug("MusicBrainz kennt Recording {Id} nicht (mehr)", recordingId);
                    continue;
                }

                candidates.AddRange(ToCandidates(match, recording));
            }
        }

        return candidates
            .OrderByDescending(c => c.Candidate.Score)
            .ThenBy(c => c.Rank)
            .ThenBy(c => c.Date, StringComparer.Ordinal)
            .Select(c => c.Candidate)
            .ToList();
    }

    private static IEnumerable<(RecordingCandidate, int, string)> ToCandidates(AcoustIdMatch match, MbRecording recording)
    {
        var artists = (recording.ArtistCredit ?? []).Select(c => c.Artist.Name).ToList();
        var releases = (recording.Releases ?? [])
            .Select(r => (Release: r, Rank: Rank(r)))
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Release.Date ?? "9999", StringComparer.Ordinal)
            .Take(MaxReleasesPerRecording)
            .ToList();

        if (releases.Count == 0)
        {
            yield return (new RecordingCandidate(
                recording.Id, match.AcoustId, recording.Title, artists, recording.Length,
                null, null, null, null, null, null, null, null, null, null, match.Score), int.MaxValue, "9999");
            yield break;
        }

        foreach (var (release, rank) in releases)
        {
            // Bei einer Recording-Abfrage enthält jedes Medium nur den Track dieser Aufnahme.
            var medium = release.Media?.FirstOrDefault(m => m.Tracks is { Count: > 0 });
            var track = medium?.Tracks?[0];
            yield return (new RecordingCandidate(
                recording.Id, match.AcoustId, recording.Title, artists, recording.Length,
                release.Id, release.ReleaseGroup?.Id, release.Title, release.ArtistCredit.Format(),
                release.ReleaseGroup?.PrimaryType, release.Status, release.Year,
                medium?.Position, track?.Position, medium?.TrackCount, match.Score), rank, release.Date ?? "9999");
        }
    }

    /// <summary>Kleiner ist besser: offiziell vor inoffiziell, Album vor EP vor Single, ohne Zusatztyp vor Compilation/Live.</summary>
    internal static int Rank(MbRelease release)
    {
        var rank = release.Status == "Official" ? 0 : 100;
        rank += release.ReleaseGroup?.PrimaryType switch
        {
            "Album" => 0,
            "EP" => 10,
            "Single" => 20,
            _ => 30,
        };
        if (release.ReleaseGroup?.SecondaryTypes is { Count: > 0 })
            rank += 40;
        return rank;
    }
}
