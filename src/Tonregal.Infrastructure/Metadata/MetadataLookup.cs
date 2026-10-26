// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
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

                candidates.AddRange(ToCandidates(match.AcoustId, match.Score, recording));
            }
        }

        // Mehrere Recordings (z. B. Duplikate in MusicBrainz) hängen oft am selben Release;
        // je Release reicht der beste Treffer.
        var seenReleases = new HashSet<Guid>();
        return candidates
            .OrderByDescending(c => c.Candidate.Score)
            .ThenBy(c => c.Rank)
            .ThenBy(c => c.Date, StringComparer.Ordinal)
            .Select(c => c.Candidate)
            .Where(c => c.ReleaseId is not { } releaseId || seenReleases.Add(releaseId))
            .ToList();
    }

    /// <summary>Unter diesem Such-Score (0–100) ist ein MusicBrainz-Suchtreffer meist ein anderer Song.</summary>
    private const int MinimumSearchScore = 80;
    private const int MaxSearchCandidates = 30;

    public async Task<IReadOnlyList<RecordingCandidate>> SearchByTagsAsync(
        string artist, string title, string? album, int durationMs, CancellationToken ct)
    {
        // Mit Album zuerst über das Release: dessen Trackliste ist vollständig, während die Recording-Suche
        // bei vielgespielten Songs Live-Mitschnitte, Demos und Compilations nach vorn stellt.
        var onRelease = string.IsNullOrWhiteSpace(album) ? [] : await ReleaseCandidatesAsync(artist, title, album, ct);

        IReadOnlyList<MbSearchRecording> onAlbum = string.IsNullOrWhiteSpace(album)
            ? []
            : await musicBrainz.SearchRecordingsAsync(artist, title, album, ct);
        var seenRecordings = new HashSet<Guid>();
        var recordings = onAlbum.Concat(await musicBrainz.SearchRecordingsAsync(artist, title, null, ct))
            .Where(r => seenRecordings.Add(r.Id))
            .ToList();
        var seenReleases = new HashSet<(Guid, Guid)>();
        var found = recordings
            .Where(r => r.Score >= MinimumSearchScore)
            .SelectMany(r => ToCandidates(null, r.Score / 100.0, r.ToRecording(), album))
            .OrderByDescending(c => album is not null && c.Candidate.ReleaseTitle is { } t && Import.InboxImporter.SameTitle(t, album))
            .ThenBy(c => durationMs <= 0 || c.Candidate.DurationMs is not { } length ? 1 : Math.Abs(length - durationMs) <= 15_000 ? 0 : 2)
            .ThenByDescending(c => c.Candidate.Score)
            .ThenBy(c => c.Rank)
            .ThenBy(c => c.Date, StringComparer.Ordinal)
            .Select(c => c.Candidate);
        return onRelease.Concat(found)
            .Where(c => c.ReleaseId is not { } releaseId || seenReleases.Add((releaseId, c.RecordingId)))
            .Take(MaxSearchCandidates)
            .ToList();
    }

    private const int MinimumReleaseSearchScore = 90;
    private const int MaxSearchedReleases = 4;

    /// <summary>
    /// Die passenden Tracks der wahrscheinlichsten Releases zum Album aus den Tags: gleicher Titel vor
    /// „Fallen (20th Anniversary)“, offizielle Studioalben vor dem Rest, die älteste Ausgabe zuerst.
    /// Für alle Tracks eines Albums kommen so dieselben Releases (aus dem Cache) in derselben Reihenfolge.
    /// </summary>
    private async Task<List<RecordingCandidate>> ReleaseCandidatesAsync(string artist, string title, string album, CancellationToken ct)
    {
        var hits = (await musicBrainz.SearchReleasesAsync(artist, album, ct))
            .Where(h => h.Score >= MinimumReleaseSearchScore && Import.InboxImporter.SameTitle(h.Title, album))
            .OrderByDescending(h => Import.InboxImporter.SameText(h.Title, album))
            .ThenBy(h => Rank(new MbRelease(h.Id, h.Title, h.Status, h.Date, null, null, h.ReleaseGroup, null)))
            .ThenBy(h => h.Date is null)
            .ThenBy(h => h.Date, StringComparer.Ordinal)
            .Take(MaxSearchedReleases)
            .ToList();

        var candidates = new List<RecordingCandidate>();
        foreach (var hit in hits)
        {
            var release = await musicBrainz.GetReleaseAsync(hit.Id, ct);
            foreach (var medium in release?.Media ?? [])
            {
                var tracks = (medium.Tracks ?? [])
                    .Where(t => t.Recording is not null && Import.InboxImporter.SameTitle(t.Title, title))
                    .OrderByDescending(t => Import.InboxImporter.SameText(t.Title, title));
                foreach (var track in tracks)
                {
                    var recording = track.Recording!;
                    var credit = recording.ArtistCredit is { Count: > 0 } rc ? rc : release!.ArtistCredit ?? [];
                    candidates.Add(new RecordingCandidate(
                        recording.Id, null, track.Title, credit.Select(c => c.Artist.Name).ToList(), track.Length ?? recording.Length,
                        release!.Id, release.ReleaseGroup?.Id, release.Title, release.ArtistCredit.Format(),
                        release.ReleaseGroup?.PrimaryType, release.Status, release.Year,
                        medium.Position, track.Position, medium.TrackCount,
                        release.Date, release.Country, medium.Format, 1.0));
                }
            }
        }

        // Exakter Titel vor „Bring Me to Life (demo)“, über alle Releases hinweg.
        return candidates.OrderByDescending(c => Import.InboxImporter.SameText(c.Title, title)).ToList();
    }

    private static IEnumerable<(RecordingCandidate Candidate, int Rank, string Date)> ToCandidates(
        Guid? acoustId, double score, MbRecording recording, string? album = null)
    {
        var artists = (recording.ArtistCredit ?? []).Select(c => c.Artist.Name).ToList();
        var releases = (recording.Releases ?? [])
            .Select(r => (Release: r, Rank: Rank(r)))
            .OrderByDescending(r => album is not null && Import.InboxImporter.SameTitle(r.Release.Title, album))
            .ThenBy(r => r.Rank)
            .ThenBy(r => r.Release.Date ?? "9999", StringComparer.Ordinal)
            .Take(MaxReleasesPerRecording)
            .ToList();

        if (releases.Count == 0)
        {
            yield return (new RecordingCandidate(
                recording.Id, acoustId, recording.Title, artists, recording.Length,
                null, null, null, null, null, null, null, null, null, null, null, null, null, score), int.MaxValue, "9999");
            yield break;
        }

        foreach (var (release, rank) in releases)
        {
            // Bei einer Recording-Abfrage enthält jedes Medium nur den Track dieser Aufnahme.
            var medium = release.Media?.FirstOrDefault(m => m.Tracks is { Count: > 0 });
            var track = medium?.Tracks?[0];
            yield return (new RecordingCandidate(
                recording.Id, acoustId, recording.Title, artists, recording.Length,
                release.Id, release.ReleaseGroup?.Id, release.Title, release.ArtistCredit.Format(),
                release.ReleaseGroup?.PrimaryType, release.Status, release.Year,
                medium?.Position, track?.Position, medium?.TrackCount,
                release.Date, release.Country, medium?.Format, score), rank, release.Date ?? "9999");
        }
    }

    /// <summary>MusicBrainz-ID von „Various Artists“.</summary>
    internal static readonly Guid VariousArtistsId = Guid.Parse("89ad4ac3-39f7-470e-963a-56509c546377");

    public async Task<Guid?> GetReleaseGroupIdAsync(Guid releaseId, CancellationToken ct) =>
        (await musicBrainz.GetReleaseAsync(releaseId, ct))?.ReleaseGroup?.Id;

    public async Task<ReleaseTrack?> GetReleaseTrackAsync(Guid releaseId, Guid recordingId, CancellationToken ct)
    {
        var release = await musicBrainz.GetReleaseAsync(releaseId, ct);
        if (release is null)
            return null;

        var media = release.Media ?? [];
        foreach (var medium in media)
        {
            var track = medium.Tracks?.FirstOrDefault(t => t.Recording?.Id == recordingId);
            if (track is null)
                continue;

            var trackCredit = track.Recording?.ArtistCredit is { Count: > 0 } rc ? rc : release.ArtistCredit ?? [];
            var albumCredit = release.ArtistCredit ?? [];
            var albumArtistIds = albumCredit.Select(c => c.Artist.Id).ToList();
            var group = release.ReleaseGroup;
            var secondary = group?.SecondaryTypes ?? [];
            var kind = albumArtistIds.Contains(VariousArtistsId) ? AlbumKind.Compilation
                : group?.PrimaryType switch
                {
                    "EP" => AlbumKind.Ep,
                    "Single" => AlbumKind.Single,
                    _ => AlbumKind.Album,
                };

            return new ReleaseTrack
            {
                RecordingId = recordingId,
                TrackId = track.Id,
                Title = track.Title,
                Artists = trackCredit.Select(c => c.Artist.Name).ToList(),
                ArtistIds = trackCredit.Select(c => c.Artist.Id).ToList(),
                ArtistSort = trackCredit.FirstOrDefault()?.Artist.SortName,
                ReleaseId = release.Id,
                ReleaseGroupId = group?.Id,
                Album = release.Title,
                AlbumArtist = albumCredit.Format() ?? trackCredit.Format() ?? "",
                AlbumArtistIds = albumArtistIds,
                AlbumArtistSort = albumCredit.Count == 1 ? albumCredit[0].Artist.SortName : null,
                Kind = kind,
                ReleaseType = group?.PrimaryType is { } primary
                    ? string.Join("; ", secondary.Prepend(primary).Select(t => t.ToLowerInvariant()))
                    : null,
                ReleaseStatus = release.Status?.ToLowerInvariant(),
                Country = release.Country,
                Year = release.Year,
                Disc = medium.Position,
                DiscCount = Math.Max(1, media.Count),
                Track = track.Position,
                TrackCount = medium.TrackCount,
            };
        }

        return null;
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
