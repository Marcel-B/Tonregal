// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>Ein Datei-Durchlauf durch die Import-Pipeline; echte Historie, nicht aus den Tags rekonstruierbar.</summary>
public class ImportJob
{
    public int Id { get; set; }

    /// <summary>Pfad relativ zur Inbox, mit <c>/</c> getrennt.</summary>
    public required string SourcePath { get; set; }
    public ImportState State { get; set; } = ImportState.New;
    public FileSource Source { get; set; } = FileSource.Inbox;

    /// <summary>Größe und Änderungszeit beim Einlesen; ändert sich die Datei, beginnt der Job von vorn.</summary>
    public long SizeBytes { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }

    /// <summary>SHA-256 des Dateiinhalts, hex.</summary>
    public string? FileHash { get; set; }
    public AudioFormat Format { get; set; }
    public int DurationMs { get; set; }
    public string? Fingerprint { get; set; }

    /// <summary>Was in den Tags der Datei steht, zum Vergleich mit dem Treffer.</summary>
    public string? TagTitle { get; set; }
    public string? TagArtist { get; set; }
    public string? TagAlbum { get; set; }

    /// <summary>Bester AcoustID-Score.</summary>
    public double? MatchScore { get; set; }

    /// <summary>Kandidatenliste aus dem Metadaten-Abgleich als JSON.</summary>
    public string? Candidates { get; set; }

    /// <summary>Gewählter Kandidat (Aufnahme auf einem Release), bei Review ein Vorschlag.</summary>
    public Guid? SelectedRecordingId { get; set; }
    public Guid? SelectedReleaseId { get; set; }

    /// <summary>Warum der Treffer nicht automatisch übernommen wird.</summary>
    public string? ReviewReason { get; set; }

    /// <summary>Pfad relativ zur Library nach dem Übernehmen.</summary>
    public string? LibraryPath { get; set; }

    /// <summary>Idempotenzschlüssel der Ingest-API (Quelle + externalId).</summary>
    public string? ExternalId { get; set; }

    /// <summary>Metadaten, die der Aufrufer der Ingest-API mitgeschickt hat, als JSON.</summary>
    public string? IngestMetadata { get; set; }
    public int? AudioFileId { get; set; }
    public AudioFile? AudioFile { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public const string VanishedReason = "Datei liegt nicht mehr in der Inbox";
    public const string RejectedByUser = "In der Inbox verworfen";
    public const string AlreadyInLibrary = "Liegt schon identisch in der Library: ";

    public static readonly ImportState[] ActiveStates =
        [ImportState.New, ImportState.Fingerprinted, ImportState.Matched, ImportState.NeedsReview];

    /// <summary>Ordner relativ zur Inbox; Dateien eines Ordners gelten als ein Album-Kandidat.</summary>
    public string Group => SourcePath.LastIndexOf('/') is var i and > 0 ? SourcePath[..i] : "";

    /// <summary>Setzt den Job auf Anfang, z. B. weil sich die Datei geändert hat.</summary>
    public void Reset()
    {
        State = ImportState.New;
        FileHash = null;
        Fingerprint = null;
        MatchScore = null;
        Candidates = null;
        SelectedRecordingId = null;
        SelectedReleaseId = null;
        ReviewReason = null;
        Error = null;
        FinishedAt = null;
    }
}
