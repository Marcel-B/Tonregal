// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;

namespace Tonregal.Application.Abstractions;

/// <summary>Zweite Hälfte der Import-Pipeline: Tags und Cover schreiben, Datei in die Library verschieben.</summary>
public interface ILibraryImporter
{
    /// <summary>
    /// Übernimmt einen Job im Zustand <see cref="ImportState.Matched"/>. Bei Erfolg ist der Job
    /// <see cref="ImportState.Imported"/> und die Datei liegt nur noch in der Library; sonst bleibt
    /// die Datei unverändert in der Inbox und der Job trägt den Fehler.
    /// </summary>
    Task<ImportJob> ImportAsync(int jobId, CancellationToken ct);
}
