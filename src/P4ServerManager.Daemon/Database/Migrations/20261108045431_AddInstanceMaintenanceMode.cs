using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace P4ServerManager.Daemon.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceMaintenanceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MaintenanceMode",
                table: "Instances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaintenanceMode",
                table: "Instances");
        }
    }
}
