// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Tonregal.Infrastructure.Fingerprinting;

namespace Tonregal.Infrastructure.Tagging;

/// <summary>
/// Sagt in einem Satz, was in einer Datei steckt, die weder TagLib noch ffmpeg als Ton lesen: leer,
/// nur Nullbytes (Inhalt beim Kopieren oder durch einen Plattenfehler verloren), eine Webseite oder
/// etwas anderes, dann mit ihren ersten Bytes.
/// </summary>
internal static class AudioSniffer
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Describe(string path)
    {
        long length;
        byte[] head;
        long firstData;
        var tag = FpcalcFingerprintService.Id3TagLength(path) ?? 0;
        using (var file = File.OpenRead(path))
        {
            length = file.Length;
            if (length == 0)
                return "Die Datei ist leer (0 Bytes).";
            head = new byte[(int)Math.Min(length, 64)];
            file.ReadExactly(head);
            file.Position = Math.Min(tag, length);
            firstData = FirstNonZero(file);
        }

        if (firstData < 0)
        {
            return tag > 0
                ? $"Hinter dem ID3-Tag ({Size(tag)}) stehen nur Nullbytes ({Size(length - tag)}): Der Ton ist verloren, vermutlich beim Kopieren oder durch einen Plattenfehler."
                : $"Die Datei besteht nur aus Nullbytes ({Size(length)}): Der Inhalt ist verloren, vermutlich beim Kopieren oder durch einen Plattenfehler.";
        }

        var text = Encoding.ASCII.GetString(head).TrimStart();
        if (text.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return "Die Datei ist eine Webseite (HTML), kein Ton.";

        var zeros = firstData - tag;
        var where = zeros >= 1024
            ? $"Nach {Size(zeros)} Nullbytes{(tag > 0 ? " hinter dem ID3-Tag" : "")} erkennt auch ffmpeg keinen Ton"
            : "Auch ffmpeg erkennt keinen Ton";
        return $"{where}; die Datei beginnt mit {Preview(head)}.";
    }

    /// <summary>Position des ersten Bytes ungleich 0 ab der aktuellen Stelle, <c>-1</c>, wenn keins mehr kommt.</summary>
    private static long FirstNonZero(Stream stream)
    {
        var buffer = new byte[1 << 16];
        while (stream.Position < stream.Length)
        {
            var start = stream.Position;
            var read = stream.Read(buffer);
            if (read == 0)
                break;
            var index = buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0);
            if (index >= 0)
                return start + index;
        }

        return -1;
    }

    /// <summary>Die ersten 16 Bytes als Hex, dazu als Text, wo sie lesbar sind (RIFF, ID3, ftyp …).</summary>
    private static string Preview(byte[] head)
    {
        var bytes = head.AsSpan(0, Math.Min(head.Length, 16));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        hex = string.Join(' ', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
        var ascii = new string(bytes.ToArray().Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
        return $"„{hex}“ („{ascii}“)";
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Bytes",
        < 1024 * 1024 => string.Create(German, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(German, $"{bytes / 1024.0 / 1024:0.#} MB"),
    };
}
