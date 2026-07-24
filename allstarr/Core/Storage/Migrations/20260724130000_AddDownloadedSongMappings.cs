using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260724130000_AddDownloadedSongMappings")]
public partial class AddDownloadedSongMappings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var textType = postgres ? "text" : "TEXT";
        var guidType = postgres ? "uuid" : "TEXT";
        var bigintType = postgres ? "bigint" : "INTEGER";

        migrationBuilder.CreateTable(
            name: "downloaded_song_mappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: guidType, nullable: false),
                ProviderId = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                ExternalId = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                LocalPath = table.Column<string>(type: textType, maxLength: 2000, nullable: false),
                Title = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                Artist = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                Album = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                DownloadedAt = table.Column<long>(type: bigintType, nullable: false),
                Revision = table.Column<long>(type: bigintType, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_downloaded_song_mappings", item => item.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_downloaded_song_mapping_identity",
            table: "downloaded_song_mappings",
            columns: new[] { "ProviderId", "ExternalId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "downloaded_song_mappings");
    }
}
