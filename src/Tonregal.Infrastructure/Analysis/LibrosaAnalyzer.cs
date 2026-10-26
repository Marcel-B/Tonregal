// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;

namespace Tonregal.Infrastructure.Analysis;

/// <summary>Ruft <c>analysis/analyze.py</c> im Analyse-venv auf (librosa, nur als Prozess).</summary>
public sealed class LibrosaAnalyzer(IOptions<AnalysisOptions> analysis, IOptions<TonregalOptions> tonregal) : IBpmKeyAnalyzer
{
    private const string InstallHint = "venv mit ./deploy/deploy-tonregal.sh anlegen";

    public async Task<BpmKeyResult> AnalyzeAsync(string path, CancellationToken ct)
    {
        var script = ScriptPath;
        if (!File.Exists(script))
            throw new ExternalToolUnavailableException($"Analyse-Script {script} fehlt");

        var result = await ExternalProcess.RunAsync(
            PythonPath, [script, path], TimeSpan.FromMinutes(analysis.Value.TimeoutMinutes), InstallHint, ct);
        // Exit 1 = Datei nicht lesbar; alles andere (z. B. ModuleNotFoundError beim Import) ist ein Setup-Problem.
        if (result.ExitCode == 1 && !string.IsNullOrWhiteSpace(result.Stderr) && !result.Stderr.Contains("ModuleNotFoundError"))
            throw new InvalidDataException($"Analyse von {Path.GetFileName(path)} fehlgeschlagen: {LastLine(result.Stderr)}");
        if (result.ExitCode != 0)
            throw new ExternalToolUnavailableException($"Analyse-Script fehlgeschlagen: {LastLine(result.Stderr)} – {InstallHint}");
        return Parse(result.Stdout);
    }

    internal static BpmKeyResult Parse(string json)
    {
        var output = JsonSerializer.Deserialize<ScriptOutput>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Analyse-Script lieferte keine Ausgabe");
        return new BpmKeyResult(
            output.Bpm is > 0 ? output.Bpm : null, string.IsNullOrWhiteSpace(output.Key) ? null : output.Key,
            output.KeyConfidence, output.Version ?? "unbekannt");
    }

    private string PythonPath => string.IsNullOrWhiteSpace(analysis.Value.PythonPath)
        ? Path.Combine(tonregal.Value.MusicRoot, "tools", "analysis-venv", "bin", "python")
        : analysis.Value.PythonPath;

    private string ScriptPath => string.IsNullOrWhiteSpace(analysis.Value.ScriptPath)
        ? Path.Combine(AppContext.BaseDirectory, "analysis", "analyze.py")
        : analysis.Value.ScriptPath;

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private sealed record ScriptOutput(double? Bpm, string? Key, double? KeyConfidence, string? Version);
}
