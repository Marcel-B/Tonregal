// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tonregal.Application;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Import;
using Tonregal.Infrastructure.Library;
using Tonregal.Infrastructure.Persistence;
using Tonregal.Infrastructure.Tagging;

namespace Tonregal.Tests.Import;

public sealed class LegacyCleanupTests : IDisposable
{
    private const string First = "Testband/2024 - Testalbum/01 - Erster Song.mp3";
    private const string Second = "Testband/2024 - Testalbum/02 - Zweiter Song.flac";

    private readonly TestLibrary _library = new();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Legacy => Path.Combine(_library.MusicRoot, "Altbestand");
    private TonregalOptions Options => new() { MusicRoot = _library.MusicRoot };

    public LegacyCleanupTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.Migrate();
        new LibraryScanner(
                db, new TagLibTagReader(), Microsoft.Extensions.Options.Options.Create(Options),
                TimeProvider.System, NullLogger<LibraryScanner>.Instance)
            .ScanAsync(null, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _library.Dispose();
    }

    private TonregalDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TonregalDbContext>().UseSqlite(_connection).Options);

    private LegacyCleanup Cleanup(TonregalDbContext db) =>
        new(db, new LibraryWriteLock(), Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System, NullLogger<LegacyCleanup>.Instance);

    /// <summary>Legt die Datei im Altbestand an und einen Job, der sie so kennt, wie sie jetzt ist.</summary>
    private async Task<ImportJob> JobAsync(string relative, ImportState state, Action<ImportJob>? setup = null, string testFile = "song1.mp3")
    {
        var full = _library.Copy(testFile, Path.Combine(Legacy, Path.GetDirectoryName(relative)!), Path.GetFileName(relative));
        var info = new FileInfo(full);
        var job = new ImportJob
        {
            SourcePath = relative,
            Source = FileSource.Legacy,
            State = state,
            SizeBytes = info.Length,
            ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            StartedAt = DateTimeOffset.UtcNow,
        };
        setup?.Invoke(job);
        await using var db = CreateContext();
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync(Ct);
        return job;
    }

    [Fact]
    public async Task Vorschau_nimmt_nur_Dateien_mit_Beleg_in_der_Library()
    {
        var imported = await JobAsync("Alt/A/01.mp3", ImportState.Imported, j => j.LibraryPath = First);
        var already = await JobAsync("Alt/A/02.mp3", ImportState.Rejected, j => j.Error = ImportJob.AlreadyInLibrary + Second);
        var missing = await JobAsync("Alt/B/03.mp3", ImportState.Imported, j => j.LibraryPath = "Weg/1999 - Fort/01 - Weg.mp3");
        var changed = await JobAsync("Alt/B/04.mp3", ImportState.Imported, j => { j.LibraryPath = First; j.SizeBytes++; });
        await JobAsync("Alt/C/05.mp3", ImportState.NeedsReview);
        await JobAsync("Alt/C/06.mp3", ImportState.Rejected, j => j.Error = ImportJob.RejectedByUser);
        // Erst übernommen, dann geändert und neu im Abgleich: Es zählt der jüngste Job.
        await JobAsync("Alt/C/07.mp3", ImportState.Imported, j => j.LibraryPath = First);
        await JobAsync("Alt/C/07.mp3", ImportState.NeedsReview);
        // Ein Pfad hinaus aus der Library ist kein Beleg.
        await JobAsync("Alt/C/08.mp3", ImportState.Imported, j => j.LibraryPath = "../Altbestand/Alt/C/08.mp3");

        await using var db = CreateContext();
        var preview = await Cleanup(db).PreviewAsync(Ct);

        Assert.Equal([imported.Id, already.Id], preview.Files.Select(f => f.JobId));
        Assert.Equal(First, preview.Files[0].LibraryPath);
        Assert.True(preview.Files[1].AlreadyInLibrary);
        Assert.Equal(2, preview.Count);
        Assert.Equal(preview.Files.Sum(f => f.SizeBytes), preview.SizeBytes);
        Assert.Equal(
            [(missing.Id, LegacyCleanup.LibraryCopyMissing), (changed.Id, LegacyCleanup.Changed)],
            preview.Skipped.Where(s => s.Path.StartsWith("Alt/B/")).Select(s => (s.JobId, s.Reason)));
        Assert.Single(preview.Skipped, s => s.Path == "Alt/C/08.mp3");
        Assert.EndsWith("/Altbestand", preview.TrashFolder);
        Assert.StartsWith(".trash/", preview.TrashFolder);
    }

    [Fact]
    public async Task Verschobene_oder_neu_getaggte_Library_Datei_wird_ueber_Aufnahme_und_Hash_gefunden()
    {
        var recording = Guid.Parse("33333333-0000-0000-0000-000000000001");
        string hash;
        await using (var db = CreateContext())
        {
            var file = await db.AudioFiles.Include(f => f.Track).SingleAsync(f => f.Path == First, Ct);
            file.Track.MusicBrainzRecordingId = recording;
            hash = (await db.AudioFiles.SingleAsync(f => f.Path == Second, Ct)).FileHash;
            await db.SaveChangesAsync(Ct);
        }

        // Nach „Ausgaben vereinheitlichen“ stimmt der Pfad des Jobs nicht mehr, die Aufnahme schon.
        var moved = await JobAsync("Alt/01.mp3", ImportState.Imported, j =>
        {
            j.LibraryPath = "Testband/1990 - Alte Ausgabe/01 - Erster Song.mp3";
            j.SelectedRecordingId = recording;
        });
        // Die gemeldete Datei ist umbenannt, eine mit gleichem Hash liegt noch da.
        var renamed = await JobAsync("Alt/02.mp3", ImportState.Rejected, j =>
        {
            j.Error = ImportJob.AlreadyInLibrary + "Testband/Alt/02.flac";
            j.FileHash = hash;
        });

        await using var read = CreateContext();
        var preview = await Cleanup(read).PreviewAsync(Ct);

        Assert.Equal([(moved.Id, First), (renamed.Id, Second)], preview.Files.Select(f => (f.JobId, f.LibraryPath)));
        Assert.Empty(preview.Skipped);
    }

    [Fact]
    public async Task Aufraeumen_verschiebt_in_den_Papierkorb_und_prueft_jede_Datei_erneut()
    {
        var imported = await JobAsync("Alt/A/01.mp3", ImportState.Imported, j => j.LibraryPath = First);
        File.WriteAllText(Path.Combine(Legacy, "Alt", "A", ".DS_Store"), "");
        var already = await JobAsync("Alt/B/02.mp3", ImportState.Rejected, j => j.Error = ImportJob.AlreadyInLibrary + Second);
        File.WriteAllText(Path.Combine(Legacy, "Alt", "B", "booklet.pdf"), "");
        var review = await JobAsync("Alt/C/03.mp3", ImportState.NeedsReview);
        var gone = await JobAsync("Alt/C/04.mp3", ImportState.Imported, j => j.LibraryPath = Second);

        await using (var db = CreateContext())
            Assert.Equal(3, (await Cleanup(db).PreviewAsync(Ct)).Count);
        // Zwischen Vorschau und Bestätigung verschwindet die Library-Datei eines Kandidaten.
        File.Delete(Path.Combine(_library.Library, Second));

        await using var clean = CreateContext();
        var result = await Cleanup(clean).CleanAsync([imported.Id, already.Id, review.Id, gone.Id], Ct);

        Assert.Equal(1, result.Moved);
        Assert.Equal(new FileInfo(Path.Combine(_library.Library, First)).Length, result.SizeBytes);
        Assert.Equal(
            [(already.Id, LegacyCleanup.LibraryCopyMissing), (gone.Id, LegacyCleanup.LibraryCopyMissing)],
            result.Skipped.Select(s => (s.JobId, s.Reason)));

        var trash = Path.Combine(_library.Library, ".trash", LibraryTrash.Day(DateTimeOffset.UtcNow), "Altbestand");
        Assert.True(File.Exists(Path.Combine(trash, "Alt", "A", "01.mp3")));
        // Ordner mit nur macOS-Resten verschwindet, alles andere bleibt, wie es ist.
        Assert.False(Directory.Exists(Path.Combine(Legacy, "Alt", "A")));
        Assert.True(File.Exists(Path.Combine(Legacy, "Alt", "B", "02.mp3")));
        Assert.True(File.Exists(Path.Combine(Legacy, "Alt", "C", "03.mp3")));
        Assert.True(File.Exists(Path.Combine(Legacy, "Alt", "C", "04.mp3")));

        // Was weg ist, taucht in der nächsten Vorschau nicht mehr auf.
        await using var after = CreateContext();
        Assert.Equal(0, (await Cleanup(after).PreviewAsync(Ct)).Count);
    }
}
