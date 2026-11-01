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
        byte[] sample;
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
            file.Position = Math.Max(firstData, 0);
            sample = new byte[(int)Math.Min(file.Length - file.Position, SampleLength)];
            file.ReadExactly(sample);
        }

        if (firstData < 0)
        {
            return tag > 0
                ? $"Hinter dem ID3-Tag ({Size(tag)}) stehen nur Nullbytes ({Size(length - tag)}): Der Ton ist verloren, vermutlich beim Kopieren oder durch einen Plattenfehler."
                : $"Die Datei besteht nur aus Nullbytes ({Size(length)}): Der Inhalt ist verloren, vermutlich beim Kopieren oder durch einen Plattenfehler.";
        }

        // Alte Sammlungen von beschädigten Platten: Die Datei zeigt auf Blöcke, die zu einer anderen
        // gehörten. Dann steckt mitten darin Text (oft ein Stück Webseite) oder ungeordnete Daten.
        if (IsText(sample))
        {
            var text = Encoding.Latin1.GetString(sample);
            return HtmlMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase))
                ? $"Die Datei enthält HTML (eine Webseite oder ein Stück davon) statt Ton; sie beginnt mit {Preview(head)}."
                : $"Die Datei enthält Text statt Ton; sie beginnt mit {Preview(head)}.";
        }

        if (firstData == tag && sample.Length >= 4096 && Entropy(sample) > 7.5)
        {
            return "Die Datei enthält von Anfang an ungeordnete Daten ohne einen einzigen Audio-Kopf, also fremden Inhalt: "
                + "vermutlich nach einem Dateisystemschaden auf der alten Platte (die Datei zeigt auf Blöcke einer anderen) "
                + $"oder verschlüsselt. Der Ton ist darin nicht zu finden; sie beginnt mit {Preview(head)}.";
        }

        var zeros = firstData - tag;
        var where = zeros >= 1024
            ? $"Nach {Size(zeros)} Nullbytes{(tag > 0 ? " hinter dem ID3-Tag" : "")} erkennt auch ffmpeg keinen Ton"
            : "Auch ffmpeg erkennt keinen Ton";
        return $"{where}; die Datei beginnt mit {Preview(head)}.";
    }

    private const int SampleLength = 1 << 16;

    private static readonly string[] HtmlMarkers = ["<html", "<!doctype", "<body", "<img", "<a href", "<p>", "<br", "<table", "<div", "&nbsp;"];

    /// <summary>Fast nur druckbare Zeichen und Zeilenumbrüche (Latin-1 und UTF-8 zählen mit).</summary>
    private static bool IsText(byte[] sample) =>
        sample.Length > 0 && sample.Count(b => b is 9 or 10 or 13 or (>= 0x20 and < 0x7F) or >= 0xA0) >= sample.Length * 0.95;

    /// <summary>Shannon-Entropie in Bit je Byte; ungeordnete oder verschlüsselte Daten liegen nahe 8.</summary>
    private static double Entropy(byte[] sample)
    {
        var counts = new int[256];
        foreach (var b in sample)
            counts[b]++;
        return -counts.Where(c => c > 0).Sum(c => (double)c / sample.Length * Math.Log2((double)c / sample.Length));
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
