// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Domain.Entities;

/// <summary>Die physische Datei. Ein Track kann mehrere haben (z. B. MP3 und FLAC).</summary>
public class AudioFile
{
    public int Id { get; set; }
    public int TrackId { get; set; }
    public Track Track { get; set; } = null!;

    /// <summary>Pfad relativ zum Library-Wurzelordner.</summary>
    public required string Path { get; set; }
    public AudioFormat Format { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public long SizeBytes { get; set; }
    public string? Fingerprint { get; set; }

    /// <summary>SHA-256 des Dateiinhalts, hex.</summary>
    public required string FileHash { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public FileSource Source { get; set; }

    public List<AnalysisResult> AnalysisResults { get; set; } = [];
}
