using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tonregal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Artists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SortName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    MusicBrainzArtistId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Artists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FilesSynced = table.Column<int>(type: "INTEGER", nullable: false),
                    BytesSynced = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Log = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Albums",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AlbumArtistId = table.Column<int>(type: "INTEGER", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    MusicBrainzReleaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DiscCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CoverPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Albums", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Albums_Artists_AlbumArtistId",
                        column: x => x.AlbumArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AlbumId = table.Column<int>(type: "INTEGER", nullable: false),
                    Disc = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    MusicBrainzRecordingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AcoustId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Bpm = table.Column<double>(type: "REAL", nullable: true),
                    InitialKey = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    ReplayGainTrack = table.Column<double>(type: "REAL", nullable: true),
                    Genre = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tracks_Albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "Albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AudioFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrackId = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Bitrate = table.Column<int>(type: "INTEGER", nullable: false),
                    SampleRate = table.Column<int>(type: "INTEGER", nullable: false),
                    Channels = table.Column<int>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    FileHash = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 64, nullable: false),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudioFiles_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackArtist",
                columns: table => new
                {
                    ArtistsId = table.Column<int>(type: "INTEGER", nullable: false),
                    TracksId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackArtist", x => new { x.ArtistsId, x.TracksId });
                    table.ForeignKey(
                        name: "FK_TrackArtist_Artists_ArtistsId",
                        column: x => x.ArtistsId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrackArtist_Tracks_TracksId",
                        column: x => x.TracksId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AudioFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Bpm = table.Column<double>(type: "REAL", nullable: true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    KeyConfidence = table.Column<double>(type: "REAL", nullable: true),
                    Loudness = table.Column<double>(type: "REAL", nullable: true),
                    RanAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalysisResults_AudioFiles_AudioFileId",
                        column: x => x.AudioFileId,
                        principalTable: "AudioFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DuplicateGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    KeeperFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DecidedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DuplicateGroups_AudioFiles_KeeperFileId",
                        column: x => x.KeeperFileId,
                        principalTable: "AudioFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ImportJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MatchScore = table.Column<double>(type: "REAL", nullable: true),
                    Candidates = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AudioFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportJobs_AudioFiles_AudioFileId",
                        column: x => x.AudioFileId,
                        principalTable: "AudioFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DuplicateGroupFile",
                columns: table => new
                {
                    DuplicateGroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    FilesId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateGroupFile", x => new { x.DuplicateGroupId, x.FilesId });
                    table.ForeignKey(
                        name: "FK_DuplicateGroupFile_AudioFiles_FilesId",
                        column: x => x.FilesId,
                        principalTable: "AudioFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DuplicateGroupFile_DuplicateGroups_DuplicateGroupId",
                        column: x => x.DuplicateGroupId,
                        principalTable: "DuplicateGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Albums_AlbumArtistId_Title",
                table: "Albums",
                columns: new[] { "AlbumArtistId", "Title" });

            migrationBuilder.CreateIndex(
                name: "IX_Albums_MusicBrainzReleaseId",
                table: "Albums",
                column: "MusicBrainzReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisResults_AudioFileId_Tool",
                table: "AnalysisResults",
                columns: new[] { "AudioFileId", "Tool" });

            migrationBuilder.CreateIndex(
                name: "IX_Artists_MusicBrainzArtistId",
                table: "Artists",
                column: "MusicBrainzArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_Artists_Name",
                table: "Artists",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_AudioFiles_FileHash",
                table: "AudioFiles",
                column: "FileHash");

            migrationBuilder.CreateIndex(
                name: "IX_AudioFiles_Path",
                table: "AudioFiles",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AudioFiles_TrackId",
                table: "AudioFiles",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_StartedAt",
                table: "BackupRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateGroupFile_FilesId",
                table: "DuplicateGroupFile",
                column: "FilesId");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateGroups_Decision",
                table: "DuplicateGroups",
                column: "Decision");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateGroups_KeeperFileId",
                table: "DuplicateGroups",
                column: "KeeperFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportJobs_AudioFileId",
                table: "ImportJobs",
                column: "AudioFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportJobs_ExternalId",
                table: "ImportJobs",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportJobs_State",
                table: "ImportJobs",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_TrackArtist_TracksId",
                table: "TrackArtist",
                column: "TracksId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_AcoustId",
                table: "Tracks",
                column: "AcoustId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_AlbumId_Disc_Number",
                table: "Tracks",
                columns: new[] { "AlbumId", "Disc", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_MusicBrainzRecordingId",
                table: "Tracks",
                column: "MusicBrainzRecordingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalysisResults");

            migrationBuilder.DropTable(
                name: "BackupRuns");

            migrationBuilder.DropTable(
                name: "DuplicateGroupFile");

            migrationBuilder.DropTable(
                name: "ImportJobs");

            migrationBuilder.DropTable(
                name: "TrackArtist");

            migrationBuilder.DropTable(
                name: "DuplicateGroups");

            migrationBuilder.DropTable(
                name: "AudioFiles");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropTable(
                name: "Albums");

            migrationBuilder.DropTable(
                name: "Artists");
        }
    }
}
