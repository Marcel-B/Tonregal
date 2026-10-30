// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonregal.Application;
using Tonregal.Application.Abstractions;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Tests.Library;

public sealed class LibraryScannerTests : IDisposable
{
    private readonly TestLibrary _library = new();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public LibraryScannerTests()
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

    private async Task<ScanResult> ScanAsync()
    {
        await using var db = CreateContext();
        var scanner = new LibraryScanner(
            db, new TagLibTagReader(), Options.Create(new TonregalOptions { MusicRoot = _library.MusicRoot }),
            TimeProvider.System, NullLogger<LibraryScanner>.Instance);
        return await scanner.ScanAsync(null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Erster_Scan_baut_den_Index_auf()
    {
        var result = await ScanAsync();

        Assert.Equal(4, result.Added);
        Assert.Empty(result.Errors);

        await using var db = CreateContext();
        var album = await db.Albums.Include(a => a.AlbumArtist).Include(a => a.Tracks).ThenInclude(t => t.Artists)
            .SingleAsync(a => a.Title == "Testalbum", TestContext.Current.CancellationToken);
        Assert.Equal("Testband", album.AlbumArtist.Name);
        Assert.Equal(2024, album.Year);
        Assert.Equal("Testband/2024 - Testalbum/cover.jpg", album.CoverPath);
        Assert.Equal([1, 2, 3], album.Tracks.Select(t => t.Number).Order());
        Assert.Equal(["Gast", "Testband"], album.Tracks.Single(t => t.Number == 2).Artists.Select(a => a.Name).Order());

        var sampler = await db.Albums.Include(a => a.AlbumArtist).SingleAsync(a => a.Title == "Sampler", TestContext.Current.CancellationToken);
        Assert.Equal(AlbumKind.Compilation, sampler.Kind);
        Assert.Equal(Artist.VariousArtists, sampler.AlbumArtist.Name);

        var file = await db.AudioFiles.SingleAsync(f => f.Path.EndsWith(".flac"), TestContext.Current.CancellationToken);
        Assert.Equal(64, file.FileHash.Length);
        Assert.Equal(AudioFormat.Flac, file.Format);
    }

    [Fact]
    public async Task Zweiter_Scan_ueberspringt_unveraenderte_Dateien()
    {
        await ScanAsync();
        var result = await ScanAsync();

        Assert.Equal(0, result.Added);
        Assert.Equal(4, result.Unchanged);

        await using var db = CreateContext();
        Assert.Equal(4, await db.Tracks.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Artists.CountAsync(a => a.Name == "Testband", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Geloeschte_Dateien_verschwinden_samt_leerem_Album()
    {
        await ScanAsync();
        Directory.Delete(Path.Combine(_library.Library, "Various Artists"), recursive: true);

        var result = await ScanAsync();

        Assert.Equal(1, result.Removed);
        await using var db = CreateContext();
        Assert.False(await db.Albums.AnyAsync(a => a.Title == "Sampler", TestContext.Current.CancellationToken));
        Assert.False(await db.Artists.AnyAsync(a => a.Name == "Jemand Anderes", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Zweites_Format_desselben_Songs_haengt_am_selben_Track()
    {
        await ScanAsync();
        _library.Copy("song1.mp3", Path.Combine(_library.Library, "Testband", "2024 - Testalbum", "flac"), "01 - Erster Song.mp3");

        var result = await ScanAsync();

        Assert.Equal(1, result.Added);
        await using var db = CreateContext();
        var track = await db.Tracks.Include(t => t.Files).SingleAsync(t => t.Title == "Erster Song", TestContext.Current.CancellationToken);
        Assert.Equal(2, track.Files.Count);
    }

    [Fact]
    public async Task Geaenderte_Datei_wird_neu_eingelesen()
    {
        await ScanAsync();
        File.SetLastWriteTimeUtc(Path.Combine(_library.AlbumFolder, "03 - Dritter Song.m4a"), DateTime.UtcNow.AddMinutes(1));

        var result = await ScanAsync();

        Assert.Equal(1, result.Updated);
        Assert.Equal(3, result.Unchanged);
    }

    [Fact]
    public async Task Unlesbare_Datei_wird_als_Fehler_gemeldet()
    {
        File.WriteAllText(Path.Combine(_library.AlbumFolder, "kaputt.mp3"), "kein mp3");

        var result = await ScanAsync();

        Assert.Equal(4, result.Added);
        Assert.Equal("Testband/2024 - Testalbum/kaputt.mp3", Assert.Single(result.Errors).Path);
    }

    [Fact]
    public async Task Mehrere_Jobs_fuer_denselben_Pfad_verknuepfen_den_juengsten()
    {
        // Der ältere Job verlor seine Datei (Dublette verworfen, AudioFileId zurückgesetzt), der neuere
        // wurde danach auf denselben, frei gewordenen Pfad übernommen.
        await ScanAsync();
        const string path = "Testband/2024 - Testalbum/04 - Neu.mp3";
        _library.Copy("song1.mp3", _library.AlbumFolder, "04 - Neu.mp3");
        await using (var setup = CreateContext())
        {
            setup.ImportJobs.Add(new ImportJob { SourcePath = "alt/Neu.mp3", State = ImportState.Imported, LibraryPath = path });
            setup.ImportJobs.Add(new ImportJob { SourcePath = "neu/Neu.mp3", State = ImportState.Imported, LibraryPath = path, Source = FileSource.Api });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await ScanAsync();

        Assert.Equal(1, result.Added);
        await using var db = CreateContext();
        var file = await db.AudioFiles.SingleAsync(f => f.Path == path, TestContext.Current.CancellationToken);
        Assert.Equal(FileSource.Api, file.Source);
        var jobs = await db.ImportJobs.OrderBy(j => j.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Null(jobs[0].AudioFileId);
        Assert.Equal(file.Id, jobs[1].AudioFileId);
    }

    [Fact]
    public async Task Ohne_Mount_Marker_kein_Scan()
    {
        File.Delete(Path.Combine(_library.MusicRoot, ".tonregal-mount"));

        await Assert.ThrowsAsync<LibraryUnavailableException>(ScanAsync);
    }
}
