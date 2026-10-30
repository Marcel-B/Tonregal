// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tonregal.Domain.Entities;

namespace Tonregal.Infrastructure.Persistence;

internal sealed class ArtistConfiguration : IEntityTypeConfiguration<Artist>
{
    public void Configure(EntityTypeBuilder<Artist> b)
    {
        b.Property(x => x.Name).HasMaxLength(512).IsRequired();
        b.Property(x => x.SortName).HasMaxLength(512).IsRequired();
        b.HasIndex(x => x.Name);
        b.HasIndex(x => x.MusicBrainzArtistId);
    }
}

internal sealed class AlbumConfiguration : IEntityTypeConfiguration<Album>
{
    public void Configure(EntityTypeBuilder<Album> b)
    {
        b.Property(x => x.Title).HasMaxLength(512).IsRequired();
        b.Property(x => x.CoverPath).HasMaxLength(1024);
        b.HasOne(x => x.AlbumArtist).WithMany(x => x.Albums)
            .HasForeignKey(x => x.AlbumArtistId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.MusicBrainzReleaseId);
        b.HasIndex(x => new { x.AlbumArtistId, x.Title });
    }
}

internal sealed class TrackConfiguration : IEntityTypeConfiguration<Track>
{
    public void Configure(EntityTypeBuilder<Track> b)
    {
        b.Property(x => x.Title).HasMaxLength(512).IsRequired();
        b.Property(x => x.InitialKey).HasMaxLength(8);
        b.Property(x => x.Genre).HasMaxLength(128);
        b.HasOne(x => x.Album).WithMany(x => x.Tracks)
            .HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Artists).WithMany(x => x.Tracks).UsingEntity("TrackArtist");
        b.HasIndex(x => x.MusicBrainzRecordingId);
        b.HasIndex(x => x.AcoustId);
        b.HasIndex(x => new { x.AlbumId, x.Disc, x.Number });
    }
}

internal sealed class AudioFileConfiguration : IEntityTypeConfiguration<AudioFile>
{
    public void Configure(EntityTypeBuilder<AudioFile> b)
    {
        b.Property(x => x.Path).HasMaxLength(1024).IsRequired();
        b.Property(x => x.FileHash).HasMaxLength(64).IsFixedLength().IsRequired();
        b.HasOne(x => x.Track).WithMany(x => x.Files)
            .HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.Path).IsUnique();
        b.HasIndex(x => x.FileHash);
    }
}

internal sealed class ImportJobConfiguration : IEntityTypeConfiguration<ImportJob>
{
    public void Configure(EntityTypeBuilder<ImportJob> b)
    {
        b.Property(x => x.SourcePath).HasMaxLength(1024).IsRequired();
        b.Property(x => x.ExternalId).HasMaxLength(256);
        b.Property(x => x.FileHash).HasMaxLength(64);
        b.Property(x => x.TagTitle).HasMaxLength(512);
        b.Property(x => x.TagArtist).HasMaxLength(512);
        b.Property(x => x.TagAlbum).HasMaxLength(512);
        b.Property(x => x.ReviewReason).HasMaxLength(512);
        b.Property(x => x.LibraryPath).HasMaxLength(1024);
        b.Ignore(x => x.Group);
        b.HasOne(x => x.AudioFile).WithMany()
            .HasForeignKey(x => x.AudioFileId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.State);
        b.HasIndex(x => x.ExternalId).IsUnique();
    }
}

internal sealed class DuplicateGroupConfiguration : IEntityTypeConfiguration<DuplicateGroup>
{
    public void Configure(EntityTypeBuilder<DuplicateGroup> b)
    {
        b.HasMany(x => x.Files).WithMany().UsingEntity("DuplicateGroupFile");
        b.HasOne(x => x.KeeperFile).WithMany()
            .HasForeignKey(x => x.KeeperFileId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.Decision);
    }
}

internal sealed class AnalysisResultConfiguration : IEntityTypeConfiguration<AnalysisResult>
{
    public void Configure(EntityTypeBuilder<AnalysisResult> b)
    {
        b.Property(x => x.Tool).HasMaxLength(64).IsRequired();
        b.Property(x => x.Version).HasMaxLength(64).IsRequired();
        b.Property(x => x.Key).HasMaxLength(8);
        b.HasOne(x => x.AudioFile).WithMany(x => x.AnalysisResults)
            .HasForeignKey(x => x.AudioFileId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.AudioFileId, x.Tool });
    }
}

internal sealed class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> b) => b.HasIndex(x => x.StartedAt);
}

internal sealed class MetadataCacheEntryConfiguration : IEntityTypeConfiguration<MetadataCacheEntry>
{
    public void Configure(EntityTypeBuilder<MetadataCacheEntry> b)
    {
        b.ToTable("MetadataCache");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(128);
        b.Property(x => x.Json).IsRequired();
    }
}

internal sealed class IngestKeyConfiguration : IEntityTypeConfiguration<IngestKey>
{
    public void Configure(EntityTypeBuilder<IngestKey> b)
    {
        b.HasKey(x => x.Source);
        b.Property(x => x.Source).HasMaxLength(64);
        b.Property(x => x.Key).HasMaxLength(128);
    }
}

internal sealed class SmartPlaylistConfiguration : IEntityTypeConfiguration<SmartPlaylist>
{
    public void Configure(EntityTypeBuilder<SmartPlaylist> b)
    {
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Comment).HasMaxLength(500);
        b.Property(x => x.Genre).HasMaxLength(100);

        // Die beiden Listen aus dem Plan als Startpunkt; gelöschte kommen nicht wieder.
        var seeded = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        b.HasData(
            new SmartPlaylist { Id = 1, Name = "Neu (30 Tage)", AddedWithinDays = 30, Sort = SmartPlaylistSort.Newest, UpdatedAt = seeded },
            new SmartPlaylist { Id = 2, Name = "Nie gehört", NeverPlayed = true, Sort = SmartPlaylistSort.Random, UpdatedAt = seeded });
    }
}

internal sealed class TrackLyricsConfiguration : IEntityTypeConfiguration<TrackLyrics>
{
    public void Configure(EntityTypeBuilder<TrackLyrics> b)
    {
        b.ToTable("Lyrics");
        b.HasKey(x => x.TrackId);
        b.HasOne(x => x.Track).WithOne()
            .HasForeignKey<TrackLyrics>(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
    }
}
