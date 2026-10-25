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
public sealed class FpcalcFingerprintService(IOptions<MetadataOptions> options, ITagReader tags) : IFingerprintService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<Fingerprint> ComputeAsync(string path, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunAsync(["-json", path], null, ct);
        if (exitCode == 0)
            return Parse(stdout);

        // Alte Ripper haben MP3-Daten in eine WAV-Hülle gepackt und einen ID3-Tag davorgesetzt.
        // Neuere FFmpeg-Versionen erkennen das nicht mehr; ohne den Tag klappt es.
        if (Id3TagLength(path) is { } offset)
        {
            try
            {
                return await ComputeWithoutId3Async(path, offset, ct);
            }
            catch (InvalidDataException)
            {
            }
        }

        throw new InvalidDataException($"fpcalc konnte {Path.GetFileName(path)} nicht lesen: {stderr.Trim()}");
    }

    /// <summary>Gibt fpcalc die Datei ab <paramref name="offset"/> über stdin.</summary>
    internal async Task<Fingerprint> ComputeWithoutId3Async(string path, long offset, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunAsync(["-json", "-"], async stdin =>
        {
            await using var file = File.OpenRead(path);
            file.Seek(offset, SeekOrigin.Begin);
            await file.CopyToAsync(stdin, ct);
        }, ct);
        if (exitCode != 0)
            throw new InvalidDataException($"fpcalc konnte {Path.GetFileName(path)} nicht lesen: {stderr.Trim()}");

        // Über stdin kennt fpcalc die Länge nicht und meldet 0; AcoustID braucht sie aber.
        var fingerprint = Parse(stdout);
        return fingerprint.Duration > TimeSpan.Zero
            ? fingerprint
            : fingerprint with { Duration = tags.Read(path).Properties.Duration };
    }

    /// <summary>Länge eines ID3v2-Tags am Dateianfang (inkl. Header/Footer) oder <c>null</c>.</summary>
    internal static long? Id3TagLength(string path)
    {
        Span<byte> header = stackalloc byte[10];
        using (var file = File.OpenRead(path))
        {
            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
                return null;
        }

        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3' || ((header[6] | header[7] | header[8] | header[9]) & 0x80) != 0)
            return null;
        var size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
        var footer = (header[5] & 0x10) != 0 ? 10 : 0;
        return 10L + size + footer;
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, stdout, _) = await RunAsync(["-version"], null, ct);
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

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        IEnumerable<string> arguments, Func<Stream, Task>? writeStdin, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(options.Value.FpcalcPath)
        {
            RedirectStandardInput = writeStdin is not null,
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
                if (writeStdin is not null)
                {
                    try
                    {
                        await writeStdin(process.StandardInput.BaseStream);
                        process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                        // fpcalc hat stdin vorzeitig geschlossen (z. B. -length erreicht oder Fehler).
                    }
                }

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
