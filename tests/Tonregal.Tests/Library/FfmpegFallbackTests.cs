// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Fingerprinting;
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Tests.Library;

/// <summary>
/// Dateien, an denen TagLib scheitert, wie sie im Altbestand vorkommen. Braucht ffmpeg und ffprobe
/// im PATH (CI: Paket ffmpeg, macOS: brew install ffmpeg).
/// </summary>
public sealed class FfmpegFallbackTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("tonregal-ffmpeg-").FullName;

    public static FfmpegAudio Ffmpeg { get; } = new(Options.Create(new MetadataOptions()));

    private readonly TagLibTagReader _reader = new(Ffmpeg);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>MPEG-Frames von song1.mp3 ohne dessen ID3-Tag.</summary>
    internal static byte[] Frames()
    {
        var path = Path.Combine(TestLibrary.TestData, "song1.mp3");
        var bytes = File.ReadAllBytes(path);
        return bytes[(int)(FpcalcFingerprintService.Id3TagLength(path) ?? 0)..];
    }

    /// <summary>Ein ID3v2.3-Tag aus den gegebenen Frames.</summary>
    internal static byte[] Id3(params byte[][] frames)
    {
        var body = frames.SelectMany(f => f).ToArray();
        var size = body.Length;
        byte[] header = [(byte)'I', (byte)'D', (byte)'3', 3, 0, 0,
            (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F)];
        return [.. header, .. body];
    }

    internal static byte[] Frame(string id, byte[] data) =>
        [.. Encoding.ASCII.GetBytes(id), (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, 0, 0, .. data];

    internal static byte[] Text(string id, string value) => Frame(id, [0, .. Encoding.Latin1.GetBytes(value)]);

    /// <summary>Wie „Joan Baez - Where have all the flowers gone.mp3“: ein Bild-Frame mit 3 Bytes.</summary>
    internal static byte[] BrokenPicture() =>
        [.. Id3(Text("TIT2", "Where have all the flowers gone"), Text("TPE1", "Joan Baez"), Frame("APIC", [0, 0, 0])), .. Frames()];

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Kaputter_Bild_Frame_wird_mit_ffprobe_gelesen()
    {
        var path = Write("Joan Baez.mp3", BrokenPicture());
        Assert.False(TagLibTagReader.CanOpen(path));

        var (tags, properties) = _reader.Read(path);

        Assert.Equal("Where have all the flowers gone", tags.Title);
        Assert.Equal(["Joan Baez"], tags.Artists);
        Assert.Equal(AudioFormat.Mp3, properties.Format);
        Assert.InRange(properties.Duration.TotalSeconds, 2.5, 3.5);
        Assert.Null(_reader.ReadCover(path));
        Assert.Null(_reader.ReadLyrics(path));
    }

    [Fact]
    public void Nullbytes_vor_dem_Ton_werden_uebersprungen()
    {
        // TagLib sucht nur 16 KB hinter dem Tag nach dem ersten Frame: „MPEG audio header not found“.
        var path = Write("Lücke.mp3", [.. Id3(Text("TIT2", "Jolene")), .. new byte[40_000], .. Frames()]);

        var (tags, properties) = _reader.Read(path);

        Assert.Equal("Jolene", tags.Title);
        Assert.Equal(AudioFormat.Mp3, properties.Format);
        Assert.True(properties.Duration > TimeSpan.Zero);
    }

    [Fact]
    public void MP3_mit_Endung_wav_wird_als_MP3_erkannt()
    {
        var path = Path.Combine(_folder, "zarathustra2.wav");
        File.Copy(Path.Combine(TestLibrary.TestData, "song1.mp3"), path);

        var (tags, properties) = _reader.Read(path);

        Assert.Equal("Erster Song", tags.Title);
        Assert.Equal(AudioFormat.Mp3, properties.Format);
    }

    [Fact]
    public void Nur_Nullbytes_sagt_die_Meldung()
    {
        var path = Write("Leer.mp3", new byte[300_000]);

        var e = Assert.Throws<InvalidDataException>(() => _reader.Read(path));

        Assert.StartsWith("Leer.mp3: ", e.Message);
        Assert.Contains("besteht nur aus Nullbytes (293 KB)", e.Message);
    }

    [Fact]
    public void Nullbytes_hinter_dem_Tag_sagt_die_Meldung()
    {
        var path = Write("Tag.mp3", [.. Id3(Text("TIT2", "Angel")), .. new byte[100_000]]);

        var e = Assert.Throws<InvalidDataException>(() => _reader.Read(path));

        Assert.Contains("Hinter dem ID3-Tag", e.Message);
        Assert.Contains("nur Nullbytes", e.Message);
    }

    [Fact]
    public void Webseite_statt_Ton_sagt_die_Meldung()
    {
        var path = Write("Download.mp3", Encoding.ASCII.GetBytes("<!DOCTYPE html><html><body>" + new string('x', 20_000) + "</body></html>"));

        var e = Assert.Throws<InvalidDataException>(() => _reader.Read(path));

        Assert.Contains("Webseite (HTML)", e.Message);
    }

    [Fact]
    public void Ein_Stueck_Webseite_mitten_im_Text_wird_erkannt()
    {
        // Wie zarathustra2.wav: beginnt mitten in fremdem HTML.
        var html = string.Concat(Enumerable.Repeat("alz.gif\" NOSAVE height=20 width=23 align=ABSBOTTOM>und&nbsp;<img SRC=\"images/Sulfur.gif\"> ", 400));
        var path = Write("zarathustra2.wav", Encoding.ASCII.GetBytes(html));

        var e = Assert.Throws<InvalidDataException>(() => _reader.Read(path));

        Assert.Contains("Webseite (HTML)", e.Message);
    }

    [Fact]
    public void Ein_Stueck_PDF_wird_erkannt()
    {
        // Wie „Body Count's In The House.mp3“: Objekte einer PDF zwischen komprimierten Strömen.
        var noise = new byte[3000];
        new Random(3).NextBytes(noise);
        var part = (byte[])[.. Encoding.ASCII.GetBytes("\n<< /Length 4135 /Filter /FlateDecode >>\nstream\n"), .. noise,
            .. Encoding.ASCII.GetBytes("\nendstream\nendobj\n12 0 obj\n<< /Type /Page /Parent 3 0 R >>\nendobj\n")];
        var path = Write("Body Count.mp3", [.. Enumerable.Repeat(part, 20).SelectMany(p => p)]);

        var e = Assert.Throws<InvalidDataException>(() => _reader.Read(path));

        Assert.Contains("Stück einer PDF-Datei", e.Message);
    }

    [Fact]
    public void Grossgeschriebenes_HTML_am_Anfang_ist_eine_Webseite()
    {
        var noise = new byte[50_000];
        new Random(5).NextBytes(noise);
        var path = Write("Celine Dion.mp3", [.. Encoding.ASCII.GetBytes("<HTML>\r\n<HEAD>\r\n"), .. noise]);

        var e = Assert.Throws<InvalidDataException>(() => _reader.Read(path));

        Assert.Contains("Webseite (HTML)", e.Message);
    }

    [Fact]
    public void Meldung_von_TagLib_und_Zusatz_sind_durch_einen_Punkt_getrennt()
    {
        var path = Write("Virtual Booster.wav", Encoding.ASCII.GetBytes(new string('x', 10_000)));

        var e = Assert.Throws<InvalidDataException>(() => _reader.Read(path));

        Assert.Contains("RIFF identifier. Die Datei", e.Message);
    }

    [Fact]
    public void Ungeordnete_Daten_ohne_Audio_Kopf_werden_benannt()
    {
        // Wie Jolene.mp3: vom ersten Byte an Zufall, kein ID3, kein Frame.
        var noise = new byte[200_000];
        new Random(7).NextBytes(noise);
        for (var i = 0; i < noise.Length; i++)
            if (noise[i] == 0xFF)
                noise[i] = 0xFE;
        var path = Write("Jolene.mp3", noise);

        var e = Assert.Throws<InvalidDataException>(() => _reader.Read(path));

        Assert.Contains("fremde Daten ohne einen einzigen Audio-Kopf", e.Message);
    }

    [Fact]
    public void Ohne_ffprobe_bleibt_die_Meldung_von_TagLib()
    {
        var reader = new TagLibTagReader(new FfmpegAudio(Options.Create(new MetadataOptions { FfprobePath = "/gibt/es/nicht/ffprobe" })));
        var path = Write("Joan Baez.mp3", BrokenPicture());

        var e = Assert.Throws<InvalidDataException>(() => reader.Read(path));

        Assert.Contains("picture frame", e.Message);
    }

    [Fact]
    public async Task Neu_verpackt_oeffnet_TagLib_die_Datei_und_die_Tags_bleiben()
    {
        var path = Write("Joan Baez.mp3", BrokenPicture());
        var target = Path.Combine(_folder, "sauber.mp3");

        await Ffmpeg.RemuxAsync(path, target, TestContext.Current.CancellationToken);

        Assert.True(TagLibTagReader.CanOpen(target));
        var (tags, properties) = new TagLibTagReader().Read(target);
        Assert.Equal("Where have all the flowers gone", tags.Title);
        Assert.Equal(["Joan Baez"], tags.Artists);
        // Die Länge nicht genau: TagLib liest den Xing-Header von Mono-MP3s falsch, auch von LAME.
        Assert.True(properties.Duration > TimeSpan.Zero);
    }

    [Theory]
    [InlineData("mp3", ".mp3", AudioFormat.Mp3)]
    [InlineData("pcm_s16le", ".wav", AudioFormat.Wav)]
    [InlineData("alac", ".m4a", AudioFormat.Alac)]
    [InlineData("wmav2", null, AudioFormat.Unknown)]
    public void Endung_und_Format_folgen_dem_Inhalt(string codec, string? extension, AudioFormat format)
    {
        var probed = new ProbedAudio("x", codec, TimeSpan.Zero, 0, 0, 0, new Dictionary<string, string>());

        Assert.Equal((extension, format), (probed.Extension, probed.Format));
    }

    [Fact]
    public void Fremdes_Format_wird_benannt()
    {
        var json = """{"streams":[{"codec_name":"wmav2","sample_rate":"44100","channels":2,"duration":"3.5"}],"format":{"format_name":"asf","tags":{"Title":"Again"}}}""";

        var probed = FfmpegAudio.Parse(json)!;

        Assert.Equal("WMA", probed.Description);
        Assert.Equal("Again", probed.Tags["title"]);
        Assert.Equal(3.5, probed.Duration.TotalSeconds);
    }
}
