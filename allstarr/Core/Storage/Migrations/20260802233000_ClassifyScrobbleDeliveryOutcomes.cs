using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260802233000_ClassifyScrobbleDeliveryOutcomes")]
public sealed class ClassifyScrobbleDeliveryOutcomes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn("CompletedAt", "playback_delivery_checkpoints", "UpdatedAt");
        migrationBuilder.AddColumn<string>("OccurrenceKey", "playback_delivery_checkpoints", "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("Kind", "playback_delivery_checkpoints", "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Completed");
        migrationBuilder.AddColumn<string>("State", "playback_delivery_checkpoints", "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Delivered");
        migrationBuilder.AddColumn<string>("ProviderCode", "playback_delivery_checkpoints", "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>("SafeMessage", "playback_delivery_checkpoints", "character varying(500)", maxLength: 500, nullable: true);
        migrationBuilder.AddColumn<string>("DetailsJson", "playback_delivery_checkpoints", "text", nullable: false, defaultValue: "{}");
        migrationBuilder.AddColumn<long>("RetryAfter", "playback_delivery_checkpoints", "bigint", nullable: true);
        migrationBuilder.AddColumn<bool>("RequiresReauthentication", "playback_delivery_checkpoints", "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddCheckConstraint("CK_playback_delivery_checkpoint_state", "playback_delivery_checkpoints",
            "\"State\" IN ('Delivered', 'Ignored', 'Retrying', 'PermanentFailure')");
        migrationBuilder.CreateIndex("IX_playback_delivery_occurrence_status", "playback_delivery_checkpoints",
            new[] { "TenantId", "OwnerUserId", "OccurrenceKey", "Kind" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_playback_delivery_occurrence_status", "playback_delivery_checkpoints");
        migrationBuilder.DropCheckConstraint("CK_playback_delivery_checkpoint_state", "playback_delivery_checkpoints");
        migrationBuilder.DropColumn("OccurrenceKey", "playback_delivery_checkpoints");
        migrationBuilder.DropColumn("Kind", "playback_delivery_checkpoints");
        migrationBuilder.DropColumn("State", "playback_delivery_checkpoints");
        migrationBuilder.DropColumn("ProviderCode", "playback_delivery_checkpoints");
        migrationBuilder.DropColumn("SafeMessage", "playback_delivery_checkpoints");
        migrationBuilder.DropColumn("DetailsJson", "playback_delivery_checkpoints");
        migrationBuilder.DropColumn("RetryAfter", "playback_delivery_checkpoints");
        migrationBuilder.DropColumn("RequiresReauthentication", "playback_delivery_checkpoints");
        migrationBuilder.RenameColumn("UpdatedAt", "playback_delivery_checkpoints", "CompletedAt");
    }
}
