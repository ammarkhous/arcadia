using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arcadia.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Tracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourcePath",
                table: "Screenshots",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAutomatic",
                table: "GameSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "InstallDirectory",
                table: "Games",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Screenshots_SourcePath",
                table: "Screenshots",
                column: "SourcePath");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Screenshots_SourcePath",
                table: "Screenshots");

            migrationBuilder.DropColumn(
                name: "SourcePath",
                table: "Screenshots");

            migrationBuilder.DropColumn(
                name: "IsAutomatic",
                table: "GameSessions");

            migrationBuilder.DropColumn(
                name: "InstallDirectory",
                table: "Games");
        }
    }
}
