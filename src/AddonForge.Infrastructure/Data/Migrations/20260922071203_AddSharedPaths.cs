using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AddonForge.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SharedPaths",
                table: "ManagedAddons",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SharedPaths",
                table: "ManagedAddons");
        }
    }
}
