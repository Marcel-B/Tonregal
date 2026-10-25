// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>Rohergebnis eines Analysewerkzeugs; der gewählte Wert landet im Track und im Tag.</summary>
public class AnalysisResult
{
    public int Id { get; set; }
    public int AudioFileId { get; set; }
    public AudioFile AudioFile { get; set; } = null!;
    public required string Tool { get; set; }
    public required string Version { get; set; }
    public double? Bpm { get; set; }
    public string? Key { get; set; }
    public double? KeyConfidence { get; set; }
    public double? Loudness { get; set; }
    public DateTimeOffset RanAt { get; set; }
}
