using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260724190000_AddManualLyricsMappings")]
public partial class AddManualLyricsMappings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var textType = "text";
        var guidType = "uuid";
        var integerType = "integer";
        var bigintType = "bigint";

        migrationBuilder.CreateTable(
            name: "manual_lyrics_mappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: guidType, nullable: false),
                IdentityHash = table.Column<string>(type: textType, maxLength: 64, nullable: false),
                Artist = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                Title = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                Album = table.Column<string>(type: textType, maxLength: 500, nullable: true),
                DurationSeconds = table.Column<int>(type: integerType, nullable: false),
                LyricsId = table.Column<int>(type: integerType, nullable: false),
                CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_manual_lyrics_mappings", item => item.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_manual_lyrics_mappings_IdentityHash",
            table: "manual_lyrics_mappings",
            column: "IdentityHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_manual_lyrics_mappings_UpdatedAt",
            table: "manual_lyrics_mappings",
            column: "UpdatedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "manual_lyrics_mappings");
    }
}
