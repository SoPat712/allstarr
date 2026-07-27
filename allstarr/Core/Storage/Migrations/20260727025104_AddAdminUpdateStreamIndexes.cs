using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminUpdateStreamIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_TenantId",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_TenantId_CreatedAt",
                table: "audit_events");

            migrationBuilder.CreateIndex(
                name: "IX_track_match_updates",
                table: "track_matches",
                columns: new[] { "TenantId", "DecidedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_updates",
                table: "provider_health_samples",
                columns: new[] { "TenantId", "ObservedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_snapshot_updates",
                table: "playlist_source_snapshots",
                columns: new[] { "TenantId", "RetrievedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_updates",
                table: "outbox_messages",
                columns: new[] { "TenantId", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_durable_job_updates",
                table: "durable_jobs",
                columns: new[] { "TenantId", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_updates",
                table: "audit_events",
                columns: new[] { "TenantId", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_track_match_updates",
                table: "track_matches");

            migrationBuilder.DropIndex(
                name: "IX_provider_health_updates",
                table: "provider_health_samples");

            migrationBuilder.DropIndex(
                name: "IX_playlist_snapshot_updates",
                table: "playlist_source_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_outbox_updates",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "IX_durable_job_updates",
                table: "durable_jobs");

            migrationBuilder.DropIndex(
                name: "IX_audit_event_updates",
                table: "audit_events");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_TenantId",
                table: "outbox_messages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_TenantId_CreatedAt",
                table: "audit_events",
                columns: new[] { "TenantId", "CreatedAt" });
        }
    }
}
