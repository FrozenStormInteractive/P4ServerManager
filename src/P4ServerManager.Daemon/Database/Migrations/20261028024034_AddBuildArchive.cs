using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace P4ServerManager.Daemon.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ArchiveSize",
                table: "Builds",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Imported",
                table: "Builds",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsExtracted",
                table: "Builds",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "P4Sha256",
                table: "Builds",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Protected",
                table: "Builds",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BuildArchivePolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoDownload = table.Column<bool>(type: "INTEGER", nullable: false),
                    CheckIntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Releases = table.Column<string>(type: "TEXT", nullable: false),
                    KeepLatestPerRelease = table.Column<int>(type: "INTEGER", nullable: true),
                    LastCheckAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastCheckError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildArchivePolicies", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildArchivePolicies");

            migrationBuilder.DropColumn(
                name: "ArchiveSize",
                table: "Builds");

            migrationBuilder.DropColumn(
                name: "Imported",
                table: "Builds");

            migrationBuilder.DropColumn(
                name: "IsExtracted",
                table: "Builds");

            migrationBuilder.DropColumn(
                name: "P4Sha256",
                table: "Builds");

            migrationBuilder.DropColumn(
                name: "Protected",
                table: "Builds");
        }
    }
}
