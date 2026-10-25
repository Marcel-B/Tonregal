// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.ValueObjects;

/// <summary>Chromaprint-Fingerprint, wie ihn fpcalc liefert (komprimiert, Base64).</summary>
public sealed record Fingerprint(string Value, TimeSpan Duration)
{
    public override string ToString() => Value;
}
