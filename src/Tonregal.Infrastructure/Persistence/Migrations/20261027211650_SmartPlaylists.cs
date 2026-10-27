using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Tonregal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SmartPlaylists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SmartPlaylists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    BpmMin = table.Column<double>(type: "REAL", nullable: true),
                    BpmMax = table.Column<double>(type: "REAL", nullable: true),
                    Keys = table.Column<string>(type: "TEXT", nullable: false),
                    Genre = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AddedWithinDays = table.Column<int>(type: "INTEGER", nullable: true),
                    NeverPlayed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Sort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Limit = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmartPlaylists", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "SmartPlaylists",
                columns: new[] { "Id", "AddedWithinDays", "BpmMax", "BpmMin", "Comment", "Genre", "Keys", "Limit", "Name", "NeverPlayed", "Sort", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, 30, null, null, null, null, "[]", null, "Neu (30 Tage)", false, "Newest", 639286560000000000L },
                    { 2, null, null, null, null, null, "[]", null, "Nie gehört", true, "Random", 639286560000000000L }
                });

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlaylists_Name",
                table: "SmartPlaylists",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SmartPlaylists");
        }
    }
}
