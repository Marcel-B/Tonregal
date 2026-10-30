// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application.Abstractions;

/// <summary>Eine Altbestand-Datei, die nachweislich in der Library liegt.</summary>
/// <param name="AlreadyInLibrary">Byte-gleich schon da gewesen, statt von Tonregal kopiert.</param>
/// <param name="LibraryPath">Die Datei in der Library, die das belegt (relativ zur Library).</param>
public sealed record LegacyCleanupFile(int JobId, string Path, long SizeBytes, bool AlreadyInLibrary, string LibraryPath);

/// <summary>Altbestand-Datei, die als übernommen gilt, aber bleibt, weil der Nachweis fehlt.</summary>
public sealed record LegacyCleanupSkipped(int JobId, string Path, string Reason);

public sealed record LegacyCleanupPreview(
    string TrashFolder, IReadOnlyList<LegacyCleanupFile> Files, IReadOnlyList<LegacyCleanupSkipped> Skipped)
{
    public int Count => Files.Count;
    public long SizeBytes => Files.Sum(f => f.SizeBytes);
}

public sealed record LegacyCleanupResult(int Moved, long SizeBytes, string TrashFolder, IReadOnlyList<LegacyCleanupSkipped> Skipped);

/// <summary>
/// „Erledigtes aufräumen“: Altbestand-Dateien, die übernommen sind oder byte-gleich schon in der
/// Library lagen, wandern nach <c>Library/.trash/{Datum}/Altbestand/</c>.
/// </summary>
public interface ILegacyCleanup
{
    /// <summary>Was ein Aufräumen jetzt verschieben würde und was trotz Übernahme bleibt.</summary>
    Task<LegacyCleanupPreview> PreviewAsync(CancellationToken ct);

    /// <summary>
    /// Verschiebt die genannten Dateien, prüft dabei jede erneut; was den Nachweis inzwischen nicht
    /// mehr besteht, bleibt liegen und steht in <see cref="LegacyCleanupResult.Skipped"/>.
    /// </summary>
    Task<LegacyCleanupResult> CleanAsync(IReadOnlyCollection<int> jobIds, CancellationToken ct);
}
