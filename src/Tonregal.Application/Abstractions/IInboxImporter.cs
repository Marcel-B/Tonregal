// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;

namespace Tonregal.Application.Abstractions;

/// <param name="Created">neue Jobs</param>
/// <param name="Restarted">Jobs, deren Datei sich geändert hat</param>
/// <param name="Removed">Jobs, deren Datei verschwunden ist</param>
/// <param name="Waiting">Dateien, die noch kopiert werden (Größe nicht stabil)</param>
public sealed record InboxSyncResult(int Created, int Restarted, int Removed, int Waiting);

/// <summary>Inbox → ImportJobs → Fingerprint und Metadaten-Abgleich bis Matched oder NeedsReview.</summary>
public interface IInboxImporter
{
    /// <summary>Legt für neue Dateien Jobs an und schließt Jobs, deren Datei weg ist.</summary>
    /// <exception cref="LibraryUnavailableException">Mount-Marker fehlt.</exception>
    Task<InboxSyncResult> SyncAsync(CancellationToken ct);

    /// <summary>Verarbeitet den ältesten offenen Job; <c>null</c>, wenn keiner wartet.</summary>
    /// <exception cref="ExternalToolUnavailableException">fpcalc oder AcoustID nicht verfügbar; der Job bleibt offen.</exception>
    Task<ImportJob?> ProcessNextAsync(CancellationToken ct);
}
