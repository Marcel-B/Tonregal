// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application.Abstractions;

public sealed record ScanProgress(int Processed, int Total, string? CurrentPath);

public sealed record ScanError(string Path, string Message);

public sealed record ScanResult(
    int Added, int Updated, int Removed, int Unchanged, IReadOnlyList<ScanError> Errors, TimeSpan Duration);

/// <summary>Die Library ist nicht erreichbar (SSD nicht gemountet, Mount-Marker fehlt).</summary>
public sealed class LibraryUnavailableException(string message) : Exception(message);

/// <summary>Liest die Library-Ordner und gleicht den DB-Index mit den Tags der Dateien ab.</summary>
public interface ILibraryScanner
{
    /// <exception cref="LibraryUnavailableException">Mount-Marker fehlt.</exception>
    Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress, CancellationToken ct);
}
