using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260721090000_AddPlaylistLinkEnabled")]
public sealed class AddPlaylistLinkEnabled : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var isPostgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        migrationBuilder.AddColumn<bool>(
            name: "Enabled",
            table: "playlist_links",
            type: isPostgres ? "boolean" : "INTEGER",
            nullable: false,
            defaultValue: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "Enabled", table: "playlist_links");
}
