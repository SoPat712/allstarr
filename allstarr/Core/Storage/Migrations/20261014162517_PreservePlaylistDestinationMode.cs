using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PreservePlaylistDestinationMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationMode",
                table: "playlist_links",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Existing");

            migrationBuilder.Sql("""
                UPDATE playlist_links
                SET DestinationMode = 'New'
                WHERE TargetPlaylistId IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DestinationMode",
                table: "playlist_links");
        }
    }
}
