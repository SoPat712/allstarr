using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PublishPlaylistGenerationsAtomically : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PublishedAt",
                table: "playlist_source_snapshots",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedTrackMatchId",
                table: "playlist_source_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE playlist_source_entries AS entry
                SET "PublishedTrackMatchId" = (
                    SELECT match."Id"
                    FROM track_matches AS match
                    WHERE match."TenantId" = entry."TenantId"
                      AND match."ExternalSnapshotId" = entry."ExternalMetadataSnapshotId"
                    ORDER BY match."DecisionVersion" DESC, match."DecidedAt" DESC
                    LIMIT 1
                )
                WHERE EXISTS (
                    SELECT 1
                    FROM track_matches AS match
                    WHERE match."TenantId" = entry."TenantId"
                      AND match."ExternalSnapshotId" = entry."ExternalMetadataSnapshotId"
                );

                UPDATE playlist_source_snapshots AS snapshot
                SET "PublishedAt" = snapshot."RetrievedAt"
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM playlist_source_entries AS entry
                    WHERE entry."TenantId" = snapshot."TenantId"
                      AND entry."PlaylistSourceSnapshotId" = snapshot."Id"
                      AND entry."PublishedTrackMatchId" IS NULL
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_snapshot_published",
                table: "playlist_source_snapshots",
                columns: new[] { "TenantId", "PlaylistLinkId", "PublishedAt", "SnapshotVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_entries_TenantId_PublishedTrackMatchId",
                table: "playlist_source_entries",
                columns: new[] { "TenantId", "PublishedTrackMatchId" });

            migrationBuilder.AddForeignKey(
                name: "FK_PlaylistSourceEntry_TrackMatch",
                table: "playlist_source_entries",
                columns: new[] { "TenantId", "PublishedTrackMatchId" },
                principalTable: "track_matches",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlaylistSourceEntry_TrackMatch",
                table: "playlist_source_entries");

            migrationBuilder.DropIndex(
                name: "IX_playlist_snapshot_published",
                table: "playlist_source_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_playlist_source_entries_TenantId_PublishedTrackMatchId",
                table: "playlist_source_entries");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "playlist_source_snapshots");

            migrationBuilder.DropColumn(
                name: "PublishedTrackMatchId",
                table: "playlist_source_entries");
        }
    }
}
