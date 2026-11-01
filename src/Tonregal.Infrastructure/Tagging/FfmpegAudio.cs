// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;

namespace Tonregal.Infrastructure.Tagging;

/// <summary>Was ffprobe in einer Datei findet: der erste Tonstrom und die Tags von Container und Strom.</summary>
public sealed record ProbedAudio(
    string Container, string Codec, TimeSpan Duration, int Bitrate, int SampleRate, int Channels,
    IReadOnlyDictionary<string, string> Tags)
{
    /// <summary>Endung, unter der Tonregal diesen Inhalt führt, oder <c>null</c> für Formate, die es nicht kennt.</summary>
    public string? Extension => Codec switch
    {
        "mp3" => ".mp3",
        "flac" => ".flac",
        "aac" or "alac" => ".m4a",
        "vorbis" => ".ogg",
        "opus" => ".opus",
        _ when Codec.StartsWith("pcm_", StringComparison.Ordinal) => ".wav",
        _ => null,
    };

    public AudioFormat Format => Codec switch
    {
        "mp3" => AudioFormat.Mp3,
        "flac" => AudioFormat.Flac,
        "aac" => AudioFormat.Aac,
        "alac" => AudioFormat.Alac,
        "vorbis" => AudioFormat.Ogg,
        "opus" => AudioFormat.Opus,
        _ when Codec.StartsWith("pcm_", StringComparison.Ordinal) => AudioFormat.Wav,
        _ => AudioFormat.Unknown,
    };

    /// <summary>Lesbarer Name des Inhalts für Meldungen.</summary>
    public string Description => Codec switch
    {
        "mp3" => "MP3",
        "mp2" => "MPEG Layer II (MP2)",
        "wmav1" or "wmav2" or "wmapro" or "wmalossless" => "WMA",
        "aac" => "AAC",
        "alac" => "Apple Lossless",
        "flac" => "FLAC",
        "vorbis" => "Ogg Vorbis",
        "opus" => "Opus",
        "ac3" or "eac3" => "Dolby Digital",
        _ when Codec.StartsWith("pcm_", StringComparison.Ordinal) => "WAV (PCM)",
        _ => Codec,
    };
}

/// <summary>
/// ffprobe und ffmpeg für Dateien, die TagLib nicht öffnet. TagLib erkennt das Format an der Endung
/// und gibt auf, wenn der Ton nicht dort beginnt, wo es ihn erwartet (Nullbytes oder ein ID3-Tag
/// vor einer WAV-Hülle) oder ein Tag-Frame kaputt ist; ffmpeg erkennt den Inhalt selbst und sucht weiter.
/// </summary>
public sealed class FfmpegAudio(IOptions<MetadataOptions> options)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RemuxTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Der erste Tonstrom der Datei, <c>null</c>, wenn ffprobe keinen findet.</summary>
    /// <exception cref="ExternalToolUnavailableException">ffprobe fehlt.</exception>
    public ProbedAudio? Probe(string path)
    {
        var (exitCode, stdout, _) = Run(options.Value.FfprobePath, "brew install ffmpeg",
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-select_streams", "a:0", path],
            ProbeTimeout, CancellationToken.None).GetAwaiter().GetResult();
        return exitCode == 0 ? Parse(stdout) : null;
    }

    internal static ProbedAudio? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            return null;
        var stream = streams[0];
        var format = root.TryGetProperty("format", out var f) ? f : default;

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Container zuerst, der Strom ergänzt (Ogg/Opus tragen ihre Tags am Strom).
        foreach (var source in new[] { format, stream })
        {
            if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("tags", out var t))
            {
                foreach (var tag in t.EnumerateObject())
                {
                    if (tag.Value.GetString() is { Length: > 0 } value)
                        tags.TryAdd(tag.Name, value);
                }
            }
        }

        var duration = Number(stream, "duration") ?? (format.ValueKind == JsonValueKind.Object ? Number(format, "duration") : null) ?? 0;
        // In reinen Nullbytes „erkennt“ ffprobe einen MP3-Strom, aber ohne Länge: kein Ton.
        if (duration <= 0)
            return null;
        var bitrate = Number(stream, "bit_rate") ?? (format.ValueKind == JsonValueKind.Object ? Number(format, "bit_rate") : null) ?? 0;
        return new ProbedAudio(
            format.ValueKind == JsonValueKind.Object && format.TryGetProperty("format_name", out var name) ? name.GetString() ?? "" : "",
            stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : "",
            TimeSpan.FromSeconds(duration),
            (int)Math.Round(bitrate / 1000),
            (int)(Number(stream, "sample_rate") ?? 0),
            stream.TryGetProperty("channels", out var channels) && channels.TryGetInt32(out var c) ? c : 0,
            tags);
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>
    /// Verpackt den ersten Tonstrom ohne neu zu kodieren in eine saubere Datei vom Typ der Endung von
    /// <paramref name="target"/>. Die Tags kommen mit, eingebettete Bilder nicht (die kaputten Frames
    /// sind meist der Grund für den Umweg).
    /// </summary>
    /// <exception cref="InvalidDataException">ffmpeg konnte die Datei nicht umverpacken.</exception>
    /// <exception cref="ExternalToolUnavailableException">ffmpeg fehlt.</exception>
    public async Task RemuxAsync(string source, string target, CancellationToken ct)
    {
        List<string> arguments = ["-nostdin", "-v", "error", "-y", "-i", source, "-map", "0:a:0", "-map_metadata", "0", "-c:a", "copy"];
        var extension = Path.GetExtension(target).ToLowerInvariant();
        if (extension == ".mp3")
            arguments.AddRange(["-id3v2_version", "4"]);
        arguments.AddRange(["-f", extension switch
        {
            ".mp3" => "mp3",
            ".flac" => "flac",
            ".m4a" => "ipod",
            ".ogg" => "ogg",
            ".opus" => "opus",
            ".wav" => "wav",
            _ => throw new InvalidDataException($"Kein Container für {extension}"),
        }, target]);

        var (exitCode, _, stderr) = await Run(options.Value.FfmpegPath, "brew install ffmpeg", arguments, RemuxTimeout, ct);
        if (exitCode != 0)
        {
            if (File.Exists(target))
                File.Delete(target);
            var reason = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            throw new InvalidDataException($"{Path.GetFileName(source)}: ffmpeg konnte die Datei nicht neu verpacken ({reason ?? $"Exit {exitCode}"})");
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> Run(
        string tool, string install, IEnumerable<string> arguments, TimeSpan limit, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(tool)
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
            throw new ExternalToolUnavailableException($"{Path.GetFileName(tool)} nicht gefunden ({tool}) – {install}", e);
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(limit);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                if (ct.IsCancellationRequested)
                    throw;
                throw new InvalidDataException($"{Path.GetFileName(tool)} brauchte länger als {limit.TotalSeconds:0} s");
            }

            return (process.ExitCode, await stdout, await stderr);
        }
    }
}
