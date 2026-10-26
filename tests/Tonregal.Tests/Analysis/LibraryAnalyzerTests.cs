// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Infrastructure.Analysis;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Tests.Analysis;

public sealed class LibraryAnalyzerTests : IDisposable
{
    private readonly TestLibrary _library = new();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TonregalDbContext _db;
    private readonly TagLibTagReader _reader = new();
    private readonly FakeBpmKey _bpmKey = new();
    private readonly FakeReplayGain _replayGain = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOptions<TonregalOptions> Options => Microsoft.Extensions.Options.Options.Create(new TonregalOptions { MusicRoot = _library.MusicRoot });

    public LibraryAnalyzerTests()
    {
        _connection.Open();
        _db = new TonregalDbContext(new DbContextOptionsBuilder<TonregalDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _library.Dispose();
    }

    private LibraryAnalyzer Analyzer() => new(
        _db, _bpmKey, _replayGain, _reader, new TagLibTagWriter(), new LibraryWriteLock(), Options, TimeProvider.System, NullLogger<LibraryAnalyzer>.Instance);

    private Task<ScanResult> ScanAsync() =>
        new LibraryScanner(_db, _reader, Options, TimeProvider.System, NullLogger<LibraryScanner>.Instance).ScanAsync(null, Ct);

    private async Task<List<AlbumAnalysis>> AnalyzeAllAsync()
    {
        var albums = new List<AlbumAnalysis>();
        while (await Analyzer().AnalyzeNextAlbumAsync(Ct) is { } album)
            albums.Add(album);
        return albums;
    }

    [Fact]
    public async Task Schreibt_fehlende_BPM_und_Tonart_und_ReplayGain_albumweise()
    {
        await ScanAsync();

        var albums = await AnalyzeAllAsync();

        Assert.Equal(["Testband – Testalbum", "Various Artists – Sampler"], albums.Select(a => a.Album).Order());
        Assert.Equal("", string.Join(" | ", albums.SelectMany(a => a.Errors)));
        // Album-Gain: alle Dateien eines Albums in einem Aufruf.
        Assert.Contains(_replayGain.Calls, call => call.Count == 3);

        var mp3 = _reader.Read(Path.Combine(_library.AlbumFolder, "01 - Erster Song.mp3")).Tags;
        Assert.Equal((120d, "Am"), (mp3.Bpm, mp3.InitialKey)); // vorhandene Werte bleiben
        var flac = _reader.Read(Path.Combine(_library.AlbumFolder, "02 - Zweiter Song.flac")).Tags;
        Assert.Equal((128d, "F#m", -3.5), (flac.Bpm, flac.InitialKey, flac.ReplayGainTrackGain));

        const string FlacPath = "Testband/2024 - Testalbum/02 - Zweiter Song.flac";
        var track = await _db.AudioFiles.Where(f => f.Path == FlacPath).Select(f => f.Track).SingleAsync(Ct);
        Assert.Equal((128d, "F#m", -3.5), (track.Bpm, track.InitialKey, track.ReplayGainTrack));
        var results = await _db.AnalysisResults.Where(r => r.AudioFile.Path == FlacPath).OrderBy(r => r.Tool).ToListAsync(Ct);
        Assert.Equal(["librosa", "rsgain"], results.Select(r => r.Tool));
        Assert.Equal((128.4, "F#m", 0.81), (results[0].Bpm, results[0].Key, results[0].KeyConfidence));
        Assert.Equal(-14.5, results[1].Loudness);

        Assert.Equal(new AnalysisCounts(4, 4, 0, 0), await Analyzer().CountAsync(Ct));
        // Hash und Änderungszeit sind nachgezogen: der nächste Scan sieht nichts Neues.
        Assert.Equal(4, (await ScanAsync()).Unchanged);
    }

    [Fact]
    public async Task Defekte_Datei_haelt_den_Lauf_nicht_auf()
    {
        await ScanAsync();
        _bpmKey.Broken = "Zweiter Song";

        var albums = await AnalyzeAllAsync();

        var error = Assert.Single(albums.SelectMany(a => a.Errors));
        Assert.Contains("02 - Zweiter Song.flac", error);
        Assert.Equal(new AnalysisCounts(4, 4, 0, 1), await Analyzer().CountAsync(Ct));
        Assert.Null(_reader.Read(Path.Combine(_library.AlbumFolder, "02 - Zweiter Song.flac")).Tags.Bpm);
    }

    [Fact]
    public async Task Fehlendes_Werkzeug_bricht_ab_ohne_etwas_zu_speichern()
    {
        await ScanAsync();
        _bpmKey.Unavailable = true;

        await Assert.ThrowsAsync<ExternalToolUnavailableException>(() => Analyzer().AnalyzeNextAlbumAsync(Ct));

        Assert.Equal(0, await _db.AnalysisResults.CountAsync(Ct));
    }

    private sealed class FakeBpmKey : IBpmKeyAnalyzer
    {
        public string? Broken { get; set; }
        public bool Unavailable { get; set; }

        public Task<BpmKeyResult> AnalyzeAsync(string path, CancellationToken ct)
        {
            if (Unavailable)
                throw new ExternalToolUnavailableException("python fehlt");
            if (Broken is not null && path.Contains(Broken))
                throw new InvalidDataException("kaputte Frames");
            return Task.FromResult(new BpmKeyResult(128.4, "F#m", 0.81, "0.11.0"));
        }
    }

    /// <summary>Schreibt einen festen Track-Gain, wie es rsgain täte.</summary>
    private sealed class FakeReplayGain : IReplayGainTagger
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<string> TagAlbumAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            Calls.Add(paths);
            foreach (var path in paths)
            {
                using var file = TagLib.File.Create(path);
                if (file is TagLib.Mpeg.AudioFile)
                    file.GetTag(TagLib.TagTypes.Id3v2, true);
                file.Tag.ReplayGainTrackGain = -3.5;
                file.Save();
            }

            return Task.FromResult("3.5.1");
        }
    }
}
