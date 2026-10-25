// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.ValueObjects;

namespace Tonregal.Infrastructure.Fingerprinting;

/// <summary>Ruft <c>fpcalc -json</c> (Chromaprint) als Subprozess auf; Chromaprint wird nie gelinkt.</summary>
public sealed class FpcalcFingerprintService(IOptions<MetadataOptions> options) : IFingerprintService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<Fingerprint> ComputeAsync(string path, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunAsync(["-json", path], ct);
        if (exitCode != 0)
            throw new InvalidDataException($"fpcalc konnte {Path.GetFileName(path)} nicht lesen: {stderr.Trim()}");
        return Parse(stdout);
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, stdout, _) = await RunAsync(["-version"], ct);
            return exitCode == 0 ? stdout.Trim() : null;
        }
        catch (ExternalToolUnavailableException)
        {
            return null;
        }
    }

    internal static Fingerprint Parse(string json)
    {
        var output = JsonSerializer.Deserialize<FpcalcOutput>(json)
            ?? throw new InvalidDataException("fpcalc lieferte keine Ausgabe");
        if (string.IsNullOrEmpty(output.Fingerprint))
            throw new InvalidDataException("fpcalc lieferte keinen Fingerprint");
        return new Fingerprint(output.Fingerprint, TimeSpan.FromSeconds(output.Duration));
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(options.Value.FpcalcPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new Win32Exception("Process.Start lieferte null");
        }
        catch (Win32Exception e)
        {
            throw new ExternalToolUnavailableException(
                $"fpcalc nicht gefunden ({options.Value.FpcalcPath}) – brew install chromaprint", e);
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(Timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            return (process.ExitCode, await stdout, await stderr);
        }
    }

    private sealed record FpcalcOutput(
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("fingerprint")] string? Fingerprint);
}
