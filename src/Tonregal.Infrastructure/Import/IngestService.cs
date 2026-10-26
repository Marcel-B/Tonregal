// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Persistence;
using LibraryPathRule = Tonregal.Domain.ValueObjects.LibraryPath;

namespace Tonregal.Infrastructure.Import;

/// <summary>
/// Nimmt eigene Stücke über die API an. Der Aufrufer kennt die Metadaten schon, deshalb gibt es
/// weder Fingerprint noch Review: der Job startet als Matched und wartet nur auf die Übernahme durch
/// den <see cref="LibraryImporter"/>. Byte-gleiche Dateien, die schon in der Library liegen, werden
/// gleich verworfen.
/// </summary>
public sealed partial class IngestService(
    TonregalDbContext db,
    ITagReader tagReader,
    IOptions<TonregalOptions> options,
    TimeProvider time,
    ILogger<IngestService> logger) : IIngestService
{
    /// <summary>Endung der Cover-Datei neben der Audiodatei; der Inbox-Scan sieht nur Audio.</summary>
    public const string CoverSuffix = ".cover";

    public async Task<IngestResult> IngestAsync(
        string source, IngestMetadata metadata, string fileName, Stream audio, Stream? cover, CancellationToken ct)
    {
        Validate(source, metadata);
        var key = IIngestService.Key(source, metadata.ExternalId.Trim());
        if (await db.ImportJobs.SingleOrDefaultAsync(j => j.ExternalId == key, ct) is { } existing)
            return new IngestResult(existing, false);

        var o = options.Value;
        if (!File.Exists(o.MountMarker))
            throw new LibraryUnavailableException($"Mount-Marker {o.MountMarker} fehlt – ist die SSD gemountet?");
        var name = LibraryPathRule.Sanitize(metadata.ExternalId.Trim()) + Path.GetExtension(fileName).ToLowerInvariant();
        string relative;
        try
        {
            relative = await InboxUpload.SaveAsync(o.Inbox, $"{InboxImporter.ApiFolder}/{source}/{name}", audio, ct, ingest: true);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"Kein unterstütztes Audioformat: {fileName}");
        }

        var path = Path.Combine(Path.GetFullPath(o.Inbox), relative);
        try
        {
            var job = await CreateJobAsync(key, relative, path, metadata, ct);
            if (cover is not null && job.State == ImportState.Matched)
            {
                await using var file = File.Create(path + CoverSuffix);
                await cover.CopyToAsync(file, ct);
            }

            db.ImportJobs.Add(job);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Ingest {Source}: {Title} ({ExternalId}) → Job {Id} {State}", source, metadata.Title, metadata.ExternalId, job.Id, job.State);
            return new IngestResult(job, true);
        }
        catch (DbUpdateException)
        {
            // Derselbe Aufruf lief gleichzeitig zweimal; der andere war schneller.
            Delete(path);
            db.ChangeTracker.Clear();
            return new IngestResult(await db.ImportJobs.SingleOrDefaultAsync(j => j.ExternalId == key, ct)
                ?? throw new InvalidOperationException($"Ingest-Job {key} ließ sich nicht speichern"), false);
        }
        catch
        {
            Delete(path);
            throw;
        }
    }

    public Task<ImportJob?> FindAsync(string source, int jobId, CancellationToken ct) =>
        db.ImportJobs.SingleOrDefaultAsync(j => j.Id == jobId && j.Source == FileSource.Api && j.SourcePath.StartsWith($"{InboxImporter.ApiFolder}/{source}/"), ct);

    private async Task<ImportJob> CreateJobAsync(string key, string relative, string path, IngestMetadata metadata, CancellationToken ct)
    {
        AudioFileInfo info;
        try
        {
            info = tagReader.Read(path);
        }
        catch (InvalidDataException e)
        {
            throw new ArgumentException($"Keine lesbare Audiodatei: {e.Message}");
        }

        var job = new ImportJob
        {
            SourcePath = relative,
            Source = FileSource.Api,
            State = ImportState.Matched,
            ExternalId = key,
            IngestMetadata = JsonSerializer.Serialize(metadata, InboxImporter.Json),
            SizeBytes = new FileInfo(path).Length,
            ModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
            FileHash = await LibraryScanner.HashAsync(path, ct),
            Format = info.Properties.Format,
            DurationMs = (int)info.Properties.Duration.TotalMilliseconds,
            TagTitle = metadata.Title,
            TagArtist = metadata.Artist,
            TagAlbum = metadata.Album,
            StartedAt = time.GetUtcNow(),
        };

        if (await db.AudioFiles.Where(f => f.FileHash == job.FileHash).Select(f => f.Path).FirstOrDefaultAsync(ct) is { } existing)
        {
            job.State = ImportState.Rejected;
            job.Error = ImportJob.AlreadyInLibrary + existing;
            job.FinishedAt = time.GetUtcNow();
            Delete(path);
        }

        return job;
    }

    private static void Validate(string source, IngestMetadata metadata)
    {
        if (!SourceName().IsMatch(source))
            throw new ArgumentException($"Ungültiger Quellname: {source}");
        if (metadata.Source is { Length: > 0 } claimed && claimed != source)
            throw new ArgumentException($"source „{claimed}“ passt nicht zum API-Key (Quelle „{source}“)");
        var missing = new[] { ("externalId", metadata.ExternalId), ("title", metadata.Title), ("artist", metadata.Artist) }
            .Where(f => string.IsNullOrWhiteSpace(f.Item2)).Select(f => f.Item1).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"Pflichtfelder fehlen: {string.Join(", ", missing)}");
        if (metadata.ExternalId.Length > 200)
            throw new ArgumentException("externalId ist länger als 200 Zeichen");
        if (metadata.Year is < 1000 or > 9999 || metadata.Track is <= 0 || metadata.Bpm is <= 0 or > 1000)
            throw new ArgumentException("year, track oder bpm liegen außerhalb des gültigen Bereichs");
    }

    private static void Delete(string path)
    {
        foreach (var file in new[] { path, path + CoverSuffix })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex SourceName();
}
