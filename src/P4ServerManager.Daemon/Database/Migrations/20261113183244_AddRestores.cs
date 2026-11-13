using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace P4ServerManager.Daemon.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRestores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RestoreRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DestinationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SnapshotKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PointId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PointCreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DownloadedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ReusedFiles = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifyProblemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifyProblems = table.Column<string>(type: "TEXT", nullable: false),
                    Warnings = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestoreRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RestoreRuns_InstanceId",
                table: "RestoreRuns",
                column: "InstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RestoreRuns");
        }
    }
}
