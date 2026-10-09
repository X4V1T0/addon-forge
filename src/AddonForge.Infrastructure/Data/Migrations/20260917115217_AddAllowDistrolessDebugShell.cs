using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AddonForge.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAllowDistrolessDebugShell : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowDistrolessDebugShell",
                table: "ManagedAddons",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowDistrolessDebugShell",
                table: "ManagedAddons");
        }
    }
}
