using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ScopeTrackMatchRejections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_manual_overrides_shape",
                table: "manual_track_overrides");

            migrationBuilder.AddColumn<string>(
                name: "MatcherVersion",
                table: "manual_track_overrides",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddCheckConstraint(
                name: "CK_manual_overrides_shape",
                table: "manual_track_overrides",
                sql: "(\"Decision\" = 'Pin' AND \"LibraryTrackId\" IS NOT NULL) OR \"Decision\" = 'Reject'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_manual_overrides_shape",
                table: "manual_track_overrides");

            migrationBuilder.DropColumn(
                name: "MatcherVersion",
                table: "manual_track_overrides");

            migrationBuilder.Sql(
                "UPDATE manual_track_overrides SET \"LibraryTrackId\" = NULL WHERE \"Decision\" = 'Reject'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_manual_overrides_shape",
                table: "manual_track_overrides",
                sql: "(\"Decision\" = 'Pin' AND \"LibraryTrackId\" IS NOT NULL) OR (\"Decision\" = 'Reject' AND \"LibraryTrackId\" IS NULL)");
        }
    }
}
