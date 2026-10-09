using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AddonForge.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPersistentPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DetectedPersistentPaths",
                table: "ManagedAddons",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PersistenceCheckedAt",
                table: "ManagedAddons",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PersistenceReviewed",
                table: "ManagedAddons",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PersistentPaths",
                table: "ManagedAddons",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetectedPersistentPaths",
                table: "ManagedAddons");

            migrationBuilder.DropColumn(
                name: "PersistenceCheckedAt",
                table: "ManagedAddons");

            migrationBuilder.DropColumn(
                name: "PersistenceReviewed",
                table: "ManagedAddons");

            migrationBuilder.DropColumn(
                name: "PersistentPaths",
                table: "ManagedAddons");
        }
    }
}
