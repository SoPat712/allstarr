using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public sealed partial class Phase6FavoritesManagedFilesEnrichment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var text = postgres ? "text" : "TEXT";
        var guid = postgres ? "uuid" : "TEXT";
        var integer = postgres ? "integer" : "INTEGER";
        var bigint = postgres ? "bigint" : "INTEGER";
        var boolean = postgres ? "boolean" : "INTEGER";

        migrationBuilder.CreateTable(name: "favorite_events", columns: table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false), TenantId = table.Column<Guid>(type: guid, nullable: false),
            OwnerUserId = table.Column<Guid>(type: guid, nullable: false), Protocol = table.Column<string>(type: text, maxLength: 32, nullable: false),
            BackendInstanceId = table.Column<string>(type: text, maxLength: 200, nullable: false), BackendPrincipalId = table.Column<string>(type: text, maxLength: 300, nullable: false),
            LibraryScopeId = table.Column<string>(type: text, maxLength: 300, nullable: true), ItemId = table.Column<string>(type: text, maxLength: 500, nullable: false),
            Operation = table.Column<string>(type: text, maxLength: 32, nullable: false), SourceRevision = table.Column<string>(type: text, maxLength: 300, nullable: false),
            EventKey = table.Column<string>(type: text, maxLength: 64, nullable: false), CorrelationId = table.Column<string>(type: text, maxLength: 100, nullable: false),
            PolicySnapshotJson = table.Column<string>(type: text, nullable: false), TargetCredentialReferenceId = table.Column<Guid>(type: guid, nullable: true),
            JobId = table.Column<Guid>(type: guid, nullable: false), State = table.Column<string>(type: text, maxLength: 32, nullable: false),
            LastErrorCode = table.Column<string>(type: text, maxLength: 100, nullable: true), LastErrorMessage = table.Column<string>(type: text, maxLength: 1000, nullable: true),
            CreatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false), UpdatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
            CompletedAt = table.Column<DateTimeOffset>(type: bigint, nullable: true), Revision = table.Column<long>(type: bigint, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_favorite_events", item => item.Id);
            table.UniqueConstraint("AK_favorite_event_owner", item => new { item.Id, item.TenantId, item.OwnerUserId });
            table.ForeignKey("FK_favorite_event_job", item => item.JobId, "durable_jobs", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_favorite_event_tenant", item => item.TenantId, "tenants", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_favorite_event_user", item => new { item.TenantId, item.OwnerUserId }, "users", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable(name: "managed_files", columns: table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false), RootId = table.Column<Guid>(type: guid, nullable: false),
            TargetRootPath = table.Column<string>(type: text, maxLength: 2000, nullable: false), CanonicalPath = table.Column<string>(type: text, maxLength: 2000, nullable: false),
            ContentSha256 = table.Column<string>(type: text, maxLength: 64, nullable: false), Length = table.Column<long>(type: bigint, nullable: false),
            PlacementMethod = table.Column<string>(type: text, maxLength: 32, nullable: false), TenantId = table.Column<Guid>(type: guid, nullable: false),
            OwnerUserId = table.Column<Guid>(type: guid, nullable: true), LibraryScopeId = table.Column<string>(type: text, maxLength: 300, nullable: true),
            SourceJobId = table.Column<Guid>(type: guid, nullable: true), ScopeKey = table.Column<string>(type: text, maxLength: 1000, nullable: false),
            ReferenceCount = table.Column<int>(type: integer, nullable: false), IsManaged = table.Column<bool>(type: boolean, nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false), RemovedAt = table.Column<DateTimeOffset>(type: bigint, nullable: true),
            Revision = table.Column<long>(type: bigint, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_managed_files", item => item.Id);
            table.UniqueConstraint("AK_managed_file_tenant", item => new { item.TenantId, item.Id });
            table.CheckConstraint("CK_managed_files_sha256", "length(\"ContentSha256\") = 64");
            table.CheckConstraint("CK_managed_files_references", "\"ReferenceCount\" >= 0");
            table.CheckConstraint("CK_managed_files_owned", "\"IsManaged\" = TRUE");
            table.ForeignKey("FK_managed_file_tenant", item => item.TenantId, "tenants", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_managed_file_user", item => new { item.TenantId, item.OwnerUserId }, "users", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_managed_file_job", item => item.SourceJobId, "durable_jobs", "Id", onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable(name: "favorite_actions", columns: table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false), EventId = table.Column<Guid>(type: guid, nullable: false),
            TenantId = table.Column<Guid>(type: guid, nullable: false), OwnerUserId = table.Column<Guid>(type: guid, nullable: false),
            ActionType = table.Column<string>(type: text, maxLength: 100, nullable: false), IdempotencyKey = table.Column<string>(type: text, maxLength: 300, nullable: false),
            Reversible = table.Column<bool>(type: boolean, nullable: false), State = table.Column<string>(type: text, maxLength: 32, nullable: false),
            AttemptCount = table.Column<int>(type: integer, nullable: false), LastErrorCode = table.Column<string>(type: text, maxLength: 100, nullable: true),
            LastErrorMessage = table.Column<string>(type: text, maxLength: 1000, nullable: true), CreatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false), CompletedAt = table.Column<DateTimeOffset>(type: bigint, nullable: true),
            Revision = table.Column<long>(type: bigint, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_favorite_actions", item => item.Id);
            table.ForeignKey("FK_favorite_action_event", item => new { item.EventId, item.TenantId, item.OwnerUserId }, "favorite_events", new[] { "Id", "TenantId", "OwnerUserId" }, onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateTable(name: "favorite_states", columns: table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false), TenantId = table.Column<Guid>(type: guid, nullable: false),
            OwnerUserId = table.Column<Guid>(type: guid, nullable: false), Protocol = table.Column<string>(type: text, maxLength: 32, nullable: false),
            BackendInstanceId = table.Column<string>(type: text, maxLength: 200, nullable: false), ItemId = table.Column<string>(type: text, maxLength: 500, nullable: false),
            IsFavorite = table.Column<bool>(type: boolean, nullable: false), LastEventId = table.Column<Guid>(type: guid, nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false), Revision = table.Column<long>(type: bigint, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_favorite_states", item => item.Id);
            table.ForeignKey("FK_favorite_state_user", item => new { item.TenantId, item.OwnerUserId }, "users", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_favorite_state_event", item => new { item.LastEventId, item.TenantId, item.OwnerUserId }, "favorite_events", new[] { "Id", "TenantId", "OwnerUserId" }, onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable(name: "metadata_enrichment_plans", columns: table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false), TenantId = table.Column<Guid>(type: guid, nullable: false),
            OwnerUserId = table.Column<Guid>(type: guid, nullable: false), LineageJobId = table.Column<Guid>(type: guid, nullable: false),
            ManagedArtifactId = table.Column<Guid>(type: guid, nullable: false), Fingerprint = table.Column<string>(type: text, maxLength: 64, nullable: false),
            PlanVersion = table.Column<int>(type: integer, nullable: false), SourceRevisionsJson = table.Column<string>(type: text, nullable: false),
            DecisionsJson = table.Column<string>(type: text, nullable: false), TagsJson = table.Column<string>(type: text, nullable: false),
            PathValuesJson = table.Column<string>(type: text, nullable: false), CreatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_enrichment_plans", item => item.Id);
            table.UniqueConstraint("AK_enrichment_plan_scope", item => new { item.Id, item.TenantId, item.OwnerUserId, item.ManagedArtifactId, item.LineageJobId });
            table.CheckConstraint("CK_enrichment_plans_fingerprint", "length(\"Fingerprint\") = 64");
            table.ForeignKey("FK_enrichment_plan_tenant", item => item.TenantId, "tenants", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_enrichment_plan_user", item => new { item.TenantId, item.OwnerUserId }, "users", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_enrichment_plan_job", item => item.LineageJobId, "durable_jobs", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_enrichment_plan_file", item => item.ManagedArtifactId, "managed_files", "Id", onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable(name: "metadata_enrichment_applications", columns: table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false), TenantId = table.Column<Guid>(type: guid, nullable: false),
            OwnerUserId = table.Column<Guid>(type: guid, nullable: false), PlanId = table.Column<Guid>(type: guid, nullable: false),
            ManagedArtifactId = table.Column<Guid>(type: guid, nullable: false), LineageJobId = table.Column<Guid>(type: guid, nullable: false),
            ArtifactContentSha256 = table.Column<string>(type: text, maxLength: 64, nullable: false), State = table.Column<string>(type: text, maxLength: 32, nullable: false),
            ErrorCode = table.Column<string>(type: text, maxLength: 100, nullable: true), SafeErrorMessage = table.Column<string>(type: text, maxLength: 1000, nullable: true),
            CreatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false), UpdatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
            Revision = table.Column<long>(type: bigint, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_enrichment_applications", item => item.Id);
            table.CheckConstraint("CK_enrichment_applications_sha256", "length(\"ArtifactContentSha256\") = 64");
            table.ForeignKey("FK_enrichment_application_user", item => new { item.TenantId, item.OwnerUserId }, "users", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_enrichment_application_job", item => item.LineageJobId, "durable_jobs", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_enrichment_application_plan", item => new { item.PlanId, item.TenantId, item.OwnerUserId, item.ManagedArtifactId, item.LineageJobId }, "metadata_enrichment_plans", new[] { "Id", "TenantId", "OwnerUserId", "ManagedArtifactId", "LineageJobId" }, onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateIndex("IX_favorite_event_key", "favorite_events", "EventKey", unique: true);
        migrationBuilder.CreateIndex("IX_favorite_event_owner_created", "favorite_events", new[] { "TenantId", "OwnerUserId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_favorite_event_job", "favorite_events", "JobId");
        migrationBuilder.CreateIndex("IX_favorite_event_credential_reference", "favorite_events", "TargetCredentialReferenceId");
        migrationBuilder.CreateIndex("IX_favorite_action_type", "favorite_actions", new[] { "EventId", "ActionType" }, unique: true);
        migrationBuilder.CreateIndex("IX_favorite_action_owner_state", "favorite_actions", new[] { "TenantId", "OwnerUserId", "State" });
        migrationBuilder.CreateIndex("IX_favorite_action_event", "favorite_actions", new[] { "EventId", "TenantId", "OwnerUserId" });
        migrationBuilder.CreateIndex("IX_favorite_state_owner_target", "favorite_states", new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "ItemId" }, unique: true);
        migrationBuilder.CreateIndex("IX_favorite_state_event", "favorite_states", new[] { "LastEventId", "TenantId", "OwnerUserId" });
        migrationBuilder.CreateIndex("IX_managed_file_path", "managed_files", "CanonicalPath", unique: true);
        migrationBuilder.CreateIndex("IX_managed_file_fingerprint", "managed_files", new[] { "RootId", "ContentSha256", "ScopeKey" });
        migrationBuilder.CreateIndex("IX_managed_file_user", "managed_files", new[] { "TenantId", "OwnerUserId" });
        migrationBuilder.CreateIndex("IX_managed_file_job", "managed_files", "SourceJobId");
        migrationBuilder.CreateIndex("IX_enrichment_plan_fingerprint", "metadata_enrichment_plans", new[] { "TenantId", "OwnerUserId", "ManagedArtifactId", "Fingerprint" }, unique: true);
        migrationBuilder.CreateIndex("IX_enrichment_plan_job", "metadata_enrichment_plans", "LineageJobId");
        migrationBuilder.CreateIndex("IX_enrichment_plan_file", "metadata_enrichment_plans", "ManagedArtifactId");
        migrationBuilder.CreateIndex("IX_enrichment_application_hash", "metadata_enrichment_applications", new[] { "TenantId", "OwnerUserId", "PlanId", "ManagedArtifactId", "ArtifactContentSha256" }, unique: true);
        migrationBuilder.CreateIndex("IX_enrichment_application_job", "metadata_enrichment_applications", "LineageJobId");
        migrationBuilder.CreateIndex("IX_enrichment_application_plan", "metadata_enrichment_applications", new[] { "PlanId", "TenantId", "OwnerUserId", "ManagedArtifactId", "LineageJobId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("favorite_actions");
        migrationBuilder.DropTable("favorite_states");
        migrationBuilder.DropTable("metadata_enrichment_applications");
        migrationBuilder.DropTable("favorite_events");
        migrationBuilder.DropTable("metadata_enrichment_plans");
        migrationBuilder.DropTable("managed_files");
    }
}
