// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Lyrics;

namespace Tonregal.Tests.Lyrics;

public sealed class LrcParserTests
{
    [Fact]
    public void Liest_Zeitmarken_und_sortiert()
    {
        var (synced, lines) = LrcParser.Parse("[ar:Testband]\n[ti:Song]\n[00:12.34]Erste Zeile\r\n[01:02.5]Zweite\n[00:05]Intro\n[02:00.00]\n");

        Assert.True(synced);
        Assert.Equal(
            [new LyricLine(5000, "Intro"), new LyricLine(12340, "Erste Zeile"), new LyricLine(62500, "Zweite"), new LyricLine(120000, "")],
            lines);
    }

    [Fact]
    public void Mehrere_Zeitmarken_Offset_und_Wortmarken()
    {
        var (_, lines) = LrcParser.Parse("[offset:+500]\n[00:10.00][00:30.00]Refrain <00:10.50>mit <00:11.00>Wörtern\n");

        Assert.Equal([new LyricLine(9500, "Refrain mit Wörtern"), new LyricLine(29500, "Refrain mit Wörtern")], lines);
    }

    [Fact]
    public void Reiner_Text_bleibt_ohne_Zeiten()
    {
        var (synced, lines) = LrcParser.Parse("\n[Chorus]\nZeile eins\n\n\n\nZeile zwei\n\n");

        Assert.False(synced);
        Assert.Equal(["[Chorus]", "Zeile eins", "", "Zeile zwei"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Null(l.TimeMs));
    }

    [Fact]
    public void Leer_ergibt_nichts() => Assert.Empty(LrcParser.Parse("  \n").Lines);

    [Fact]
    public void Bester_Treffer_bevorzugt_synchronisiert_mit_passender_Laenge()
    {
        LrcLibTrack Hit(long id, double duration, string? synced, string? plain = "Text") =>
            new(id, "Song", "Band", "Album", duration, false, plain, synced);

        var best = LrcLibClient.Best([Hit(1, 200, null), Hit(2, 260, "[00:01.00]x"), Hit(3, 202, "[00:01.00]y"), Hit(4, 199, "[00:01.00]z")], 200);

        Assert.Equal(4, best?.Id);
        Assert.Null(LrcLibClient.Best([Hit(5, 300, "[00:01.00]x")], 200));
    }
}
