// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.ValueObjects;

namespace Tonregal.Application.Abstractions;

/// <summary>Berechnet den Chromaprint-Fingerprint (fpcalc als Subprozess).</summary>
public interface IFingerprintService
{
    Task<Fingerprint> ComputeAsync(string path, CancellationToken ct);
}
