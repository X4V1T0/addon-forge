using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AddonForge.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManagedAddons",
                columns: table => new
                {
                    Slug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SourceRepoUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Ingress = table.Column<bool>(type: "INTEGER", nullable: false),
                    IngressPort = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceRefType = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackedBranch = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentRef = table.Column<string>(type: "TEXT", nullable: true),
                    CurrentVersion = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatePolicy = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastWrittenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StagedRef = table.Column<string>(type: "TEXT", nullable: true),
                    StagedVersion = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedAddons", x => x.Slug);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManagedAddons");
        }
    }
}
