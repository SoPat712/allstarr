using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class StandardizeTrackDurationMilliseconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_library_tracks_duration",
                table: "library_tracks");

            migrationBuilder.AlterColumn<long>(
                name: "DurationMilliseconds",
                table: "library_tracks",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "DurationProvenance",
                table: "library_tracks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DurationRetrievedAt",
                table: "library_tracks",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE library_tracks
                SET "DurationMilliseconds" = NULL
                WHERE "DurationMilliseconds" <= 0;

                UPDATE library_tracks
                SET "DurationProvenance" = "Protocol",
                    "DurationRetrievedAt" = "IndexedAt"
                WHERE "DurationMilliseconds" IS NOT NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_library_tracks_duration",
                table: "library_tracks",
                sql: "\"DurationMilliseconds\" IS NULL OR \"DurationMilliseconds\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_library_tracks_duration",
                table: "library_tracks");

            migrationBuilder.DropColumn(
                name: "DurationProvenance",
                table: "library_tracks");

            migrationBuilder.DropColumn(
                name: "DurationRetrievedAt",
                table: "library_tracks");

            migrationBuilder.AlterColumn<long>(
                name: "DurationMilliseconds",
                table: "library_tracks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_library_tracks_duration",
                table: "library_tracks",
                sql: "\"DurationMilliseconds\" >= 0");
        }
    }
}
