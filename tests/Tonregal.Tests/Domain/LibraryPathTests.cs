// SPDX-License-Identifier: AGPL-3.0-or-later
using Tonregal.Domain.Entities;
using Tonregal.Domain.ValueObjects;

namespace Tonregal.Tests.Domain;

public class LibraryPathTests
{
    [Fact]
    public void Folgt_der_Ordnerregel()
    {
        var path = LibraryPath.For("Radiohead", 1997, "OK Computer", AlbumKind.Album, 1, 1, 3, "Subterranean Homesick Alien", ".FLAC");

        Assert.Equal(Path.Combine("Radiohead", "1997 - OK Computer", "03 - Subterranean Homesick Alien.flac"), path);
    }

    [Fact]
    public void Mehrere_Discs_bekommen_Disc_Praefix()
    {
        Assert.Equal("2-07 - Title.mp3", LibraryPath.FileName(2, 2, 7, "Title", "mp3"));
    }

    [Fact]
    public void Compilations_landen_unter_Various_Artists()
    {
        var folder = LibraryPath.AlbumFolder("Irgendwer", 2001, "Sampler", AlbumKind.Compilation);

        Assert.Equal(Path.Combine("Various Artists", "2001 - Sampler"), folder);
    }

    [Fact]
    public void Ohne_Jahr_nur_Albumtitel()
    {
        Assert.Equal(Path.Combine("A", "B"), LibraryPath.AlbumFolder("A", null, "B", AlbumKind.Album));
    }

    [Theory]
    [InlineData("AC/DC", "AC_DC")]
    [InlineData("What? Why: Because", "What_ Why_ Because")]
    [InlineData("...Baby One More Time", "Baby One More Time")]
    [InlineData("Trailing.", "Trailing")]
    [InlineData("  ", "_")]
    [InlineData("Sigur Rós", "Sigur Rós")]
    public void Sanitize_ersetzt_ungueltige_Zeichen(string input, string expected)
    {
        Assert.Equal(expected, LibraryPath.Sanitize(input));
    }

    [Fact]
    public void Sanitize_kuerzt_lange_Segmente_auf_Zeichengrenzen()
    {
        var result = LibraryPath.Sanitize(new string('ä', 150));

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result) <= 200);
        Assert.Equal(100, result.Length);
    }
}
