using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace P4ServerManager.Daemon.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HealthCheckRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Checks = table.Column<string>(type: "TEXT", nullable: false),
                    P4dVersion = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    HasFixJournal = table.Column<bool>(type: "INTEGER", nullable: false),
                    ServerRestartPending = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReportHidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    CrossCheckPairs = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HealthCheckRuns_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HealthCheckFindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Check = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Table = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HealthCheckFindings_HealthCheckRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "HealthCheckRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckFindings_RunId",
                table: "HealthCheckFindings",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckRuns_InstanceId",
                table: "HealthCheckRuns",
                column: "InstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthCheckFindings");

            migrationBuilder.DropTable(
                name: "HealthCheckRuns");
        }
    }
}
