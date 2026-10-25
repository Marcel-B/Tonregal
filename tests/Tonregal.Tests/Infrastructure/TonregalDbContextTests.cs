// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tonregal.Domain.Entities;
using Tonregal.Infrastructure.Persistence;

namespace Tonregal.Tests.Infrastructure;

public sealed class TonregalDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public TonregalDbContextTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private TonregalDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TonregalDbContext>().UseSqlite(_connection).Options);

    [Fact]
    public async Task Migration_laeuft_und_Modell_ist_aktuell()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Track_mit_mehreren_Artists_und_Dateien_speichern_und_laden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync(ct);
            var albumArtist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" };
            var guest = new Artist { Name = "Pharrell Williams", SortName = "Williams, Pharrell" };
            var album = new Album { Title = "Random Access Memories", AlbumArtist = albumArtist, Year = 2013 };
            var track = new Track
            {
                Album = album, Number = 8, Title = "Get Lucky", DurationMs = 369_000, Bpm = 116,
                Artists = [albumArtist, guest],
                Files =
                [
                    new AudioFile { Path = "a.mp3", FileHash = new string('a', 64), Format = AudioFormat.Mp3, AddedAt = DateTimeOffset.UtcNow },
                    new AudioFile { Path = "a.flac", FileHash = new string('b', 64), Format = AudioFormat.Flac, AddedAt = DateTimeOffset.UtcNow },
                ],
            };
            db.Tracks.Add(track);
            await db.SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            var loaded = await db.Tracks.Include(t => t.Artists).Include(t => t.Files).Include(t => t.Album).SingleAsync(ct);
            Assert.Equal(2, loaded.Artists.Count);
            Assert.Equal(2, loaded.Files.Count);
            Assert.Equal("Random Access Memories", loaded.Album.Title);
            Assert.Equal(1, await db.Tracks.CountAsync(t => t.Bpm > 110 && t.Bpm < 120, ct));
        }
    }

    [Fact]
    public async Task ExternalId_ist_eindeutig()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = CreateContext();
        await db.Database.MigrateAsync(ct);
        db.ImportJobs.Add(new ImportJob { SourcePath = "x", ExternalId = "tonwerk-ai:1", StartedAt = DateTimeOffset.UtcNow });
        db.ImportJobs.Add(new ImportJob { SourcePath = "y", ExternalId = "tonwerk-ai:1", StartedAt = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
    }
}
