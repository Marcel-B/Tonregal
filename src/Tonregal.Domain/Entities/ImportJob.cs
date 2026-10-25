// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>Ein Datei-Durchlauf durch die Import-Pipeline; echte Historie, nicht aus den Tags rekonstruierbar.</summary>
public class ImportJob
{
    public int Id { get; set; }
    public required string SourcePath { get; set; }
    public ImportState State { get; set; } = ImportState.New;
    public FileSource Source { get; set; } = FileSource.Inbox;
    public double? MatchScore { get; set; }

    /// <summary>Kandidatenliste aus dem Metadaten-Abgleich als JSON.</summary>
    public string? Candidates { get; set; }

    /// <summary>Idempotenzschlüssel der Ingest-API (Quelle + externalId).</summary>
    public string? ExternalId { get; set; }
    public int? AudioFileId { get; set; }
    public AudioFile? AudioFile { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
