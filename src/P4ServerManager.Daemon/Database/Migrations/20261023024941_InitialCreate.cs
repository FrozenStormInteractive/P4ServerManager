using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace P4ServerManager.Daemon.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Builds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Release = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    BuildNumber = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DownloadedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Builds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Instances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    RootPath = table.Column<string>(type: "TEXT", nullable: false),
                    Release = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    BuildId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CaseSensitive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Unicode = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Instances_Builds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "Builds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Builds_Platform_Version_BuildNumber",
                table: "Builds",
                columns: new[] { "Platform", "Version", "BuildNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instances_BuildId",
                table: "Instances",
                column: "BuildId");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_Name",
                table: "Instances",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instances_Port",
                table: "Instances",
                column: "Port",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Instances");

            migrationBuilder.DropTable(
                name: "Builds");
        }
    }
}
