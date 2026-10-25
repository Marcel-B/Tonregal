// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Fingerprinting;
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Tests.Metadata;

/// <summary>Braucht fpcalc im PATH (CI: Paket libchromaprint-tools, macOS: brew install chromaprint).</summary>
public class FpcalcFingerprintServiceTests
{
    private static FpcalcFingerprintService Create(string path = "fpcalc") =>
        new(Options.Create(new MetadataOptions { FpcalcPath = path }), new TagLibTagReader());

    [Fact]
    public async Task Berechnet_Fingerprint_und_Dauer()
    {
        var fingerprint = await Create().ComputeAsync(Path.Combine(TestLibrary.TestData, "song2.flac"), TestContext.Current.CancellationToken);

        Assert.StartsWith("AQ", fingerprint.Value);
        Assert.InRange(fingerprint.Duration.TotalSeconds, 2.5, 3.5);
    }

    [Fact]
    public async Task Version_wird_gemeldet()
    {
        Assert.Contains("fpcalc", await Create().GetVersionAsync(TestContext.Current.CancellationToken));
        Assert.Null(await Create("/gibt/es/nicht/fpcalc").GetVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fehlendes_fpcalc_meldet_ExternalToolUnavailable()
    {
        await Assert.ThrowsAsync<ExternalToolUnavailableException>(() =>
            Create("/gibt/es/nicht/fpcalc").ComputeAsync("x.mp3", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unlesbare_Datei_meldet_InvalidData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3");
        File.WriteAllText(path, "kein mp3");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => Create().ComputeAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Ohne_ID3_Tag_über_stdin_mit_Dauer_aus_den_Tags()
    {
        // Der Fallback für MP3-in-WAV mit ID3-Tag, den neuere FFmpeg-Versionen nicht lesen.
        var path = Path.Combine(TestLibrary.TestData, "song1.mp3");
        var offset = FpcalcFingerprintService.Id3TagLength(path);
        Assert.NotNull(offset);

        var direct = await Create().ComputeAsync(path, TestContext.Current.CancellationToken);
        var fallback = await Create().ComputeFromOffsetAsync(path, offset.Value, TestContext.Current.CancellationToken);

        Assert.Equal(direct.Value, fallback.Value);
        Assert.InRange(fallback.Duration.TotalSeconds, 2.5, 3.5);
    }

    [Fact]
    public async Task WAV_Hülle_hinter_fremden_Bytes_wird_ab_den_Audiodaten_gelesen()
    {
        // So sahen die Dateien aus, bei denen schon das Weglassen des ID3-Tags nicht reichte.
        var riff = await File.ReadAllBytesAsync(Path.Combine(TestLibrary.TestData, "song5-riff.mp3"), TestContext.Current.CancellationToken);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3");
        await File.WriteAllBytesAsync(path, [.. Enumerable.Range(0, 500).Select(i => (byte)(i * 7 % 251)), .. riff], TestContext.Current.CancellationToken);
        try
        {
            var expected = await Create().ComputeAsync(Path.Combine(TestLibrary.TestData, "song5-riff.mp3"), TestContext.Current.CancellationToken);
            var fingerprint = await Create().ComputeAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(expected.Value, fingerprint.Value);
            Assert.InRange(fingerprint.Duration.TotalSeconds, 2.5, 3.5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Findet_Datenblock_der_WAV_Hülle_und_ersten_MP3_Frame()
    {
        var path = Path.Combine(TestLibrary.TestData, "song5-riff.mp3");
        var bytes = File.ReadAllBytes(path);
        var id3 = (int)FpcalcFingerprintService.Id3TagLength(path)!.Value;

        var data = FpcalcFingerprintService.RiffDataOffset(bytes, id3);
        var frame = FpcalcFingerprintService.FirstMpegFrame(bytes, id3);

        Assert.NotNull(data);
        var dataStart = (int)data.Value;
        Assert.Equal("data"u8.ToArray(), bytes[(dataStart - 8)..(dataStart - 4)]);
        Assert.NotNull(frame);
        Assert.True(frame >= data);
        Assert.NotNull(FpcalcFingerprintService.MpegFrameLength(bytes.AsSpan((int)frame.Value)));
        Assert.Equal(data, frame);
        Assert.Equal([id3, data.Value], FpcalcFingerprintService.AudioDataOffsets(path));
    }

    [Fact]
    public void ID3_Länge_nur_bei_ID3_Header()
    {
        Assert.NotNull(FpcalcFingerprintService.Id3TagLength(Path.Combine(TestLibrary.TestData, "song1.mp3")));
        Assert.Null(FpcalcFingerprintService.Id3TagLength(Path.Combine(TestLibrary.TestData, "song2.flac")));
    }

    [Fact]
    public void Parst_fpcalc_Json()
    {
        var fingerprint = FpcalcFingerprintService.Parse("""{"duration": 243.17, "fingerprint": "AQADtEmUaEkS"}""");

        Assert.Equal("AQADtEmUaEkS", fingerprint.Value);
        Assert.Equal(243.17, fingerprint.Duration.TotalSeconds, 2);
    }
}
