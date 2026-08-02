using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260802183000_AddPlaylistProjectionMode")]
public sealed class AddPlaylistProjectionMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "ProjectionMode",
        table: "playlist_links",
        type: "character varying(32)",
        maxLength: 32,
        nullable: false,
        defaultValue: "Resolved");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "ProjectionMode",
        table: "playlist_links");
}
