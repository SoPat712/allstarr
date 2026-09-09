using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260825020000_EnforcePlaylistImportPolicies")]
public sealed class EnforcePlaylistImportPolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddCheckConstraint(
            name: "CK_playlist_links_import_mode",
            table: "playlist_links",
            sql: "\"ImportMode\" IN ('Linked', 'OneTime')");

        migrationBuilder.AddCheckConstraint(
            name: "CK_playlist_links_track_retention",
            table: "playlist_links",
            sql: "\"TrackRetention\" IN ('OnDemand', 'KeepAll')");

        migrationBuilder.AddCheckConstraint(
            name: "CK_playlist_links_one_time_schedule",
            table: "playlist_links",
            sql: "\"ImportMode\" <> 'OneTime' OR \"ScheduleId\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_playlist_links_import_mode",
            table: "playlist_links");

        migrationBuilder.DropCheckConstraint(
            name: "CK_playlist_links_track_retention",
            table: "playlist_links");

        migrationBuilder.DropCheckConstraint(
            name: "CK_playlist_links_one_time_schedule",
            table: "playlist_links");
    }
}
