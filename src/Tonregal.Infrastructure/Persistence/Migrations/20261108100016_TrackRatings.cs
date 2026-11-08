using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tonregal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RatedAt",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Rating",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RatingPending",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_RatingPending",
                table: "Tracks",
                column: "RatingPending");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tracks_RatingPending",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "RatedAt",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "RatingPending",
                table: "Tracks");
        }
    }
}
