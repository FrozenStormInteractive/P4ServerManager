using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace P4ServerManager.Daemon.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAdministrators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAdministrator",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAdministrator",
                table: "Users");
        }
    }
}
