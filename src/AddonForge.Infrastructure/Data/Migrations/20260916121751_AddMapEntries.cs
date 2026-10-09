using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AddonForge.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMapEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MapEntries",
                table: "ManagedAddons",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MapEntries",
                table: "ManagedAddons");
        }
    }
}
