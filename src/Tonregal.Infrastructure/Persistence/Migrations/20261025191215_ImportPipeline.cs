using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tonregal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "ImportJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FileHash",
                table: "ImportJobs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "ImportJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "ImportJobs",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<long>(
                name: "ModifiedAt",
                table: "ImportJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ReviewReason",
                table: "ImportJobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedRecordingId",
                table: "ImportJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedReleaseId",
                table: "ImportJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SizeBytes",
                table: "ImportJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "TagAlbum",
                table: "ImportJobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TagArtist",
                table: "ImportJobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TagTitle",
                table: "ImportJobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "FileHash",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "Format",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "ReviewReason",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "SelectedRecordingId",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "SelectedReleaseId",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "SizeBytes",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "TagAlbum",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "TagArtist",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "TagTitle",
                table: "ImportJobs");
        }
    }
}
