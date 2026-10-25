// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.ValueObjects;

namespace Tonregal.Application.Abstractions;

/// <summary>Ein externes Werkzeug oder ein Dienst ist nicht nutzbar (nicht installiert, kein API-Key, nicht erreichbar).</summary>
public sealed class ExternalToolUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Berechnet den Chromaprint-Fingerprint (fpcalc als Subprozess).</summary>
public interface IFingerprintService
{
    /// <exception cref="ExternalToolUnavailableException">fpcalc fehlt.</exception>
    /// <exception cref="InvalidDataException">fpcalc konnte die Datei nicht lesen.</exception>
    Task<Fingerprint> ComputeAsync(string path, CancellationToken ct);

    /// <summary>Version von fpcalc oder <c>null</c>, wenn es nicht gefunden wird.</summary>
    Task<string?> GetVersionAsync(CancellationToken ct);
}
