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
        var partial = TryParse(stdout);

        // Alte Ripper haben MP3-Daten in eine WAV-Hülle gepackt, teils mit ID3-Tag oder fremden Bytes
        // davor. Neuere FFmpeg-Versionen erkennen das nicht mehr; ab dem Beginn der Audiodaten klappt es.
        foreach (var offset in AudioDataOffsets(path))
        {
            var fromOffset = await RunFromOffsetAsync(path, offset, ct);
            if (fromOffset.ExitCode == 0)
                return WithDuration(Parse(fromOffset.Stdout), path);
            partial ??= TryParse(fromOffset.Stdout) is { } p ? WithDuration(p, path) : null;
        }

        // Bei einem Lesefehler mitten in der Datei (kaputter Frame, Reste am Ende) meldet fpcalc
        // einen Fehler, liefert aber den Fingerprint über alles bis dahin – der reicht für AcoustID.
        return partial ?? throw new InvalidDataException($"fpcalc konnte {Path.GetFileName(path)} nicht lesen: {stderr.Trim()}");
    }

    /// <summary>Gibt fpcalc die Datei ab <paramref name="offset"/> über stdin.</summary>
    internal async Task<Fingerprint> ComputeFromOffsetAsync(string path, long offset, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunFromOffsetAsync(path, offset, ct);
        if (exitCode != 0)
            throw new InvalidDataException($"fpcalc konnte {Path.GetFileName(path)} nicht lesen: {stderr.Trim()}");
        return WithDuration(Parse(stdout), path);
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunFromOffsetAsync(string path, long offset, CancellationToken ct) =>
        RunAsync(["-json", "-"], async stdin =>
        {
            await using var file = File.OpenRead(path);
            file.Seek(offset, SeekOrigin.Begin);
            await file.CopyToAsync(stdin, ct);
        }, ct);

    /// <summary>Über stdin kennt fpcalc die Länge nicht und meldet 0; AcoustID braucht sie aber.</summary>
    private Fingerprint WithDuration(Fingerprint fingerprint, string path) =>
        fingerprint.Duration > TimeSpan.Zero
            ? fingerprint
            : fingerprint with { Duration = tags.Read(path).Properties.Duration };

    /// <summary>
    /// Mögliche Anfänge der Audiodaten, in dieser Reihenfolge: hinter einem ID3-Tag, eine WAV-Hülle,
    /// deren Datenblock und der erste MP3-Frame, auf den ein zweiter passender folgt.
    /// </summary>
    internal static IEnumerable<long> AudioDataOffsets(string path)
    {
        const int ScanLength = 1 << 20;
        var id3 = Id3TagLength(path);
        byte[] head;
        using (var file = File.OpenRead(path))
        {
            head = new byte[(int)Math.Min(file.Length, ScanLength)];
            file.ReadExactly(head);
        }

        var start = (int)Math.Min(id3 ?? 0, head.Length);
        var offsets = new List<long>();
        if (id3 is { } tagLength)
            offsets.Add(tagLength);
        var riff = head.AsSpan(start).IndexOf("RIFF"u8);
        if (riff >= 0 && RiffDataOffset(head, start + riff) is { } data)
        {
            offsets.Add(start + riff);
            offsets.Add(data);
        }

        if (FirstMpegFrame(head, start) is { } frame)
            offsets.Add(frame);
        return offsets.Distinct();
    }

    /// <summary>Beginn des <c>data</c>-Chunks der RIFF/WAVE-Hülle, die bei <paramref name="start"/> beginnt.</summary>
    internal static long? RiffDataOffset(ReadOnlySpan<byte> head, int start)
    {
        if (head.Length < start + 12 || !head.Slice(start, 4).SequenceEqual("RIFF"u8) || !head.Slice(start + 8, 4).SequenceEqual("WAVE"u8))
            return null;
        var position = start + 12;
        while (position + 8 <= head.Length)
        {
            var size = BitConverter.ToUInt32(head.Slice(position + 4, 4));
            if (head.Slice(position, 4).SequenceEqual("data"u8))
                return position + 8;
            position += 8 + (int)Math.Min(size + (size & 1), int.MaxValue - position);
        }

        return null;
    }

    /// <summary>Erster MPEG-Audio-Layer-III-Frame ab <paramref name="start"/>, bestätigt durch den direkt folgenden Frame.</summary>
    internal static long? FirstMpegFrame(ReadOnlySpan<byte> head, int start)
    {
        for (var i = start; i + 4 <= head.Length; i++)
        {
            if (MpegFrameLength(head[i..]) is not { } length || i + length + 4 > head.Length)
                continue;
            if (MpegFrameLength(head[(i + length)..]) is not null)
                return i;
        }

        return null;
    }

    private static readonly int[] BitratesV1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] BitratesV2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] SampleRatesV1 = [44100, 48000, 32000];

    /// <summary>Länge eines Layer-III-Frames, dessen Header an Position 0 steht, sonst <c>null</c>.</summary>
    internal static int? MpegFrameLength(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4 || frame[0] != 0xFF || (frame[1] & 0xE0) != 0xE0)
            return null;
        var version = (frame[1] >> 3) & 3; // 3 = MPEG 1, 2 = MPEG 2, 0 = MPEG 2.5
        var layer = (frame[1] >> 1) & 3;   // 1 = Layer III
        var bitrateIndex = frame[2] >> 4;
        var sampleRateIndex = (frame[2] >> 2) & 3;
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
            return null;

        var mpeg1 = version == 3;
        var bitrate = (mpeg1 ? BitratesV1 : BitratesV2)[bitrateIndex] * 1000;
        var sampleRate = SampleRatesV1[sampleRateIndex] / (mpeg1 ? 1 : version == 2 ? 2 : 4);
        var padding = (frame[2] >> 1) & 1;
        return (mpeg1 ? 144 : 72) * bitrate / sampleRate + padding;
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

    private static Fingerprint? TryParse(string json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : Parse(json);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException)
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
