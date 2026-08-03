using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260802210000_Phase5AListeningOccurrences")]
public sealed class Phase5AListeningOccurrences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "listening_events",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                Protocol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                BackendInstanceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                LibraryScopeId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                OccurrenceKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                StartedAt = table.Column<long>(type: "bigint", nullable: true),
                ListenedAt = table.Column<long>(type: "bigint", nullable: true),
                UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                PositionTicks = table.Column<long>(type: "bigint", nullable: true),
                DurationMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                ClientClass = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                DeviceClass = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                SourceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ImportProvenance = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                TrackReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                Artist = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                Album = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CanonicalRecordingId = table.Column<Guid>(type: "uuid", nullable: true),
                LibraryTrackId = table.Column<Guid>(type: "uuid", nullable: true),
                ProviderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                ProviderAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                ProviderTrackIdentityId = table.Column<Guid>(type: "uuid", nullable: true),
                ProviderTrackReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_listening_events", item => item.Id);
                table.CheckConstraint("CK_listening_event_duration", "\"DurationMilliseconds\" IS NULL OR \"DurationMilliseconds\" > 0");
                table.CheckConstraint("CK_listening_event_position", "\"PositionTicks\" IS NULL OR \"PositionTicks\" >= 0");
                table.ForeignKey("FK_listening_event_canonical_recording",
                    item => new { item.TenantId, item.CanonicalRecordingId }, "canonical_recordings", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_listening_event_library_track",
                    item => new { item.TenantId, item.LibraryTrackId }, "library_tracks", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_listening_event_provider_account",
                    item => item.ProviderAccountId, "provider_accounts", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_listening_event_provider_identity",
                    item => item.ProviderTrackIdentityId, "provider_track_identities", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_listening_events_tenants_TenantId",
                    item => item.TenantId, "tenants", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_listening_events_users_TenantId_OwnerUserId",
                    item => new { item.TenantId, item.OwnerUserId }, "users", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_listening_events_ProviderAccountId", "listening_events", "ProviderAccountId");
        migrationBuilder.CreateIndex("IX_listening_events_ProviderTrackIdentityId", "listening_events", "ProviderTrackIdentityId");
        migrationBuilder.CreateIndex("IX_listening_events_TenantId_CanonicalRecordingId", "listening_events", new[] { "TenantId", "CanonicalRecordingId" });
        migrationBuilder.CreateIndex("IX_listening_events_TenantId_LibraryTrackId", "listening_events", new[] { "TenantId", "LibraryTrackId" });
        migrationBuilder.CreateIndex("IX_listening_event_occurrence", "listening_events", new[] { "TenantId", "OwnerUserId", "OccurrenceKey" }, unique: true);
        migrationBuilder.CreateIndex("IX_listening_event_scope_history", "listening_events", new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId", "ListenedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "listening_events");
}
