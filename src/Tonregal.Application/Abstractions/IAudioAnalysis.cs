// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Tonregal.Application.Abstractions;

/// <summary>Rohergebnis der Tempo- und Tonartanalyse eines Tracks.</summary>
public sealed record BpmKeyResult(double? Bpm, string? Key, double? KeyConfidence, string Version);

/// <summary>Tempo und Tonart einer Datei (librosa-Script als Subprozess).</summary>
public interface IBpmKeyAnalyzer
{
    public const string ToolName = "librosa";

    /// <exception cref="ExternalToolUnavailableException">Python, Script oder librosa fehlen.</exception>
    /// <exception cref="InvalidDataException">Die Datei ließ sich nicht analysieren.</exception>
    Task<BpmKeyResult> AnalyzeAsync(string path, CancellationToken ct);
}

/// <summary>Track- und Album-Gain per rsgain, direkt in die Tags der Dateien.</summary>
public interface IReplayGainTagger
{
    public const string ToolName = "rsgain";

    /// <summary>Analysiert die Dateien eines Albums gemeinsam und schreibt die Gains; liefert die rsgain-Version.</summary>
    /// <exception cref="ExternalToolUnavailableException">rsgain fehlt.</exception>
    /// <exception cref="InvalidDataException">rsgain ist fehlgeschlagen.</exception>
    Task<string> TagAlbumAsync(IReadOnlyList<string> paths, CancellationToken ct);
}

public sealed record AnalysisCounts(int Files, int Analyzed, int Pending, int WithoutResult);

public sealed record AlbumAnalysis(string Album, int Files, IReadOnlyList<string> Errors);

/// <summary>Arbeitet die Library albumweise ab: BPM/Tonart je Datei, dann ReplayGain fürs Album.</summary>
public interface ILibraryAnalyzer
{
    Task<AnalysisCounts> CountAsync(CancellationToken ct);

    /// <summary>Analysiert das nächste Album mit offenen Dateien; <c>null</c>, wenn nichts mehr offen ist.</summary>
    /// <exception cref="ExternalToolUnavailableException">Ein Werkzeug fehlt; nichts wurde gespeichert.</exception>
    /// <exception cref="LibraryUnavailableException">Die SSD ist nicht gemountet.</exception>
    Task<AlbumAnalysis?> AnalyzeNextAlbumAsync(CancellationToken ct);
}
