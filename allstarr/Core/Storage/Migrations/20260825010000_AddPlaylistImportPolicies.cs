using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260825010000_AddPlaylistImportPolicies")]
public sealed class AddPlaylistImportPolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ImportMode",
            table: "playlist_links",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "Linked");

        migrationBuilder.AddColumn<string>(
            name: "TrackRetention",
            table: "playlist_links",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "OnDemand");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ImportMode", table: "playlist_links");
        migrationBuilder.DropColumn(name: "TrackRetention", table: "playlist_links");
    }
}
