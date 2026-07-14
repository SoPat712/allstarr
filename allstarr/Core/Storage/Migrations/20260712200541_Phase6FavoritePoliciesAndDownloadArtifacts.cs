using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public sealed partial class Phase6FavoritePoliciesAndDownloadArtifacts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var text = postgres ? "text" : "TEXT";
        var guid = postgres ? "uuid" : "TEXT";
        var bigint = postgres ? "bigint" : "INTEGER";
        var boolean = postgres ? "boolean" : "INTEGER";

        migrationBuilder.CreateTable("favorite_action_policies", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            TenantId = table.Column<Guid>(type: guid, nullable: false),
            OwnerUserId = table.Column<Guid>(type: guid, nullable: true),
            Scope = table.Column<string>(type: text, maxLength: 32, nullable: false),
            Protocol = table.Column<string>(type: text, maxLength: 32, nullable: false),
            BackendInstanceId = table.Column<string>(type: text, maxLength: 200, nullable: false),
            LibraryScopeId = table.Column<string>(type: text, maxLength: 300, nullable: true),
            AddToVirtualLiked = table.Column<bool>(type: boolean, nullable: true),
            MatchLocalLibrary = table.Column<bool>(type: boolean, nullable: true),
            AutoDownload = table.Column<bool>(type: boolean, nullable: true),
            EnrichMetadata = table.Column<bool>(type: boolean, nullable: true),
            PlaceManagedFile = table.Column<bool>(type: boolean, nullable: true),
            RefreshBackendLibrary = table.Column<bool>(type: boolean, nullable: true),
            TargetCredentialReferenceId = table.Column<Guid>(type: guid, nullable: true),
            UpdatedByUserId = table.Column<Guid>(type: guid, nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
            Revision = table.Column<long>(type: bigint, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_favorite_action_policies", item => item.Id);
            table.ForeignKey("FK_favorite_policy_tenant", item => item.TenantId, "tenants", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_favorite_policy_owner", item => new { item.TenantId, item.OwnerUserId }, "users", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_favorite_policy_actor", item => new { item.TenantId, item.UpdatedByUserId }, "users", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable("provider_download_workspaces", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            WorkspaceId = table.Column<string>(type: text, maxLength: 64, nullable: false),
            TenantId = table.Column<Guid>(type: guid, nullable: false),
            OwnerUserId = table.Column<Guid>(type: guid, nullable: true),
            LibraryScopeId = table.Column<string>(type: text, maxLength: 300, nullable: true),
            DurableJobId = table.Column<Guid>(type: guid, nullable: false),
            ProviderId = table.Column<string>(type: text, maxLength: 100, nullable: false),
            ProviderAccountId = table.Column<Guid>(type: guid, nullable: true),
            IdempotencyKey = table.Column<string>(type: text, maxLength: 300, nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
            CompletedAt = table.Column<DateTimeOffset>(type: bigint, nullable: true),
            Revision = table.Column<long>(type: bigint, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_provider_download_workspaces", item => item.Id);
            table.ForeignKey("FK_download_workspace_tenant", item => item.TenantId, "tenants", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_download_workspace_user", item => new { item.TenantId, item.OwnerUserId }, "users", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_download_workspace_job", item => item.DurableJobId, "durable_jobs", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_download_workspace_account", item => new { item.ProviderAccountId, item.ProviderId }, "provider_accounts", new[] { "Id", "ProviderId" }, onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable("provider_download_artifacts", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            WorkspaceRecordId = table.Column<Guid>(type: guid, nullable: false),
            WorkspaceId = table.Column<string>(type: text, maxLength: 64, nullable: false),
            TenantId = table.Column<Guid>(type: guid, nullable: false),
            OwnerUserId = table.Column<Guid>(type: guid, nullable: true),
            LibraryScopeId = table.Column<string>(type: text, maxLength: 300, nullable: true),
            DurableJobId = table.Column<Guid>(type: guid, nullable: false),
            ProviderId = table.Column<string>(type: text, maxLength: 100, nullable: false),
            ProviderAccountId = table.Column<Guid>(type: guid, nullable: true),
            ProviderArtifactId = table.Column<string>(type: text, maxLength: 500, nullable: false),
            RelativePath = table.Column<string>(type: text, maxLength: 1000, nullable: false),
            ContentSha256 = table.Column<string>(type: text, maxLength: 64, nullable: false),
            Length = table.Column<long>(type: bigint, nullable: false),
            State = table.Column<string>(type: text, maxLength: 32, nullable: false),
            ManagedFileId = table.Column<Guid>(type: guid, nullable: true),
            CreatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
            VerifiedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
            PlacedAt = table.Column<DateTimeOffset>(type: bigint, nullable: true),
            Revision = table.Column<long>(type: bigint, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_provider_download_artifacts", item => item.Id);
            table.CheckConstraint("CK_download_artifact_length", "\"Length\" > 0");
            table.CheckConstraint("CK_download_artifact_sha", "length(\"ContentSha256\") = 64");
            table.ForeignKey("FK_download_artifact_workspace", item => item.WorkspaceRecordId, "provider_download_workspaces", "Id", onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_download_artifact_managed_file", item => item.ManagedFileId, "managed_files", "Id", onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateIndex("IX_favorite_policy_actor", "favorite_action_policies", new[] { "TenantId", "UpdatedByUserId" });
        migrationBuilder.CreateIndex("IX_favorite_policy_credential_reference", "favorite_action_policies", "TargetCredentialReferenceId");
        migrationBuilder.CreateIndex("IX_favorite_policy_scope", "favorite_action_policies", new[] { "TenantId", "OwnerUserId", "Scope", "Protocol", "BackendInstanceId", "LibraryScopeId" }, unique: true);
        migrationBuilder.CreateIndex("IX_download_workspace_idempotency", "provider_download_workspaces", new[] { "TenantId", "DurableJobId", "ProviderId", "ProviderAccountId", "IdempotencyKey" }, unique: true);
        migrationBuilder.CreateIndex("IX_download_workspace_account", "provider_download_workspaces", new[] { "ProviderAccountId", "ProviderId" });
        migrationBuilder.CreateIndex("IX_download_workspace_user", "provider_download_workspaces", new[] { "TenantId", "OwnerUserId" });
        migrationBuilder.CreateIndex("IX_download_workspace_id", "provider_download_workspaces", "WorkspaceId", unique: true);
        migrationBuilder.CreateIndex("IX_download_artifact_identity", "provider_download_artifacts", new[] { "WorkspaceRecordId", "ProviderArtifactId" }, unique: true);
        migrationBuilder.CreateIndex("IX_download_artifact_job_provider", "provider_download_artifacts", new[] { "TenantId", "DurableJobId", "ProviderId" }, unique: true);
        migrationBuilder.CreateIndex("IX_download_artifact_managed_file", "provider_download_artifacts", "ManagedFileId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("favorite_action_policies");
        migrationBuilder.DropTable("provider_download_artifacts");
        migrationBuilder.DropTable("provider_download_workspaces");
    }
}
