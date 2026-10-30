// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Infrastructure.Lyrics;

/// <summary>
/// Reihenfolge: eine <c>.lrc</c> neben der Audiodatei gilt immer (von Hand gepflegt oder früher geholt), ein
/// eingebetteter synchronisierter Text ebenso. Sonst wird bei lrclib.net gesucht; ein synchronisierter Treffer schlägt
/// einen eingebetteten reinen Text. Was von lrclib.net kommt, landet auch als <c>.lrc</c> neben der Datei.
/// </summary>
public sealed class LyricsService(
    TonregalDbContext db,
    ITagReader tags,
    LrcLibClient lrclib,
    IOptions<TonregalOptions> options,
    IOptions<LyricsOptions> lyricsOptions,
    TimeProvider time,
    ILogger<LyricsService> logger) : ILyricsService
{
    public const string SidecarExtension = ".lrc";

    public async Task<SongLyrics?> GetAsync(int trackId, bool refresh, CancellationToken ct)
    {
        var track = await db.Tracks
            .Include(t => t.Album).ThenInclude(a => a.AlbumArtist)
            .Include(t => t.Artists)
            .Include(t => t.Files)
            .AsSplitQuery()
            .SingleOrDefaultAsync(t => t.Id == trackId, ct);
        if (track is null)
            return null;

        var stored = await db.Lyrics.FindAsync([trackId], ct);
        var now = time.GetUtcNow();
        var files = AudioFiles(track);

        // Eine nachträglich abgelegte .lrc soll nicht erst nach RetryDays auftauchen.
        if (!refresh && stored is not null && !IsStale(stored, now)
            && !(stored.Source == LyricsSource.None && files.Any(f => File.Exists(SidecarOf(f)))))
            return ToLyrics(stored);

        var local = ReadLocal(files);
        if (!refresh && local is { Source: LyricsSource.Sidecar } or { Synced: true })
            return await SaveAsync(trackId, stored, local, now, ct);
        if (!lyricsOptions.Value.Online)
            return await SaveAsync(trackId, stored, local ?? Nothing, now, ct);

        Found? online;
        try
        {
            online = await LookupAsync(track, ct);
        }
        catch (Exception e) when (e is ExternalToolUnavailableException or HttpRequestException
                                      || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Songtext für Track {TrackId} nicht abrufbar: {Message}", trackId, e.Message);
            // Nichts speichern, damit der nächste Aufruf es wieder versucht.
            if (local is not null)
                return ToLyrics(local);
            if (stored is { Text: not null } or { Instrumental: true })
                return ToLyrics(stored);
            throw e as ExternalToolUnavailableException ?? new ExternalToolUnavailableException($"lrclib.net nicht erreichbar: {e.Message}", e);
        }

        var chosen = refresh
            ? online ?? local ?? Nothing
            : online is { Synced: true } or { Instrumental: true } ? online : local ?? online ?? Nothing;
        if (chosen.Source == LyricsSource.Lrclib && chosen.Text is not null)
            WriteSidecar(files, chosen.Text);
        return await SaveAsync(trackId, stored, chosen, now, ct);
    }

    private sealed record Found(LyricsSource Source, string? Text, bool Synced, bool Instrumental);

    private static readonly Found Nothing = new(LyricsSource.None, null, false, false);

    /// <summary>Reine oder fehlende Texte werden nach <see cref="LyricsOptions.RetryDays"/> erneut gesucht.</summary>
    private bool IsStale(TrackLyrics stored, DateTimeOffset now) =>
        !stored.Synced && !stored.Instrumental && stored.Source != LyricsSource.Sidecar
        && stored.CheckedAt < now.AddDays(-lyricsOptions.Value.RetryDays);

    /// <summary>Vorhandene Audiodateien, FLAC zuerst (wie im Player).</summary>
    private List<string> AudioFiles(Track track)
    {
        if (!File.Exists(options.Value.MountMarker))
            return [];
        var root = Path.GetFullPath(options.Value.Library);
        return track.Files
            .OrderByDescending(f => f.Format == AudioFormat.Flac).ThenByDescending(f => f.Bitrate)
            .Select(f => Path.Combine(root, f.Path))
            .Where(File.Exists)
            .ToList();
    }

    public static string SidecarOf(string audioFile) => Path.ChangeExtension(audioFile, SidecarExtension);

    private Found? ReadLocal(IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            var sidecar = SidecarOf(file);
            if (!File.Exists(sidecar))
                continue;
            try
            {
                var text = File.ReadAllText(sidecar);
                var (synced, lines) = LrcParser.Parse(text);
                if (lines.Count > 0)
                    return new Found(LyricsSource.Sidecar, text, synced, false);
            }
            catch (IOException e)
            {
                logger.LogWarning("{Sidecar} nicht lesbar: {Message}", sidecar, e.Message);
            }
        }

        foreach (var file in files)
        {
            try
            {
                if (tags.ReadLyrics(file) is { } text && LrcParser.Parse(text) is { Lines.Count: > 0 } parsed)
                    return new Found(LyricsSource.Tag, text, parsed.Synced, false);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                logger.LogWarning("Tags von {File} nicht lesbar: {Message}", file, e.Message);
            }
        }

        return null;
    }

    private async Task<Found?> LookupAsync(Track track, CancellationToken ct)
    {
        var artist = track.Artists.FirstOrDefault()?.Name ?? track.Album.AlbumArtist.Name;
        var hit = await lrclib.FindAsync(artist, track.Title, track.Album.Title, track.DurationMs / 1000.0, ct);
        if (hit is null)
            return null;
        if (hit.Instrumental)
            return new Found(LyricsSource.Lrclib, null, false, true);
        if (!string.IsNullOrWhiteSpace(hit.SyncedLyrics) && LrcParser.Parse(hit.SyncedLyrics).Synced)
            return new Found(LyricsSource.Lrclib, hit.SyncedLyrics, true, false);
        return string.IsNullOrWhiteSpace(hit.PlainLyrics) ? null : new Found(LyricsSource.Lrclib, hit.PlainLyrics, false, false);
    }

    private void WriteSidecar(IReadOnlyList<string> files, string text)
    {
        if (!lyricsOptions.Value.WriteSidecar || files.Count == 0)
            return;
        var sidecar = SidecarOf(files[0]);
        try
        {
            File.WriteAllText(sidecar, text.ReplaceLineEndings("\n").TrimEnd() + "\n", new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Sidecar} nicht schreibbar: {Message}", sidecar, e.Message);
        }
    }

    private async Task<SongLyrics> SaveAsync(
        int trackId, TrackLyrics? stored, Found found, DateTimeOffset now, CancellationToken ct)
    {
        stored ??= db.Lyrics.Add(new TrackLyrics { TrackId = trackId }).Entity;
        stored.Source = found.Source;
        stored.Text = found.Text;
        stored.Synced = found.Synced;
        stored.Instrumental = found.Instrumental;
        stored.CheckedAt = now;
        await db.SaveChangesAsync(ct);
        return ToLyrics(stored);
    }

    private static SongLyrics ToLyrics(Found found) => Build(found.Source, found.Text, found.Instrumental);

    private static SongLyrics ToLyrics(TrackLyrics stored) => Build(stored.Source, stored.Text, stored.Instrumental);

    private static SongLyrics Build(LyricsSource source, string? text, bool instrumental)
    {
        var (synced, lines) = LrcParser.Parse(text);
        return new SongLyrics(source, synced, instrumental, lines);
    }
}
