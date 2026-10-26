// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Infrastructure.Analysis;

/// <summary>
/// Analysiert die Library albumweise. Je Datei Tempo und Tonart (librosa); fehlen BPM oder Tonart in den
/// Tags, werden sie in eine Kopie geschrieben, geprüft und atomar zurückbenannt – vorhandene Werte
/// (z. B. aus DJ-Software) bleiben. Danach schreibt rsgain Track- und Album-Gain für alle Dateien des
/// Albums. Jedes Rohergebnis landet als <see cref="AnalysisResult"/> in der DB, auch ein leeres, wenn
/// eine Datei nicht lesbar war; so bleibt der Lauf nicht an ihr hängen.
/// </summary>
public sealed class LibraryAnalyzer(
    TonregalDbContext db,
    IBpmKeyAnalyzer bpmKey,
    IReplayGainTagger replayGain,
    ITagReader tagReader,
    ITagWriter tagWriter,
    IOptions<TonregalOptions> options,
    TimeProvider time,
    ILogger<LibraryAnalyzer> logger) : ILibraryAnalyzer
{
    private const string BpmKeyTool = IBpmKeyAnalyzer.ToolName;
    private const string GainTool = IReplayGainTagger.ToolName;

    /// <summary>rsgain normalisiert auf −18 LUFS; daraus ergibt sich die gemessene Lautheit.</summary>
    private const double ReferenceLoudness = -18;

    private IQueryable<AudioFile> Pending => db.AudioFiles.Where(f =>
        !f.AnalysisResults.Any(r => r.Tool == BpmKeyTool) || !f.AnalysisResults.Any(r => r.Tool == GainTool));

    public async Task<AnalysisCounts> CountAsync(CancellationToken ct)
    {
        var files = await db.AudioFiles.CountAsync(ct);
        var pending = await Pending.CountAsync(ct);
        var withoutResult = await db.AnalysisResults
            .Where(r => r.Tool == BpmKeyTool && r.Bpm == null && r.Key == null)
            .Select(r => r.AudioFileId).Distinct().CountAsync(ct);
        return new AnalysisCounts(files, files - pending, pending, withoutResult);
    }

    public async Task<AlbumAnalysis?> AnalyzeNextAlbumAsync(CancellationToken ct)
    {
        if (!File.Exists(options.Value.MountMarker))
            throw new LibraryUnavailableException($"Mount-Marker {options.Value.MountMarker} fehlt – ist die SSD gemountet?");
        if (await Pending.OrderBy(f => f.Track.AlbumId).Select(f => (int?)f.Track.AlbumId).FirstOrDefaultAsync(ct) is not { } albumId)
            return null;

        var files = await db.AudioFiles
            .Include(f => f.Track).ThenInclude(t => t.Album).ThenInclude(a => a.AlbumArtist)
            .Include(f => f.AnalysisResults)
            .Where(f => f.Track.AlbumId == albumId)
            .OrderBy(f => f.Path)
            .ToListAsync(ct);
        var album = files[0].Track.Album;
        var name = $"{album.AlbumArtist.Name} – {album.Title}";
        var root = Path.GetFullPath(options.Value.Library);
        var errors = new List<string>();
        var present = files.Where(f => File.Exists(FullPath(root, f))).ToList();
        foreach (var missing in files.Except(present))
        {
            errors.Add($"{missing.Path}: Datei fehlt");
            foreach (var tool in new[] { BpmKeyTool, GainTool }.Where(t => !Has(missing, t)))
                Record(missing, tool, "–");
        }

        foreach (var file in present.Where(f => !Has(f, BpmKeyTool)))
        {
            try
            {
                var result = await bpmKey.AnalyzeAsync(FullPath(root, file), ct);
                WriteMissingTags(FullPath(root, file), result);
                Record(file, BpmKeyTool, result.Version, r => (r.Bpm, r.Key, r.KeyConfidence) = (result.Bpm, result.Key, result.KeyConfidence));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Analyse von {Path} fehlgeschlagen: {Message}", file.Path, e.Message);
                errors.Add($"{file.Path}: {e.Message}");
                Record(file, BpmKeyTool, "–");
            }
        }

        if (present.Count > 0 && present.Any(f => !Has(f, GainTool)))
            await TagReplayGainAsync(root, present, errors, ct);

        foreach (var file in present)
            await RefreshAsync(root, file, ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Analyse {Album}: {Files} Datei(en), {Errors} Fehler", name, files.Count, errors.Count);
        return new AlbumAnalysis(name, files.Count, errors);
    }

    /// <summary>Album-Gain braucht alle Dateien des Albums, auch die schon analysierten.</summary>
    private async Task TagReplayGainAsync(string root, List<AudioFile> files, List<string> errors, CancellationToken ct)
    {
        string version;
        try
        {
            version = await replayGain.TagAlbumAsync([.. files.Select(f => FullPath(root, f))], ct);
        }
        catch (InvalidDataException e)
        {
            logger.LogWarning("rsgain für {Files} Datei(en) fehlgeschlagen: {Message}", files.Count, e.Message);
            errors.Add(e.Message);
            foreach (var file in files.Where(f => !Has(f, GainTool)))
                Record(file, GainTool, "–");
            return;
        }

        foreach (var file in files)
        {
            var gain = tagReader.Read(FullPath(root, file)).Tags.ReplayGainTrackGain;
            file.AnalysisResults.RemoveAll(r => r.Tool == GainTool);
            Record(file, GainTool, version, r => r.Loudness = gain is { } g ? Math.Round(ReferenceLoudness - g, 2) : null);
        }
    }

    private void WriteMissingTags(string path, BpmKeyResult result)
    {
        var tags = tagReader.Read(path).Tags;
        var bpm = tags.Bpm is null ? result.Bpm : null;
        var key = tags.InitialKey is null ? result.Key : null;
        if (bpm is null && key is null)
            return;

        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".tonregal-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            File.Copy(path, temp);
            tagWriter.WriteAnalysis(temp, bpm, key);
            var written = tagReader.Read(temp).Tags;
            if ((bpm is not null && written.Bpm is null) || (key is not null && written.InitialKey != key))
                throw new InvalidDataException($"BPM/Tonart ließen sich nicht korrekt schreiben: {Path.GetFileName(path)}");
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>Werte aus den geschriebenen Tags übernehmen, damit der nächste Library-Scan die Datei als unverändert sieht.</summary>
    private async Task RefreshAsync(string root, AudioFile file, CancellationToken ct)
    {
        var path = FullPath(root, file);
        var info = new FileInfo(path);
        var modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (file.SizeBytes == info.Length && file.ModifiedAt == modifiedAt)
            return;

        var tags = tagReader.Read(path).Tags;
        file.Track.Bpm = tags.Bpm ?? file.Track.Bpm;
        file.Track.InitialKey = tags.InitialKey ?? file.Track.InitialKey;
        file.Track.ReplayGainTrack = tags.ReplayGainTrackGain ?? file.Track.ReplayGainTrack;
        file.SizeBytes = info.Length;
        file.ModifiedAt = modifiedAt;
        file.FileHash = await LibraryScanner.HashAsync(path, ct);
    }

    private void Record(AudioFile file, string tool, string version, Action<AnalysisResult>? fill = null)
    {
        var result = new AnalysisResult { Tool = tool, Version = version, RanAt = time.GetUtcNow() };
        fill?.Invoke(result);
        file.AnalysisResults.Add(result);
    }

    private static bool Has(AudioFile file, string tool) => file.AnalysisResults.Any(r => r.Tool == tool);

    private static string FullPath(string root, AudioFile file) => Path.Combine(root, file.Path);
}
