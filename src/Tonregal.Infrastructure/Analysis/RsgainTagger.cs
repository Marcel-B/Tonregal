// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;

namespace Tonregal.Infrastructure.Analysis;

/// <summary>
/// <c>rsgain custom -a -s i</c>: Track- und Album-Gain (EBU R128, Ziel −18 LUFS) direkt in die Tags,
/// alle Dateien eines Albums in einem Aufruf, damit der Album-Gain stimmt.
/// </summary>
public sealed class RsgainTagger(IOptions<AnalysisOptions> options) : IReplayGainTagger
{
    private const string InstallHint = "brew install rsgain";
    private string? _version;

    public async Task<string> TagAlbumAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(options.Value.TimeoutMinutes);
        var result = await ExternalProcess.RunAsync(
            options.Value.RsgainPath, ["custom", "-a", "-s", "i", .. paths], timeout, InstallHint, ct);
        if (result.ExitCode != 0)
            throw new InvalidDataException($"rsgain fehlgeschlagen (Exit {result.ExitCode}): {result.Stderr.Trim()}");
        return _version ??= await VersionAsync(timeout, ct);
    }

    private async Task<string> VersionAsync(TimeSpan timeout, CancellationToken ct)
    {
        var result = await ExternalProcess.RunAsync(options.Value.RsgainPath, ["--version"], timeout, InstallHint, ct);
        var line = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return line?.Replace("rsgain", "", StringComparison.OrdinalIgnoreCase).Trim() is { Length: > 0 } version
            ? version[..Math.Min(version.Length, 64)]
            : "unbekannt";
    }
}
