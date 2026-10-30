// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Tonregal.Domain.Entities;

namespace Tonregal.Infrastructure.Persistence;

/// <summary>
/// Index über die Library. Bis auf <see cref="ImportJobs"/>, <see cref="DuplicateGroups"/>,
/// <see cref="BackupRuns"/> und <see cref="IngestKeys"/> lässt sich alles aus den Tags der Dateien neu aufbauen.
/// </summary>
public class TonregalDbContext(DbContextOptions<TonregalDbContext> options) : DbContext(options)
{
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<AudioFile> AudioFiles => Set<AudioFile>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<DuplicateGroup> DuplicateGroups => Set<DuplicateGroup>();
    public DbSet<AnalysisResult> AnalysisResults => Set<AnalysisResult>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<MetadataCacheEntry> MetadataCache => Set<MetadataCacheEntry>();
    public DbSet<IngestKey> IngestKeys => Set<IngestKey>();
    public DbSet<SmartPlaylist> SmartPlaylists => Set<SmartPlaylist>();
    public DbSet<TrackLyrics> Lyrics => Set<TrackLyrics>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TonregalDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite kann DateTimeOffset nicht sortieren; als UTC-Ticks speichern.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToUtcTicksConverter>();
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }
}
