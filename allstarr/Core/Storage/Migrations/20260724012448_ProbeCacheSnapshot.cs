using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    [DbContext(typeof(AllstarrDbContext))]
    [Migration("20260724012448_ProbeCacheSnapshot")]
    public partial class ProbeCacheSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            migrationBuilder.AlterColumn<bool>(
                name: "Enabled",
                table: "playlist_links",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            migrationBuilder.AlterColumn<int>(
                name: "Enabled",
                table: "playlist_links",
                type: "integer",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: true);
        }
    }
}
