using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace P4ServerManager.Daemon.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupDestinations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedCredentials = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedPassphrase = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupDestinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CheckpointNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    JournalNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstanceBackupDestinations",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceBackupDestinations", x => new { x.InstanceId, x.DestinationId });
                    table.ForeignKey(
                        name: "FK_InstanceBackupDestinations_BackupDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "BackupDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InstanceBackupDestinations_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackupRunDestinations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UploadedObjects = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    SnapshotKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    VerificationDeep = table.Column<bool>(type: "INTEGER", nullable: false),
                    VerificationSucceeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    VerificationCheckedObjects = table.Column<int>(type: "INTEGER", nullable: false),
                    VerificationProblemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    VerificationProblems = table.Column<string>(type: "TEXT", nullable: false),
                    VerificationError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRunDestinations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupRunDestinations_BackupRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "BackupRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupDestinations_Name",
                table: "BackupDestinations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupRunDestinations_RunId",
                table: "BackupRunDestinations",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_InstanceId",
                table: "BackupRuns",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_InstanceBackupDestinations_DestinationId",
                table: "InstanceBackupDestinations",
                column: "DestinationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupRunDestinations");

            migrationBuilder.DropTable(
                name: "InstanceBackupDestinations");

            migrationBuilder.DropTable(
                name: "BackupRuns");

            migrationBuilder.DropTable(
                name: "BackupDestinations");
        }
    }
}
