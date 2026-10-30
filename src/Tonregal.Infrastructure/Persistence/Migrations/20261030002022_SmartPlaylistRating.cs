using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tonregal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SmartPlaylistRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinRating",
                table: "SmartPlaylists",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "SmartPlaylists",
                keyColumn: "Id",
                keyValue: 1,
                column: "MinRating",
                value: null);

            migrationBuilder.UpdateData(
                table: "SmartPlaylists",
                keyColumn: "Id",
                keyValue: 2,
                column: "MinRating",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinRating",
                table: "SmartPlaylists");
        }
    }
}
