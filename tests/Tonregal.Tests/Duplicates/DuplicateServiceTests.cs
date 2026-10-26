// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Duplicates;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;
using FileRow = Tonregal.Infrastructure.Duplicates.DuplicateService.FileRow;

namespace Tonregal.Tests.Duplicates;

public sealed class DuplicateServiceTests : IDisposable
{
    private const string Original = "Testband/2024 - Testalbum/01 - Erster Song.mp3";
    private const string Copy = "Testband/Kopien/01 - Erster Song.mp3";

    private readonly TestLibrary _library = new();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TonregalOptions Options => new() { MusicRoot = _library.MusicRoot };

    public DuplicateServiceTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.Migrate();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _library.Dispose();
    }

    private TonregalDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TonregalDbContext>().UseSqlite(_connection).Options);

    private DuplicateService Service(TonregalDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System, NullLogger<DuplicateService>.Instance);

    private async Task ScanAsync()
    {
        await using var db = CreateContext();
        await new LibraryScanner(
                db, new TagLibTagReader(), Microsoft.Extensions.Options.Options.Create(Options),
                TimeProvider.System, NullLogger<LibraryScanner>.Instance)
            .ScanAsync(null, Ct);
    }

    private async Task<List<DuplicateGroup>> DetectAsync()
    {
        await using (var db = CreateContext())
            await Service(db).DetectAsync(Ct);
        await using var read = CreateContext();
        return await read.DuplicateGroups.Include(g => g.Files).AsNoTracking().ToListAsync(Ct);
    }

    private static FileRow Row(
        int id, string title = "Song", string artist = "Band", int durationMs = 200_000, string? hash = null,
        AudioFormat format = AudioFormat.Mp3, int bitrate = 192, int trackId = 0, Guid? recording = null, Guid? acoustId = null) =>
        new(id, trackId == 0 ? id : trackId, $"{id}.mp3", hash ?? $"hash{id}", format, bitrate, durationMs, title, [artist], recording, acoustId);

    [Fact]
    public void Gruppiert_nach_Hash_Aufnahme_und_Track_Key()
    {
        var recording = Guid.NewGuid();
        var acoustId = Guid.NewGuid();
        var candidates = DuplicateService.Find([
            Row(1, hash: "gleich"), Row(2, title: "Anderes", hash: "gleich"),
            Row(3, title: "X", recording: recording), Row(4, title: "Y", recording: recording),
            Row(5, title: "Z", acoustId: acoustId), Row(6, title: "Z2", acoustId: acoustId),
            Row(7, title: "Hello", artist: "The Band", durationMs: 180_000), Row(8, title: "hello", artist: "Band", durationMs: 181_500),
            Row(9, title: "Hello", artist: "Band", durationMs: 190_000),
            Row(10, title: "Track 1", artist: LibraryScanner.UnknownArtist), Row(11, title: "Track 1", artist: LibraryScanner.UnknownArtist),
        ]);

        Assert.Equal(
            ["1,2:ExactHash", "3,4:Fingerprint", "5,6:Fingerprint", "7,8:Metadata"],
            candidates.Select(c => $"{string.Join(",", c.Files.Select(f => f.Id).Order())}:{c.Kind}").Order());
    }

    [Fact]
    public void MP3_und_FLAC_am_selben_Track_sind_keine_Dublette()
    {
        Assert.Empty(DuplicateService.Find([
            Row(1, trackId: 5, format: AudioFormat.Mp3), Row(2, trackId: 5, format: AudioFormat.Flac),
        ]));
        Assert.Single(DuplicateService.Find([
            Row(1, trackId: 5, format: AudioFormat.Mp3), Row(2, trackId: 5, format: AudioFormat.Mp3),
        ]));
    }

    [Fact]
    public void Vorschlag_behaelt_verlustfrei_dann_hoehere_Bitrate()
    {
        Assert.Equal(2, DuplicateService.Suggest([Row(1, bitrate: 320), Row(2, format: AudioFormat.Flac, bitrate: 900), Row(3, bitrate: 128)]).Id);
        Assert.Equal(1, DuplicateService.Suggest([Row(1, bitrate: 320), Row(3, bitrate: 128)]).Id);
    }

    [Fact]
    public async Task Identische_Kopie_wird_gefunden_und_landet_im_Papierkorb()
    {
        _library.Copy("song1.mp3", Path.Combine(_library.Library, "Testband", "Kopien"), "01 - Erster Song.mp3");
        _library.Copy("cover.jpg", Path.Combine(_library.Library, "Testband", "Kopien"), "cover.jpg");
        await ScanAsync();

        var groups = await DetectAsync();
        var group = Assert.Single(groups);
        Assert.Equal(DuplicateKind.ExactHash, group.Kind);
        Assert.Equal([Original, Copy], group.Files.Select(f => f.Path).Order());
        var original = group.Files.Single(f => f.Path == Original);
        Assert.Equal(original.Id, group.KeeperFileId);
        Assert.Equal(group.Id, Assert.Single(await DetectAsync()).Id);

        await using (var db = CreateContext())
            await Service(db).ResolveAsync(group.Id, [original.Id], Ct);

        var trash = Path.Combine(Options.Trash, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));
        Assert.True(File.Exists(Path.Combine(trash, "Testband", "Kopien", "01 - Erster Song.mp3")));
        Assert.True(File.Exists(Path.Combine(trash, "Testband", "Kopien", "cover.jpg")));
        Assert.False(Directory.Exists(Path.Combine(_library.Library, "Testband", "Kopien")));
        Assert.True(File.Exists(Path.Combine(_library.Library, Original)));

        await using var check = CreateContext();
        Assert.False(await check.AudioFiles.AnyAsync(f => f.Path == Copy, Ct));
        var resolved = await check.DuplicateGroups.SingleAsync(Ct);
        Assert.Equal((DuplicateDecision.Resolved, original.Id), (resolved.Decision, resolved.KeeperFileId));
        Assert.NotNull(resolved.DecidedAt);
        Assert.DoesNotContain(await DetectAsync(), g => g.Decision == DuplicateDecision.Open);
    }

    [Fact]
    public async Task Alle_behalten_wird_gemerkt()
    {
        _library.Copy("song1.mp3", Path.Combine(_library.Library, "Testband", "Kopien"), "01 - Erster Song.mp3");
        await ScanAsync();
        var group = Assert.Single(await DetectAsync());

        await using (var db = CreateContext())
            await Service(db).ResolveAsync(group.Id, group.Files.Select(f => f.Id).ToList(), Ct);

        Assert.True(File.Exists(Path.Combine(_library.Library, Copy)));
        var groups = await DetectAsync();
        Assert.Equal(DuplicateDecision.Resolved, Assert.Single(groups).Decision);
    }

    [Fact]
    public async Task Ungueltige_Auswahl_und_doppelte_Entscheidung_werden_abgelehnt()
    {
        _library.Copy("song1.mp3", Path.Combine(_library.Library, "Testband", "Kopien"), "01 - Erster Song.mp3");
        await ScanAsync();
        var group = Assert.Single(await DetectAsync());

        await using var db = CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).ResolveAsync(group.Id, [], Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).ResolveAsync(group.Id, [999], Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service(db).ResolveAsync(999, [1], Ct));
        await Service(db).ResolveAsync(group.Id, [group.Files[0].Id], Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).ResolveAsync(group.Id, [group.Files[0].Id], Ct));
    }

    [Fact]
    public void Papierkorb_loescht_nur_Tagesordner_aelter_als_30_Tage()
    {
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var trash = Options.Trash;
        Directory.CreateDirectory(Path.Combine(trash, "2026-09-24", "a"));
        Directory.CreateDirectory(Path.Combine(trash, "2026-09-26"));
        Directory.CreateDirectory(Path.Combine(trash, "kein-datum"));

        Assert.Equal(1, LibraryTrash.Purge(trash, now));
        Assert.Equal(["2026-09-26", "kein-datum"], Directory.GetDirectories(trash).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Papierkorb_ueberschreibt_nichts()
    {
        var now = DateTimeOffset.UtcNow;
        var a = _library.Copy("song1.mp3", _library.MusicRoot, "a.mp3");
        var b = _library.Copy("song1.mp3", _library.MusicRoot, "b.mp3");

        var first = LibraryTrash.Move(Options.Trash, a, "x/song.mp3", now);
        var second = LibraryTrash.Move(Options.Trash, b, "x/song.mp3", now);

        Assert.EndsWith("song.mp3", first);
        Assert.EndsWith("song (2).mp3", second);
    }
}
