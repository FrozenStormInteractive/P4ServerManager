using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace P4ServerManager.Daemon.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceAutoStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoStart",
                table: "Instances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoStart",
                table: "Instances");
        }
    }
}
