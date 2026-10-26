// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;

namespace Tonregal.Application.Abstractions;

public sealed record DuplicateScanResult(int Open, int Created, int Removed);

/// <summary>Findet Dubletten in der Library und setzt die Entscheidung um.</summary>
public interface IDuplicateService
{
    /// <summary>
    /// Gleicht die offenen <see cref="DuplicateGroup"/>s mit dem aktuellen Index ab: neue Funde
    /// anlegen, erledigte entfernen. Gruppen, bei denen alle Dateien behalten wurden, kommen nicht wieder.
    /// </summary>
    Task<DuplicateScanResult> DetectAsync(CancellationToken ct);

    /// <summary>Behält die genannten Dateien und verschiebt die übrigen der Gruppe nach <c>Library/.trash</c>.</summary>
    /// <exception cref="KeyNotFoundException">Gruppe gibt es nicht.</exception>
    /// <exception cref="InvalidOperationException">Gruppe ist schon entschieden oder die Auswahl passt nicht zur Gruppe.</exception>
    Task ResolveAsync(int groupId, IReadOnlyCollection<int> keepFileIds, CancellationToken ct);

    /// <summary>Löscht Papierkorb-Ordner, die älter als die Aufbewahrungszeit sind.</summary>
    int PurgeTrash();
}
