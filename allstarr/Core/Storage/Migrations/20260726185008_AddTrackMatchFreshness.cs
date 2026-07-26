using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackMatchFreshness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LibraryIndexRevision",
                table: "track_matches",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "MatcherVersion",
                table: "track_matches",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SourceSnapshotVersion",
                table: "track_matches",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LibraryIndexRevision",
                table: "track_matches");

            migrationBuilder.DropColumn(
                name: "MatcherVersion",
                table: "track_matches");

            migrationBuilder.DropColumn(
                name: "SourceSnapshotVersion",
                table: "track_matches");
        }
    }
}
