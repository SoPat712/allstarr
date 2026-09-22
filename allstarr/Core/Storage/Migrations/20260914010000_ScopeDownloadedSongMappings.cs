using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260914010000_ScopeDownloadedSongMappings")]
public sealed class ScopeDownloadedSongMappings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_downloaded_song_mapping_identity",
            table: "downloaded_song_mappings");

        migrationBuilder.AddColumn<int>(
            name: "AudioQuality",
            table: "downloaded_song_mappings",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "LibraryScopeId",
            table: "downloaded_song_mappings",
            type: "character varying(300)",
            maxLength: 300,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ProviderAccountId",
            table: "downloaded_song_mappings",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ScopeKey",
            table: "downloaded_song_mappings",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "legacy");

        migrationBuilder.AddColumn<Guid>(
            name: "TenantId",
            table: "downloaded_song_mappings",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_downloaded_song_mapping_identity",
            table: "downloaded_song_mappings",
            columns: new[] { "ScopeKey", "ProviderId", "ExternalId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_downloaded_song_mapping_identity",
            table: "downloaded_song_mappings");

        migrationBuilder.DropColumn(name: "AudioQuality", table: "downloaded_song_mappings");
        migrationBuilder.DropColumn(name: "LibraryScopeId", table: "downloaded_song_mappings");
        migrationBuilder.DropColumn(name: "ProviderAccountId", table: "downloaded_song_mappings");
        migrationBuilder.DropColumn(name: "ScopeKey", table: "downloaded_song_mappings");
        migrationBuilder.DropColumn(name: "TenantId", table: "downloaded_song_mappings");

        migrationBuilder.CreateIndex(
            name: "IX_downloaded_song_mapping_identity",
            table: "downloaded_song_mappings",
            columns: new[] { "ProviderId", "ExternalId" },
            unique: true);
    }
}
