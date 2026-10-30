// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Tonregal.Application.Abstractions;

namespace Tonregal.Infrastructure.Lyrics;

/// <summary>
/// Liest LRC (<c>[mm:ss.xx]Zeile</c>, auch mehrere Zeitmarken je Zeile, <c>[offset:…]</c> und Wortmarken
/// <c>&lt;mm:ss.xx&gt;</c>) und reinen Text. Ein Text gilt als synchronisiert, sobald eine Zeile eine Zeitmarke hat.
/// </summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"^\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[([a-zA-Z#]+):(.*)\]\s*$")]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTag();

    public static (bool Synced, IReadOnlyList<LyricLine> Lines) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (false, []);
        var rows = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var offset = 0;
        var timed = new List<LyricLine>();
        foreach (var row in rows)
        {
            var rest = row.Trim();
            var times = new List<int>();
            while (TimeTag().Match(rest) is { Success: true } m)
            {
                times.Add(Milliseconds(m));
                rest = rest[m.Length..];
            }

            if (times.Count == 0)
            {
                if (MetaTag().Match(rest) is { Success: true } meta
                    && meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(meta.Groups[2].Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var o))
                    offset = o;
                continue;
            }

            var line = WordTag().Replace(rest, "").Trim();
            timed.AddRange(times.Select(t => new LyricLine(t, line)));
        }

        if (timed.Count > 0)
        {
            // Ein positiver Offset zeigt den Text früher an.
            var lines = timed
                .Select(l => l with { TimeMs = Math.Max(0, l.TimeMs!.Value - offset) })
                .OrderBy(l => l.TimeMs)
                .ToList();
            return (true, lines);
        }

        return (false, Plain(rows));
    }

    /// <summary>Zeilen ohne Metadaten, Leerzeilen am Rand weg, mehrere Leerzeilen zu einer.</summary>
    private static List<LyricLine> Plain(string[] rows)
    {
        var lines = new List<LyricLine>();
        foreach (var row in rows)
        {
            var line = row.Trim();
            if (MetaTag().IsMatch(line))
                continue;
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Text.Length == 0))
                continue;
            lines.Add(new LyricLine(null, line));
        }

        while (lines.Count > 0 && lines[^1].Text.Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static int Milliseconds(Match m)
    {
        var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var fraction = m.Groups[3].Success ? m.Groups[3].Value : "0";
        // ".5" sind 500 ms, ".05" 50 ms, ".005" 5 ms.
        var ms = int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        return (minutes * 60 + seconds) * 1000 + ms;
    }
}
