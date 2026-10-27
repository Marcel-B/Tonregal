// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;

namespace Tonregal.Application.Abstractions;

/// <summary>
/// Was die Quelle über ein Stück weiß. Pflicht sind <see cref="ExternalId"/>, <see cref="Title"/> und
/// <see cref="Artist"/>; ohne <see cref="Album"/> wird das Stück eine Single unter seinem Titel.
/// </summary>
public sealed record IngestMetadata
{
    /// <summary>Eindeutig innerhalb der Quelle; derselbe Wert ein zweites Mal legt nichts neu an.</summary>
    public string ExternalId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string? Album { get; init; }
    public int? Year { get; init; }
    public int? Track { get; init; }
    public string? Genre { get; init; }
    public double? Bpm { get; init; }
    public string? Key { get; init; }

    /// <summary>Optional; muss zur Quelle des API-Keys passen.</summary>
    public string? Source { get; init; }
}

/// <param name="Created"><c>false</c>, wenn es zur externalId schon einen Job gab.</param>
public sealed record IngestResult(ImportJob Job, bool Created);

/// <summary>Nimmt ein Stück über die API an: Datei unter <c>Inbox/_api/{Quelle}/</c>, Job direkt im Zustand Matched.</summary>
public interface IIngestService
{
    /// <exception cref="ArgumentException">Metadaten unvollständig oder keine lesbare Audiodatei.</exception>
    /// <exception cref="LibraryUnavailableException">SSD nicht gemountet.</exception>
    Task<IngestResult> IngestAsync(string source, IngestMetadata metadata, string fileName, Stream audio, Stream? cover, CancellationToken ct);

    /// <summary>Der Job mit dieser ID, sofern er von der Quelle stammt.</summary>
    Task<ImportJob?> FindAsync(string source, int jobId, CancellationToken ct);

    /// <summary>Gespeicherter Idempotenzschlüssel: Quelle und externalId.</summary>
    static string Key(string source, string externalId) => $"{source}:{externalId}";
}
