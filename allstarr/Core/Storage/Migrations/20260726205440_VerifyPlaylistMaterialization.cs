using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class VerifyPlaylistMaterialization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PlannedTargetDurationMilliseconds",
                table: "playlist_sync_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlannedTargetTrackCount",
                table: "playlist_sync_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationCode",
                table: "playlist_sync_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VerifiedAt",
                table: "playlist_sync_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VerifiedTargetDurationMilliseconds",
                table: "playlist_sync_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerifiedTargetTrackCount",
                table: "playlist_sync_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_playlist_sync_verification_counts",
                table: "playlist_sync_runs",
                sql: "(\"PlannedTargetTrackCount\" IS NULL OR \"PlannedTargetTrackCount\" >= 0) AND (\"VerifiedTargetTrackCount\" IS NULL OR \"VerifiedTargetTrackCount\" >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_playlist_sync_verification_durations",
                table: "playlist_sync_runs",
                sql: "(\"PlannedTargetDurationMilliseconds\" IS NULL OR \"PlannedTargetDurationMilliseconds\" >= 0) AND (\"VerifiedTargetDurationMilliseconds\" IS NULL OR \"VerifiedTargetDurationMilliseconds\" >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_playlist_sync_verification_counts",
                table: "playlist_sync_runs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_playlist_sync_verification_durations",
                table: "playlist_sync_runs");

            migrationBuilder.DropColumn(
                name: "PlannedTargetDurationMilliseconds",
                table: "playlist_sync_runs");

            migrationBuilder.DropColumn(
                name: "PlannedTargetTrackCount",
                table: "playlist_sync_runs");

            migrationBuilder.DropColumn(
                name: "VerificationCode",
                table: "playlist_sync_runs");

            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                table: "playlist_sync_runs");

            migrationBuilder.DropColumn(
                name: "VerifiedTargetDurationMilliseconds",
                table: "playlist_sync_runs");

            migrationBuilder.DropColumn(
                name: "VerifiedTargetTrackCount",
                table: "playlist_sync_runs");
        }
    }
}
