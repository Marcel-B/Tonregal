// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;
using LibraryPathRule = Tonregal.Domain.ValueObjects.LibraryPath;

namespace Tonregal.Infrastructure.Import;

/// <summary>
/// Übernimmt einen erkannten Job in die Library: Tags und Cover in eine Kopie schreiben, die Kopie
/// prüfen, atomar an ihren Platz nach der Ordnerregel umbenennen und erst dann das Original aus der
/// Inbox löschen. Geht etwas schief, bleibt die Inbox-Datei unverändert.
/// </summary>
public sealed class LibraryImporter(
    TonregalDbContext db,
    ITagReader tagReader,
    ITagWriter tagWriter,
    IMetadataLookup lookup,
    ICoverArtSource covers,
    IOptions<TonregalOptions> options,
    TimeProvider time,
    ILogger<LibraryImporter> logger) : ILibraryImporter
{
    private const string TempPrefix = ".tonregal-";

    public async Task<ImportJob> ImportAsync(int jobId, CancellationToken ct)
    {
        var job = await db.ImportJobs.FindAsync([jobId], ct)
            ?? throw new KeyNotFoundException($"Import-Job {jobId} gibt es nicht");
        try
        {
            await ImportAsync(job, ct);
            job.Error = null;
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException or ExternalToolUnavailableException)
        {
            logger.LogWarning("Übernahme von {Path} fehlgeschlagen: {Message}", job.SourcePath, e.Message);
            job.Error = e.Message;
        }

        await db.SaveChangesAsync(ct);
        return job;
    }

    private async Task ImportAsync(ImportJob job, CancellationToken ct)
    {
        if (job.State != ImportState.Matched || job.SelectedRecordingId is not { } recordingId || job.SelectedReleaseId is not { } releaseId)
            throw new InvalidOperationException("Nur erkannte Dateien mit gewähltem Release lassen sich übernehmen");

        var o = options.Value;
        if (!File.Exists(o.MountMarker))
            throw new IOException($"Mount-Marker {o.MountMarker} fehlt – ist die SSD gemountet?");
        var inboxRoot = Path.GetFullPath(o.Inbox);
        var libraryRoot = Path.GetFullPath(o.Library);
        var source = Path.Combine(inboxRoot, job.SourcePath);
        if (!File.Exists(source))
            throw new IOException(ImportJob.VanishedReason);

        var track = await lookup.GetReleaseTrackAsync(releaseId, recordingId, ct)
            ?? throw new InvalidOperationException("Die Aufnahme steht bei MusicBrainz nicht (mehr) auf diesem Release");
        track = track with { AcoustId = SelectedCandidate(job)?.AcoustId };

        var relative = LibraryPathRule.For(
            track.AlbumArtist, track.Year, track.Album, track.Kind,
            track.Disc, track.DiscCount, track.Track, track.Title, Path.GetExtension(source));
        var target = Path.Combine(libraryRoot, relative);
        var albumFolder = Path.GetDirectoryName(target)!;
        var libraryRelative = relative.Replace(Path.DirectorySeparatorChar, '/');
        if (File.Exists(target))
            throw new IOException($"In der Library liegt schon {libraryRelative}");

        Directory.CreateDirectory(albumFolder);
        var cover = await CoverAsync(source, albumFolder, releaseId, ct);

        var temp = Path.Combine(albumFolder, $"{TempPrefix}{Guid.NewGuid():N}{Path.GetExtension(source)}");
        try
        {
            File.Copy(source, temp);
            tagWriter.Write(temp, track, cover);
            Verify(temp, track);
            File.Move(temp, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        File.Delete(source);
        RemoveEmptyFolders(Path.GetDirectoryName(source)!, inboxRoot);

        job.State = ImportState.Imported;
        job.LibraryPath = libraryRelative;
        job.FinishedAt = time.GetUtcNow();
        logger.LogInformation("Übernommen: {Source} → {Target}", job.SourcePath, libraryRelative);
    }

    /// <summary>
    /// cover.jpg im Albumordner hat Vorrang, dann ein eingebettetes Cover der Datei, dann das Cover
    /// Art Archive. Fehlt cover.jpg, wird es angelegt (Navidrome-Konvention).
    /// </summary>
    private async Task<EmbeddedPicture?> CoverAsync(string source, string albumFolder, Guid releaseId, CancellationToken ct)
    {
        var folderCover = Path.Combine(albumFolder, "cover.jpg");
        if (File.Exists(folderCover))
            return new EmbeddedPicture(await File.ReadAllBytesAsync(folderCover, ct), "image/jpeg");

        EmbeddedPicture? cover = null;
        try
        {
            cover = tagReader.ReadCover(source) ?? await covers.GetFrontCoverAsync(releaseId, ct);
        }
        catch (Exception e) when (e is ExternalToolUnavailableException or HttpRequestException or InvalidDataException)
        {
            // Ohne Cover übernehmen ist besser als gar nicht.
            logger.LogWarning("Kein Cover für Release {Release}: {Message}", releaseId, e.Message);
        }

        if (cover is { MimeType: "image/jpeg" })
            await File.WriteAllBytesAsync(folderCover, cover.Data, ct);
        return cover;
    }

    /// <summary>Liest die geschriebene Kopie zurück, bevor sie das Original ersetzt.</summary>
    private void Verify(string path, ReleaseTrack track)
    {
        var written = tagReader.Read(path).Tags;
        if (written.Title != track.Title || written.MusicBrainzRecordingId != track.RecordingId)
            throw new InvalidDataException($"Tags ließen sich nicht korrekt schreiben (gelesen: „{written.Title}“)");
    }

    /// <summary>Entfernt leere Inbox-Ordner (macOS-Reste wie .DS_Store und ._* zählen als leer).</summary>
    private static void RemoveEmptyFolders(string folder, string inboxRoot)
    {
        while (folder.Length > inboxRoot.Length && folder.StartsWith(inboxRoot, StringComparison.Ordinal))
        {
            var entries = Directory.EnumerateFileSystemEntries(folder).ToList();
            if (entries.Any(e => !Path.GetFileName(e).StartsWith('.')))
                return;
            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                    return;
                File.Delete(entry);
            }

            Directory.Delete(folder);
            folder = Path.GetDirectoryName(folder)!;
        }
    }

    private static RecordingCandidate? SelectedCandidate(ImportJob job) =>
        job.Candidates is null
            ? null
            : JsonSerializer.Deserialize<List<RecordingCandidate>>(job.Candidates, InboxImporter.Json)?
                .FirstOrDefault(c => c.RecordingId == job.SelectedRecordingId && c.ReleaseId == job.SelectedReleaseId);
}
