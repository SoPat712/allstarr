using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class HouseholdKeptRecordingRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SourceAccountRevision",
                table: "provider_download_artifacts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceExternalTrackId",
                table: "provider_download_artifacts",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "recording_kept_routes",
                columns: table => new
                {
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ManagedFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublishedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicationJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recording_kept_routes", x => x.CanonicalRecordingId);
                    table.ForeignKey(
                        name: "FK_recording_kept_routes_canonical_recordings_CanonicalRecordingId",
                        column: x => x.CanonicalRecordingId,
                        principalTable: "canonical_recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recording_kept_routes_durable_jobs_PublicationJobId",
                        column: x => x.PublicationJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recording_kept_routes_managed_files_ManagedFileId",
                        column: x => x.ManagedFileId,
                        principalTable: "managed_files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recording_kept_routes_provider_download_artifacts_SourceArtifactId",
                        column: x => x.SourceArtifactId,
                        principalTable: "provider_download_artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recording_kept_routes_users_PublishedByUserId",
                        column: x => x.PublishedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recording_kept_routes_ManagedFileId",
                table: "recording_kept_routes",
                column: "ManagedFileId");

            migrationBuilder.CreateIndex(
                name: "IX_recording_kept_routes_PublicationJobId",
                table: "recording_kept_routes",
                column: "PublicationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_recording_kept_routes_PublishedByUserId",
                table: "recording_kept_routes",
                column: "PublishedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_recording_kept_routes_SourceArtifactId",
                table: "recording_kept_routes",
                column: "SourceArtifactId");

            const string validPublication = """
                EXISTS (SELECT 1 FROM managed_files f
                    JOIN durable_jobs j ON j."Id"=NEW."PublicationJobId"
                    JOIN provider_download_artifacts a ON a."Id"=NEW."SourceArtifactId"
                    JOIN provider_download_workspaces w ON w."Id"=a."WorkspaceRecordId"
                    WHERE f."Id"=NEW."ManagedFileId" AND f."OwnerUserId" IS NULL
                      AND f."SourceJobId" IS NULL AND f."IsManaged"=1 AND f."RemovedAt" IS NULL
                      AND f."ScopeKey"='managed:shared:' || lower(replace(f."RootId",'-',''))
                      AND j."OwnerUserId" IS NEW."PublishedByUserId"
                      AND a."OwnerUserId" IS NEW."PublishedByUserId"
                      AND a."DurableJobId"=j."Id" AND a."State"='Verified'
                      AND w."WorkspaceId"=a."WorkspaceId" AND w."OwnerUserId" IS a."OwnerUserId"
                      AND w."DurableJobId"=a."DurableJobId" AND w."ProviderId"=a."ProviderId"
                      AND w."ProviderAccountId" IS a."ProviderAccountId"
                      AND length(trim(a."SourceExternalTrackId"))>0
                      AND ((a."ProviderAccountId" IS NULL AND a."SourceAccountRevision" IS NULL)
                        OR (a."ProviderAccountId" IS NOT NULL AND a."SourceAccountRevision">=0)))
                """;
            foreach (var operation in new[] { "INSERT", "UPDATE" })
                migrationBuilder.Sql($"""
                    CREATE TRIGGER "TR_kept_publication_{operation.ToLowerInvariant()}"
                    BEFORE {operation} ON recording_kept_routes
                    WHEN NOT ({validPublication})
                    BEGIN SELECT RAISE(ABORT, 'CK_kept_publication_provenance'); END;
                    """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_kept_source_immutable" BEFORE UPDATE ON provider_download_artifacts
                WHEN EXISTS (SELECT 1 FROM recording_kept_routes r WHERE r."SourceArtifactId"=OLD."Id")
                  AND (NEW."OwnerUserId" IS NOT OLD."OwnerUserId"
                    OR NEW."DurableJobId" IS NOT OLD."DurableJobId"
                    OR NEW."ProviderId" IS NOT OLD."ProviderId"
                    OR NEW."ProviderAccountId" IS NOT OLD."ProviderAccountId"
                    OR NEW."SourceExternalTrackId" IS NOT OLD."SourceExternalTrackId"
                    OR NEW."SourceAccountRevision" IS NOT OLD."SourceAccountRevision"
                    OR NEW."WorkspaceRecordId" IS NOT OLD."WorkspaceRecordId"
                    OR NEW."WorkspaceId" IS NOT OLD."WorkspaceId"
                    OR NEW."RelativePath" IS NOT OLD."RelativePath"
                    OR NEW."ContentSha256" IS NOT OLD."ContentSha256"
                    OR NEW."Length" IS NOT OLD."Length"
                    OR NEW."MimeType" IS NOT OLD."MimeType")
                BEGIN SELECT RAISE(ABORT, 'CK_kept_source_immutable'); END;
                CREATE TRIGGER "TR_kept_workspace_immutable" BEFORE UPDATE ON provider_download_workspaces
                WHEN EXISTS (SELECT 1 FROM recording_kept_routes r
                    JOIN provider_download_artifacts a ON a."Id"=r."SourceArtifactId"
                    WHERE a."WorkspaceRecordId"=OLD."Id")
                  AND (NEW."WorkspaceId" IS NOT OLD."WorkspaceId" OR NEW."OwnerUserId" IS NOT OLD."OwnerUserId"
                    OR NEW."DurableJobId" IS NOT OLD."DurableJobId" OR NEW."ProviderId" IS NOT OLD."ProviderId"
                    OR NEW."ProviderAccountId" IS NOT OLD."ProviderAccountId")
                BEGIN SELECT RAISE(ABORT, 'CK_kept_workspace_immutable'); END;
                CREATE TRIGGER "TR_kept_file_immutable" BEFORE UPDATE ON managed_files
                WHEN EXISTS (SELECT 1 FROM recording_kept_routes r WHERE r."ManagedFileId"=OLD."Id")
                  AND (NEW."OwnerUserId" IS NOT NULL OR NEW."SourceJobId" IS NOT NULL
                    OR NEW."RemovedAt" IS NOT NULL OR NEW."IsManaged"<>1
                    OR NEW."RootId" IS NOT OLD."RootId" OR NEW."ScopeKey" IS NOT OLD."ScopeKey"
                    OR NEW."CanonicalPath" IS NOT OLD."CanonicalPath"
                    OR NEW."TargetRootPath" IS NOT OLD."TargetRootPath"
                    OR NEW."ContentSha256" IS NOT OLD."ContentSha256" OR NEW."Length" IS NOT OLD."Length")
                BEGIN SELECT RAISE(ABORT, 'CK_kept_file_immutable'); END;
                CREATE TRIGGER "TR_kept_job_owner" BEFORE UPDATE OF "OwnerUserId" ON durable_jobs
                WHEN EXISTS (SELECT 1 FROM recording_kept_routes r WHERE r."PublicationJobId"=OLD."Id"
                    AND r."PublishedByUserId" IS NOT NEW."OwnerUserId")
                BEGIN SELECT RAISE(ABORT, 'CK_kept_job_owner'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER "TR_kept_publication_insert";
                DROP TRIGGER "TR_kept_publication_update";
                DROP TRIGGER "TR_kept_source_immutable";
                DROP TRIGGER "TR_kept_file_immutable";
                DROP TRIGGER "TR_kept_workspace_immutable";
                DROP TRIGGER "TR_kept_job_owner";
                """);
            migrationBuilder.DropTable(name: "recording_kept_routes");
            // Native column removal preserves the existing ownership triggers.
            migrationBuilder.Sql("""
                ALTER TABLE provider_download_artifacts DROP COLUMN "SourceAccountRevision";
                ALTER TABLE provider_download_artifacts DROP COLUMN "SourceExternalTrackId";
                """);
        }
    }
}
