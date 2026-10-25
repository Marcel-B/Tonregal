using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tonregal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportLibraryPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LibraryPath",
                table: "ImportJobs",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LibraryPath",
                table: "ImportJobs");
        }
    }
}
