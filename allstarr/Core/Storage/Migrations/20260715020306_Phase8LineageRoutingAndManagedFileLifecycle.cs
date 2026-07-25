using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Phase8LineageRoutingAndManagedFileLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_durable_jobs_users_OwnerUserId",
                table: "durable_jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_provider_accounts_users_OwnerUserId",
                table: "provider_accounts");

            migrationBuilder.DropIndex(
                name: "IX_provider_accounts_OwnerUserId",
                table: "provider_accounts");

            migrationBuilder.DropIndex(
                name: "IX_provider_accounts_TenantId",
                table: "provider_accounts");

            migrationBuilder.DropIndex(
                name: "IX_durable_jobs_OwnerUserId",
                table: "durable_jobs");

            migrationBuilder.DropIndex(
                name: "IX_durable_jobs_TenantId",
                table: "durable_jobs");

            migrationBuilder.AddColumn<string>(
                name: "FileSystemDeviceId",
                table: "managed_files",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileSystemFileId",
                table: "managed_files",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "FileSystemLinkCount",
                table: "managed_files",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "managed_file_references",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    ManagedFileId = table.Column<Guid>(nullable: false),
                    TenantId = table.Column<Guid>(nullable: false),
                    OwnerUserId = table.Column<Guid>(nullable: true),
                    ScopeKey = table.Column<string>(maxLength: 1000, nullable: false),
                    ReferenceKey = table.Column<string>(maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<long>(nullable: false),
                    ReleasedAt = table.Column<long>(nullable: true),
                    Revision = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_file_references", x => x.Id);
                    table.ForeignKey(
                        name: "FK_managed_file_reference_tenant_file",
                        columns: x => new { x.TenantId, x.ManagedFileId },
                        principalTable: "managed_files",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_file_reference_tenant_user",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_route_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    TenantId = table.Column<Guid>(nullable: false),
                    ActorUserId = table.Column<Guid>(nullable: true),
                    DurableJobId = table.Column<Guid>(nullable: true),
                    RouteKey = table.Column<string>(maxLength: 64, nullable: false),
                    OperationId = table.Column<string>(maxLength: 100, nullable: false),
                    CorrelationId = table.Column<string>(maxLength: 100, nullable: false),
                    Capability = table.Column<string>(maxLength: 100, nullable: false),
                    LibraryScopeId = table.Column<string>(maxLength: 300, nullable: true),
                    SelectedProviderId = table.Column<string>(maxLength: 100, nullable: true),
                    SelectedProviderAccountId = table.Column<Guid>(nullable: true),
                    CandidateDecisionsJson = table.Column<string>(nullable: false),
                    CreatedAt = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_route_decisions", x => x.Id);
                    table.UniqueConstraint("AK_provider_route_decisions_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.ForeignKey(
                        name: "FK_provider_route_decision_account",
                        columns: x => new { x.SelectedProviderAccountId, x.SelectedProviderId },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "Id", "ProviderId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_route_decision_actor",
                        columns: x => new { x.TenantId, x.ActorUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_route_decision_job",
                        column: x => x.DurableJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_route_decisions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_route_outcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    TenantId = table.Column<Guid>(nullable: false),
                    RouteDecisionId = table.Column<Guid>(nullable: false),
                    OutcomeKey = table.Column<string>(maxLength: 64, nullable: false),
                    Sequence = table.Column<int>(nullable: false),
                    Stage = table.Column<string>(maxLength: 50, nullable: false),
                    ProviderId = table.Column<string>(maxLength: 100, nullable: true),
                    ProviderAccountId = table.Column<Guid>(nullable: true),
                    Status = table.Column<string>(maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(maxLength: 100, nullable: false),
                    NextProviderId = table.Column<string>(maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_route_outcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_route_outcome_account",
                        columns: x => new { x.ProviderAccountId, x.ProviderId },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "Id", "ProviderId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_route_outcome_decision",
                        columns: x => new { x.RouteDecisionId, x.TenantId },
                        principalTable: "provider_route_decisions",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_provider_route_outcomes_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_TenantId_OwnerUserId",
                table: "provider_accounts",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "UX_managed_file_owner_lineage",
                table: "managed_files",
                columns: new[] { "Id", "TenantId", "OwnerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_TenantId_OwnerUserId",
                table: "durable_jobs",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "UX_durable_job_owner_lineage",
                table: "durable_jobs",
                columns: new[] { "Id", "TenantId", "OwnerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_durable_job_tenant_lineage",
                table: "durable_jobs",
                columns: new[] { "Id", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_file_reference_key",
                table: "managed_file_references",
                columns: new[] { "ManagedFileId", "ReferenceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_file_reference_owner",
                table: "managed_file_references",
                columns: new[] { "TenantId", "OwnerUserId", "ReleasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_file_references_TenantId_ManagedFileId",
                table: "managed_file_references",
                columns: new[] { "TenantId", "ManagedFileId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_decision_correlation",
                table: "provider_route_decisions",
                columns: new[] { "TenantId", "CorrelationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_decision_key",
                table: "provider_route_decisions",
                columns: new[] { "TenantId", "RouteKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_decisions_DurableJobId",
                table: "provider_route_decisions",
                column: "DurableJobId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_decisions_SelectedProviderAccountId_SelectedProviderId",
                table: "provider_route_decisions",
                columns: new[] { "SelectedProviderAccountId", "SelectedProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_decisions_TenantId_ActorUserId",
                table: "provider_route_decisions",
                columns: new[] { "TenantId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_outcome_key",
                table: "provider_route_outcomes",
                columns: new[] { "RouteDecisionId", "OutcomeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_outcome_tenant_created",
                table: "provider_route_outcomes",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_outcomes_ProviderAccountId_ProviderId",
                table: "provider_route_outcomes",
                columns: new[] { "ProviderAccountId", "ProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_route_outcomes_RouteDecisionId_TenantId",
                table: "provider_route_outcomes",
                columns: new[] { "RouteDecisionId", "TenantId" });

            migrationBuilder.AddForeignKey(
                name: "FK_durable_job_tenant_owner",
                table: "durable_jobs",
                columns: new[] { "TenantId", "OwnerUserId" },
                principalTable: "users",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_provider_account_tenant_owner",
                table: "provider_accounts",
                columns: new[] { "TenantId", "OwnerUserId" },
                principalTable: "users",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            ApplyPostgresLineageEnforcement(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RemovePostgresLineageEnforcement(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "FK_durable_job_tenant_owner",
                table: "durable_jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_provider_account_tenant_owner",
                table: "provider_accounts");

            migrationBuilder.DropTable(
                name: "managed_file_references");

            migrationBuilder.DropTable(
                name: "provider_route_outcomes");

            migrationBuilder.DropTable(
                name: "provider_route_decisions");

            migrationBuilder.DropIndex(
                name: "IX_provider_accounts_TenantId_OwnerUserId",
                table: "provider_accounts");

            migrationBuilder.DropIndex(
                name: "UX_managed_file_owner_lineage",
                table: "managed_files");

            migrationBuilder.DropIndex(
                name: "IX_durable_jobs_TenantId_OwnerUserId",
                table: "durable_jobs");

            migrationBuilder.DropIndex(
                name: "UX_durable_job_owner_lineage",
                table: "durable_jobs");

            migrationBuilder.DropIndex(
                name: "UX_durable_job_tenant_lineage",
                table: "durable_jobs");

            migrationBuilder.DropColumn(
                name: "FileSystemDeviceId",
                table: "managed_files");

            migrationBuilder.DropColumn(
                name: "FileSystemFileId",
                table: "managed_files");

            migrationBuilder.DropColumn(
                name: "FileSystemLinkCount",
                table: "managed_files");

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_OwnerUserId",
                table: "provider_accounts",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_TenantId",
                table: "provider_accounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_OwnerUserId",
                table: "durable_jobs",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_TenantId",
                table: "durable_jobs",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_durable_jobs_users_OwnerUserId",
                table: "durable_jobs",
                column: "OwnerUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_provider_accounts_users_OwnerUserId",
                table: "provider_accounts",
                column: "OwnerUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        private static void ApplyPostgresLineageEnforcement(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "managed_file_references"
                    ("Id", "ManagedFileId", "TenantId", "OwnerUserId", "ScopeKey", "ReferenceKey", "CreatedAt", "ReleasedAt", "Revision")
                SELECT (substr(digest,1,8)||'-'||substr(digest,9,4)||'-'||substr(digest,13,4)||'-'||substr(digest,17,4)||'-'||substr(digest,21,12))::uuid,
                       file_id, tenant_id, owner_user_id, scope_key, 'legacy:' || ordinal::text, created_at, NULL, 1
                FROM (
                    SELECT file."Id" file_id, file."TenantId" tenant_id, file."OwnerUserId" owner_user_id,
                           file."ScopeKey" scope_key, file."CreatedAt" created_at, ordinal,
                           md5(file."Id"::text || ':legacy-reference:' || ordinal::text) digest
                    FROM "managed_files" file
                    CROSS JOIN LATERAL generate_series(1, file."ReferenceCount") ordinal
                    WHERE file."ReferenceCount" > 0
                ) legacy
                ON CONFLICT DO NOTHING;

                CREATE FUNCTION allstarr_account_scope_matches(uuid, uuid, uuid, text)
                RETURNS boolean LANGUAGE sql STABLE AS $fn$
                    SELECT $1 IS NULL OR EXISTS (
                        SELECT 1 FROM "provider_accounts" a WHERE a."Id" = $1 AND (
                            (a."Scope" = 'Global' AND a."TenantId" IS NULL AND a."OwnerUserId" IS NULL AND a."LibraryScopeId" IS NULL)
                            OR (a."Scope" = 'User' AND $2 IS NOT NULL AND $3 IS NOT NULL AND a."TenantId" = $2 AND a."OwnerUserId" = $3)
                            OR (a."Scope" = 'Library' AND $2 IS NOT NULL AND $4 IS NOT NULL AND a."TenantId" = $2 AND a."OwnerUserId" IS NULL AND a."LibraryScopeId" = $4)
                        )
                    )
                $fn$;

                DO $check$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "managed_files" c JOIN "durable_jobs" j ON j."Id"=c."SourceJobId"
                        WHERE c."SourceJobId" IS NOT NULL AND (j."TenantId" IS DISTINCT FROM c."TenantId" OR
                        (c."OwnerUserId" IS NOT NULL AND j."OwnerUserId" IS DISTINCT FROM c."OwnerUserId")))
                    THEN RAISE EXCEPTION 'FK_managed_file_job_tenant_lineage'; END IF;
                    IF EXISTS (SELECT 1 FROM "favorite_events" c JOIN "durable_jobs" j ON j."Id"=c."JobId"
                        WHERE j."TenantId" IS DISTINCT FROM c."TenantId" OR j."OwnerUserId" IS DISTINCT FROM c."OwnerUserId")
                    THEN RAISE EXCEPTION 'FK_favorite_event_job_lineage'; END IF;
                    IF EXISTS (SELECT 1 FROM "provider_download_workspaces" c JOIN "durable_jobs" j ON j."Id"=c."DurableJobId"
                        WHERE j."TenantId" IS DISTINCT FROM c."TenantId" OR
                        (c."OwnerUserId" IS NOT NULL AND j."OwnerUserId" IS DISTINCT FROM c."OwnerUserId"))
                    THEN RAISE EXCEPTION 'FK_download_workspace_job_tenant_lineage'; END IF;
                    IF EXISTS (SELECT 1 FROM "metadata_enrichment_plans" c JOIN "durable_jobs" j ON j."Id"=c."LineageJobId"
                        JOIN "managed_files" f ON f."Id"=c."ManagedArtifactId" WHERE
                        j."TenantId" IS DISTINCT FROM c."TenantId" OR j."OwnerUserId" IS DISTINCT FROM c."OwnerUserId" OR
                        f."TenantId" IS DISTINCT FROM c."TenantId" OR f."OwnerUserId" IS DISTINCT FROM c."OwnerUserId")
                    THEN RAISE EXCEPTION 'FK_enrichment_plan_lineage'; END IF;
                    IF EXISTS (SELECT 1 FROM "metadata_enrichment_applications" c JOIN "durable_jobs" j ON j."Id"=c."LineageJobId"
                        WHERE j."TenantId" IS DISTINCT FROM c."TenantId" OR j."OwnerUserId" IS DISTINCT FROM c."OwnerUserId")
                    THEN RAISE EXCEPTION 'FK_enrichment_application_job_lineage'; END IF;
                    IF EXISTS (SELECT 1 FROM "provider_route_decisions" c JOIN "durable_jobs" j ON j."Id"=c."DurableJobId"
                        WHERE c."DurableJobId" IS NOT NULL AND (j."TenantId" IS DISTINCT FROM c."TenantId" OR
                        (c."ActorUserId" IS NOT NULL AND j."OwnerUserId" IS DISTINCT FROM c."ActorUserId")))
                    THEN RAISE EXCEPTION 'FK_provider_route_decision_job_tenant_lineage'; END IF;
                    IF EXISTS (SELECT 1 FROM "durable_jobs" c WHERE NOT allstarr_account_scope_matches(c."ProviderAccountId",c."TenantId",c."OwnerUserId",c."LibraryScopeId"))
                    THEN RAISE EXCEPTION 'CK_durable_job_account_scope'; END IF;
                    IF EXISTS (SELECT 1 FROM "provider_route_decisions" c WHERE NOT allstarr_account_scope_matches(c."SelectedProviderAccountId",c."TenantId",c."ActorUserId",c."LibraryScopeId"))
                    THEN RAISE EXCEPTION 'CK_provider_route_decision_account_scope'; END IF;
                    IF EXISTS (SELECT 1 FROM "provider_route_outcomes" c JOIN "provider_route_decisions" d ON d."Id"=c."RouteDecisionId" AND d."TenantId"=c."TenantId"
                        WHERE NOT allstarr_account_scope_matches(c."ProviderAccountId",d."TenantId",d."ActorUserId",d."LibraryScopeId"))
                    THEN RAISE EXCEPTION 'CK_provider_route_outcome_account_scope'; END IF;
                END $check$;

                ALTER TABLE "managed_files" ADD CONSTRAINT "FK_managed_file_job_tenant_lineage" FOREIGN KEY ("SourceJobId","TenantId") REFERENCES "durable_jobs" ("Id","TenantId") NOT VALID;
                ALTER TABLE "managed_files" ADD CONSTRAINT "FK_managed_file_job_owner_lineage" FOREIGN KEY ("SourceJobId","TenantId","OwnerUserId") REFERENCES "durable_jobs" ("Id","TenantId","OwnerUserId") NOT VALID;
                ALTER TABLE "favorite_events" ADD CONSTRAINT "FK_favorite_event_job_lineage" FOREIGN KEY ("JobId","TenantId","OwnerUserId") REFERENCES "durable_jobs" ("Id","TenantId","OwnerUserId") NOT VALID;
                ALTER TABLE "provider_download_workspaces" ADD CONSTRAINT "FK_download_workspace_job_tenant_lineage" FOREIGN KEY ("DurableJobId","TenantId") REFERENCES "durable_jobs" ("Id","TenantId") NOT VALID;
                ALTER TABLE "provider_download_workspaces" ADD CONSTRAINT "FK_download_workspace_job_owner_lineage" FOREIGN KEY ("DurableJobId","TenantId","OwnerUserId") REFERENCES "durable_jobs" ("Id","TenantId","OwnerUserId") NOT VALID;
                ALTER TABLE "metadata_enrichment_plans" ADD CONSTRAINT "FK_enrichment_plan_job_lineage" FOREIGN KEY ("LineageJobId","TenantId","OwnerUserId") REFERENCES "durable_jobs" ("Id","TenantId","OwnerUserId") NOT VALID;
                ALTER TABLE "metadata_enrichment_plans" ADD CONSTRAINT "FK_enrichment_plan_file_lineage" FOREIGN KEY ("ManagedArtifactId","TenantId","OwnerUserId") REFERENCES "managed_files" ("Id","TenantId","OwnerUserId") NOT VALID;
                ALTER TABLE "metadata_enrichment_applications" ADD CONSTRAINT "FK_enrichment_application_job_lineage" FOREIGN KEY ("LineageJobId","TenantId","OwnerUserId") REFERENCES "durable_jobs" ("Id","TenantId","OwnerUserId") NOT VALID;
                ALTER TABLE "provider_route_decisions" ADD CONSTRAINT "FK_provider_route_decision_job_tenant_lineage" FOREIGN KEY ("DurableJobId","TenantId") REFERENCES "durable_jobs" ("Id","TenantId") NOT VALID;
                ALTER TABLE "provider_route_decisions" ADD CONSTRAINT "FK_provider_route_decision_job_owner_lineage" FOREIGN KEY ("DurableJobId","TenantId","ActorUserId") REFERENCES "durable_jobs" ("Id","TenantId","OwnerUserId") NOT VALID;

                ALTER TABLE "managed_files" VALIDATE CONSTRAINT "FK_managed_file_job_tenant_lineage";
                ALTER TABLE "managed_files" VALIDATE CONSTRAINT "FK_managed_file_job_owner_lineage";
                ALTER TABLE "favorite_events" VALIDATE CONSTRAINT "FK_favorite_event_job_lineage";
                ALTER TABLE "provider_download_workspaces" VALIDATE CONSTRAINT "FK_download_workspace_job_tenant_lineage";
                ALTER TABLE "provider_download_workspaces" VALIDATE CONSTRAINT "FK_download_workspace_job_owner_lineage";
                ALTER TABLE "metadata_enrichment_plans" VALIDATE CONSTRAINT "FK_enrichment_plan_job_lineage";
                ALTER TABLE "metadata_enrichment_plans" VALIDATE CONSTRAINT "FK_enrichment_plan_file_lineage";
                ALTER TABLE "metadata_enrichment_applications" VALIDATE CONSTRAINT "FK_enrichment_application_job_lineage";
                ALTER TABLE "provider_route_decisions" VALIDATE CONSTRAINT "FK_provider_route_decision_job_tenant_lineage";
                ALTER TABLE "provider_route_decisions" VALIDATE CONSTRAINT "FK_provider_route_decision_job_owner_lineage";
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION allstarr_validate_job_account_scope() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN IF NOT allstarr_account_scope_matches(NEW."ProviderAccountId",NEW."TenantId",NEW."OwnerUserId",NEW."LibraryScopeId")
                    THEN RAISE EXCEPTION 'CK_durable_job_account_scope' USING ERRCODE='23503'; END IF; RETURN NEW; END $fn$;
                CREATE FUNCTION allstarr_validate_route_decision_account_scope() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN IF NOT allstarr_account_scope_matches(NEW."SelectedProviderAccountId",NEW."TenantId",NEW."ActorUserId",NEW."LibraryScopeId")
                    THEN RAISE EXCEPTION 'CK_provider_route_decision_account_scope' USING ERRCODE='23503'; END IF; RETURN NEW; END $fn$;
                CREATE FUNCTION allstarr_validate_route_outcome_account_scope() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE d "provider_route_decisions"%ROWTYPE; BEGIN SELECT * INTO d FROM "provider_route_decisions" WHERE "Id"=NEW."RouteDecisionId" AND "TenantId"=NEW."TenantId";
                    IF NOT FOUND OR NOT allstarr_account_scope_matches(NEW."ProviderAccountId",d."TenantId",d."ActorUserId",d."LibraryScopeId")
                    THEN RAISE EXCEPTION 'CK_provider_route_outcome_account_scope' USING ERRCODE='23503'; END IF; RETURN NEW; END $fn$;
                CREATE FUNCTION allstarr_validate_managed_file_reference_lineage() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN IF NOT EXISTS (SELECT 1 FROM "managed_files" f WHERE f."Id"=NEW."ManagedFileId" AND f."TenantId"=NEW."TenantId"
                    AND f."OwnerUserId" IS NOT DISTINCT FROM NEW."OwnerUserId" AND f."ScopeKey"=NEW."ScopeKey")
                    THEN RAISE EXCEPTION 'FK_managed_file_reference_lineage' USING ERRCODE='23503'; END IF; RETURN NEW; END $fn$;
                CREATE FUNCTION allstarr_guard_managed_file_reference_lineage() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN IF EXISTS (SELECT 1 FROM "managed_file_references" r WHERE r."ManagedFileId"=OLD."Id" AND
                    (NEW."TenantId" IS DISTINCT FROM r."TenantId" OR NEW."OwnerUserId" IS DISTINCT FROM r."OwnerUserId" OR NEW."ScopeKey" IS DISTINCT FROM r."ScopeKey"))
                    THEN RAISE EXCEPTION 'FK_managed_file_saved_reference_lineage' USING ERRCODE='23503'; END IF; RETURN NEW; END $fn$;
                CREATE FUNCTION allstarr_sync_managed_file_reference_count() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE old_file uuid; new_file uuid;
                BEGIN
                    old_file := CASE WHEN TG_OP IN ('UPDATE','DELETE') THEN OLD."ManagedFileId" ELSE NULL END;
                    new_file := CASE WHEN TG_OP IN ('INSERT','UPDATE') THEN NEW."ManagedFileId" ELSE NULL END;
                    IF old_file IS NOT NULL THEN
                        UPDATE "managed_files" f SET "ReferenceCount"=(SELECT count(*)::integer FROM "managed_file_references" r WHERE r."ManagedFileId"=old_file AND r."ReleasedAt" IS NULL), "Revision"=f."Revision"+1
                        WHERE f."Id"=old_file AND f."ReferenceCount" IS DISTINCT FROM (SELECT count(*) FROM "managed_file_references" r WHERE r."ManagedFileId"=old_file AND r."ReleasedAt" IS NULL);
                    END IF;
                    IF new_file IS NOT NULL AND new_file IS DISTINCT FROM old_file THEN
                        UPDATE "managed_files" f SET "ReferenceCount"=(SELECT count(*)::integer FROM "managed_file_references" r WHERE r."ManagedFileId"=new_file AND r."ReleasedAt" IS NULL), "Revision"=f."Revision"+1
                        WHERE f."Id"=new_file AND f."ReferenceCount" IS DISTINCT FROM (SELECT count(*) FROM "managed_file_references" r WHERE r."ManagedFileId"=new_file AND r."ReleasedAt" IS NULL);
                    END IF;
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END $fn$;
                CREATE FUNCTION allstarr_guard_managed_file_reference_count() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE active_count bigint;
                BEGIN
                    SELECT count(*) INTO active_count FROM "managed_file_references" r WHERE r."ManagedFileId"=NEW."Id" AND r."ReleasedAt" IS NULL;
                    IF NEW."ReferenceCount" IS DISTINCT FROM active_count THEN
                        RAISE EXCEPTION 'CK_managed_file_reference_count' USING ERRCODE='23514';
                    END IF;
                    IF NEW."RemovedAt" IS NOT NULL AND active_count <> 0 THEN
                        RAISE EXCEPTION 'CK_managed_file_removed_references' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END $fn$;
                CREATE FUNCTION allstarr_initialize_managed_file_reference_count() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    UPDATE "managed_files" SET "ReferenceCount"=0, "Revision"="Revision"+1 WHERE "Id"=NEW."Id" AND "ReferenceCount"<>0;
                    RETURN NEW;
                END $fn$;
                CREATE FUNCTION allstarr_validate_download_artifact_file_lineage() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF NEW."ManagedFileId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "managed_files" f WHERE f."Id"=NEW."ManagedFileId"
                        AND f."TenantId"=NEW."TenantId" AND f."OwnerUserId" IS NOT DISTINCT FROM NEW."OwnerUserId"
                        AND f."LibraryScopeId" IS NOT DISTINCT FROM NEW."LibraryScopeId") THEN
                        RAISE EXCEPTION 'FK_download_artifact_managed_file_lineage' USING ERRCODE='23503';
                    END IF;
                    RETURN NEW;
                END $fn$;
                CREATE FUNCTION allstarr_guard_download_artifact_file_lineage() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "provider_download_artifacts" a WHERE a."ManagedFileId"=OLD."Id" AND
                        (a."TenantId" IS DISTINCT FROM NEW."TenantId" OR a."OwnerUserId" IS DISTINCT FROM NEW."OwnerUserId" OR
                         a."LibraryScopeId" IS DISTINCT FROM NEW."LibraryScopeId")) THEN
                        RAISE EXCEPTION 'FK_download_artifact_saved_file_lineage' USING ERRCODE='23503';
                    END IF;
                    RETURN NEW;
                END $fn$;
                CREATE FUNCTION allstarr_guard_provider_account_scope_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN IF EXISTS (SELECT 1 FROM "durable_jobs" j WHERE j."ProviderAccountId"=OLD."Id" AND NOT (
                    (NEW."Scope"='Global' AND NEW."TenantId" IS NULL AND NEW."OwnerUserId" IS NULL AND NEW."LibraryScopeId" IS NULL) OR
                    (NEW."Scope"='User' AND NEW."TenantId"=j."TenantId" AND NEW."OwnerUserId"=j."OwnerUserId") OR
                    (NEW."Scope"='Library' AND NEW."TenantId"=j."TenantId" AND NEW."OwnerUserId" IS NULL AND NEW."LibraryScopeId"=j."LibraryScopeId")))
                  OR EXISTS (SELECT 1 FROM "provider_route_decisions" d WHERE d."SelectedProviderAccountId"=OLD."Id" AND NOT (
                    (NEW."Scope"='Global' AND NEW."TenantId" IS NULL AND NEW."OwnerUserId" IS NULL AND NEW."LibraryScopeId" IS NULL) OR
                    (NEW."Scope"='User' AND NEW."TenantId"=d."TenantId" AND NEW."OwnerUserId"=d."ActorUserId") OR
                    (NEW."Scope"='Library' AND NEW."TenantId"=d."TenantId" AND NEW."OwnerUserId" IS NULL AND NEW."LibraryScopeId"=d."LibraryScopeId")))
                  OR EXISTS (SELECT 1 FROM "provider_route_outcomes" o JOIN "provider_route_decisions" d ON d."Id"=o."RouteDecisionId" AND d."TenantId"=o."TenantId" WHERE o."ProviderAccountId"=OLD."Id" AND NOT (
                    (NEW."Scope"='Global' AND NEW."TenantId" IS NULL AND NEW."OwnerUserId" IS NULL AND NEW."LibraryScopeId" IS NULL) OR
                    (NEW."Scope"='User' AND NEW."TenantId"=d."TenantId" AND NEW."OwnerUserId"=d."ActorUserId") OR
                    (NEW."Scope"='Library' AND NEW."TenantId"=d."TenantId" AND NEW."OwnerUserId" IS NULL AND NEW."LibraryScopeId"=d."LibraryScopeId")))
                THEN RAISE EXCEPTION 'CK_provider_account_saved_lineage' USING ERRCODE='23503'; END IF; RETURN NEW; END $fn$;

                CREATE TRIGGER "TR_durable_job_account_scope" BEFORE INSERT OR UPDATE OF "ProviderAccountId","TenantId","OwnerUserId","LibraryScopeId" ON "durable_jobs" FOR EACH ROW EXECUTE FUNCTION allstarr_validate_job_account_scope();
                CREATE TRIGGER "TR_provider_route_decision_account_scope" BEFORE INSERT OR UPDATE OF "SelectedProviderAccountId","TenantId","ActorUserId","LibraryScopeId" ON "provider_route_decisions" FOR EACH ROW EXECUTE FUNCTION allstarr_validate_route_decision_account_scope();
                CREATE TRIGGER "TR_provider_route_outcome_account_scope" BEFORE INSERT OR UPDATE OF "ProviderAccountId","TenantId","RouteDecisionId" ON "provider_route_outcomes" FOR EACH ROW EXECUTE FUNCTION allstarr_validate_route_outcome_account_scope();
                CREATE TRIGGER "TR_managed_file_reference_lineage" BEFORE INSERT OR UPDATE OF "ManagedFileId","TenantId","OwnerUserId","ScopeKey" ON "managed_file_references" FOR EACH ROW EXECUTE FUNCTION allstarr_validate_managed_file_reference_lineage();
                CREATE TRIGGER "TR_managed_file_saved_reference_lineage" BEFORE UPDATE OF "Id","TenantId","OwnerUserId","ScopeKey" ON "managed_files" FOR EACH ROW EXECUTE FUNCTION allstarr_guard_managed_file_reference_lineage();
                CREATE TRIGGER "TR_managed_file_reference_count_insert" AFTER INSERT ON "managed_file_references" FOR EACH ROW EXECUTE FUNCTION allstarr_sync_managed_file_reference_count();
                CREATE TRIGGER "TR_managed_file_reference_count_update" AFTER UPDATE OF "ManagedFileId","ReleasedAt" ON "managed_file_references" FOR EACH ROW EXECUTE FUNCTION allstarr_sync_managed_file_reference_count();
                CREATE TRIGGER "TR_managed_file_reference_count_delete" AFTER DELETE ON "managed_file_references" FOR EACH ROW EXECUTE FUNCTION allstarr_sync_managed_file_reference_count();
                CREATE TRIGGER "TR_managed_file_reference_count_guard" BEFORE UPDATE OF "ReferenceCount","RemovedAt" ON "managed_files" FOR EACH ROW EXECUTE FUNCTION allstarr_guard_managed_file_reference_count();
                CREATE TRIGGER "TR_managed_file_reference_count_initialize" AFTER INSERT ON "managed_files" FOR EACH ROW EXECUTE FUNCTION allstarr_initialize_managed_file_reference_count();
                CREATE TRIGGER "TR_download_artifact_file_lineage_insert" BEFORE INSERT ON "provider_download_artifacts" FOR EACH ROW EXECUTE FUNCTION allstarr_validate_download_artifact_file_lineage();
                CREATE TRIGGER "TR_download_artifact_file_lineage_update" BEFORE UPDATE OF "ManagedFileId","TenantId","OwnerUserId","LibraryScopeId" ON "provider_download_artifacts" FOR EACH ROW EXECUTE FUNCTION allstarr_validate_download_artifact_file_lineage();
                CREATE TRIGGER "TR_download_artifact_saved_file_lineage" BEFORE UPDATE OF "Id","TenantId","OwnerUserId","LibraryScopeId" ON "managed_files" FOR EACH ROW EXECUTE FUNCTION allstarr_guard_download_artifact_file_lineage();
                CREATE TRIGGER "TR_provider_account_saved_lineage" BEFORE UPDATE OF "Scope","TenantId","OwnerUserId","LibraryScopeId" ON "provider_accounts" FOR EACH ROW EXECUTE FUNCTION allstarr_guard_provider_account_scope_update();
                """);
        }

        private static void RemovePostgresLineageEnforcement(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_provider_account_saved_lineage" ON "provider_accounts";
                DROP TRIGGER IF EXISTS "TR_managed_file_saved_reference_lineage" ON "managed_files";
                DROP TRIGGER IF EXISTS "TR_managed_file_reference_count_guard" ON "managed_files";
                DROP TRIGGER IF EXISTS "TR_managed_file_reference_count_initialize" ON "managed_files";
                DROP TRIGGER IF EXISTS "TR_download_artifact_saved_file_lineage" ON "managed_files";
                DROP TRIGGER IF EXISTS "TR_download_artifact_file_lineage_update" ON "provider_download_artifacts";
                DROP TRIGGER IF EXISTS "TR_download_artifact_file_lineage_insert" ON "provider_download_artifacts";
                DROP TRIGGER IF EXISTS "TR_managed_file_reference_count_delete" ON "managed_file_references";
                DROP TRIGGER IF EXISTS "TR_managed_file_reference_count_update" ON "managed_file_references";
                DROP TRIGGER IF EXISTS "TR_managed_file_reference_count_insert" ON "managed_file_references";
                DROP TRIGGER IF EXISTS "TR_managed_file_reference_lineage" ON "managed_file_references";
                DROP TRIGGER IF EXISTS "TR_provider_route_outcome_account_scope" ON "provider_route_outcomes";
                DROP TRIGGER IF EXISTS "TR_provider_route_decision_account_scope" ON "provider_route_decisions";
                DROP TRIGGER IF EXISTS "TR_durable_job_account_scope" ON "durable_jobs";
                DROP FUNCTION IF EXISTS allstarr_guard_provider_account_scope_update();
                DROP FUNCTION IF EXISTS allstarr_guard_managed_file_reference_lineage();
                DROP FUNCTION IF EXISTS allstarr_validate_managed_file_reference_lineage();
                DROP FUNCTION IF EXISTS allstarr_guard_managed_file_reference_count();
                DROP FUNCTION IF EXISTS allstarr_sync_managed_file_reference_count();
                DROP FUNCTION IF EXISTS allstarr_initialize_managed_file_reference_count();
                DROP FUNCTION IF EXISTS allstarr_guard_download_artifact_file_lineage();
                DROP FUNCTION IF EXISTS allstarr_validate_download_artifact_file_lineage();
                DROP FUNCTION IF EXISTS allstarr_validate_route_outcome_account_scope();
                DROP FUNCTION IF EXISTS allstarr_validate_route_decision_account_scope();
                DROP FUNCTION IF EXISTS allstarr_validate_job_account_scope();

                ALTER TABLE "provider_route_decisions" DROP CONSTRAINT IF EXISTS "FK_provider_route_decision_job_owner_lineage";
                ALTER TABLE "provider_route_decisions" DROP CONSTRAINT IF EXISTS "FK_provider_route_decision_job_tenant_lineage";
                ALTER TABLE "metadata_enrichment_applications" DROP CONSTRAINT IF EXISTS "FK_enrichment_application_job_lineage";
                ALTER TABLE "metadata_enrichment_plans" DROP CONSTRAINT IF EXISTS "FK_enrichment_plan_file_lineage";
                ALTER TABLE "metadata_enrichment_plans" DROP CONSTRAINT IF EXISTS "FK_enrichment_plan_job_lineage";
                ALTER TABLE "provider_download_workspaces" DROP CONSTRAINT IF EXISTS "FK_download_workspace_job_owner_lineage";
                ALTER TABLE "provider_download_workspaces" DROP CONSTRAINT IF EXISTS "FK_download_workspace_job_tenant_lineage";
                ALTER TABLE "favorite_events" DROP CONSTRAINT IF EXISTS "FK_favorite_event_job_lineage";
                ALTER TABLE "managed_files" DROP CONSTRAINT IF EXISTS "FK_managed_file_job_owner_lineage";
                ALTER TABLE "managed_files" DROP CONSTRAINT IF EXISTS "FK_managed_file_job_tenant_lineage";
                DROP FUNCTION IF EXISTS allstarr_account_scope_matches(uuid, uuid, uuid, text);
                """);
        }

    }
}
