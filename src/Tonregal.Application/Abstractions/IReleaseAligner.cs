// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application.Abstractions;

/// <param name="ReleaseId">Das Release, das jetzt alle Tracks tragen.</param>
/// <param name="Changed">Dateien mit neuen Tags.</param>
/// <param name="Skipped">Dateien, deren Aufnahme auf diesem Release nicht vorkommt.</param>
public sealed record ReleaseAlignResult(Guid? ReleaseId, int Changed, int Skipped);

/// <summary>
/// Navidrome trennt Alben nach MusicBrainz-Release-ID: Tragen die Tracks eines Albums verschiedene
/// Veröffentlichungen (z. B. EU- und US-CD), erscheint es mehrfach. Der Aligner gibt allen Tracks dasselbe Release.
/// </summary>
public interface IReleaseAligner
{
    /// <summary>Anzahl verschiedener Release-IDs in den Tags der Album-Dateien.</summary>
    Task<int> CountReleasesAsync(int albumId, CancellationToken ct);

    /// <summary>
    /// Schreibt bei allen Dateien das Release der Mehrheit in die Tags und benennt sie nach der
    /// Ordnerregel um, wenn sich Tracknummer oder Jahr ändern.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Album gibt es nicht.</exception>
    /// <exception cref="LibraryUnavailableException">Mount-Marker fehlt.</exception>
    Task<ReleaseAlignResult> AlignAsync(int albumId, CancellationToken ct);
}
