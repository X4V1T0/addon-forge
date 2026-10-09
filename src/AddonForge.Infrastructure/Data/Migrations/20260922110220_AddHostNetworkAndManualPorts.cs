using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AddonForge.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostNetworkAndManualPorts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HostNetwork",
                table: "ManagedAddons",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ManualPorts",
                table: "ManagedAddons",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HostNetwork",
                table: "ManagedAddons");

            migrationBuilder.DropColumn(
                name: "ManualPorts",
                table: "ManagedAddons");
        }
    }
}
