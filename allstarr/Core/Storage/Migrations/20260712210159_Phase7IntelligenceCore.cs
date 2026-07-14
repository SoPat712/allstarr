using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Phase7IntelligenceCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
            var text = postgres ? "text" : "TEXT";
            var guid = postgres ? "uuid" : "TEXT";
            var integer = postgres ? "integer" : "INTEGER";
            var bigint = postgres ? "bigint" : "INTEGER";
            var boolean = postgres ? "boolean" : "INTEGER";
            var real = postgres ? "double precision" : "REAL";

            migrationBuilder.CreateTable(
                name: "intelligence_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    TenantId = table.Column<Guid>(type: guid, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guid, nullable: false),
                    Protocol = table.Column<string>(type: text, maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: text, maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: text, maxLength: 300, nullable: false),
                    Enabled = table.Column<bool>(type: boolean, nullable: false),
                    TargetCredentialReferenceId = table.Column<Guid>(type: guid, nullable: true),
                    RetentionDays = table.Column<int>(type: integer, nullable: false),
                    AllowedSignalTypesJson = table.Column<string>(type: text, nullable: false),
                    EnabledProvidersJson = table.Column<string>(type: text, nullable: false),
                    CreatedAt = table.Column<long>(type: bigint, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigint, nullable: false),
                    Revision = table.Column<long>(type: bigint, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_intelligence_policies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_intelligence_policies_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_intelligence_policies_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "listening_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    TenantId = table.Column<Guid>(type: guid, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guid, nullable: false),
                    Protocol = table.Column<string>(type: text, maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: text, maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: text, maxLength: 300, nullable: false),
                    ProfileJson = table.Column<string>(type: text, nullable: false),
                    WindowStart = table.Column<long>(type: bigint, nullable: false),
                    WindowEnd = table.Column<long>(type: bigint, nullable: false),
                    CreatedAt = table.Column<long>(type: bigint, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listening_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_listening_profiles_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_profiles_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "listening_signals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    TenantId = table.Column<Guid>(type: guid, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guid, nullable: false),
                    Protocol = table.Column<string>(type: text, maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: text, maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: text, maxLength: 300, nullable: false),
                    SignalType = table.Column<string>(type: text, maxLength: 32, nullable: false),
                    TrackKeyHash = table.Column<string>(type: text, maxLength: 64, nullable: false),
                    Value = table.Column<double>(type: real, nullable: false),
                    TrackReference = table.Column<string>(type: text, maxLength: 100, nullable: false),
                    SignalKey = table.Column<string>(type: text, maxLength: 64, nullable: true),
                    SourceJobId = table.Column<Guid>(type: guid, nullable: true),
                    ObservedAt = table.Column<long>(type: bigint, nullable: false),
                    ExpiresAt = table.Column<long>(type: bigint, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listening_signals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_listening_signal_job",
                        column: x => x.SourceJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_signals_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listening_signals_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playback_delivery_checkpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    TenantId = table.Column<Guid>(type: guid, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guid, nullable: false),
                    SignalKey = table.Column<string>(type: text, maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: text, maxLength: 100, nullable: false),
                    CompletedAt = table.Column<long>(type: bigint, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playback_delivery_checkpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_playback_delivery_checkpoints_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recommendation_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    TenantId = table.Column<Guid>(type: guid, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guid, nullable: false),
                    Protocol = table.Column<string>(type: text, maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: text, maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: text, maxLength: 300, nullable: false),
                    JobId = table.Column<Guid>(type: guid, nullable: false),
                    IdempotencyKey = table.Column<string>(type: text, maxLength: 300, nullable: false),
                    PolicySnapshotJson = table.Column<string>(type: text, nullable: false),
                    SeedTrackKeysJson = table.Column<string>(type: text, nullable: false),
                    Limit = table.Column<int>(type: integer, nullable: false),
                    TargetCredentialReferenceId = table.Column<Guid>(type: guid, nullable: true),
                    State = table.Column<string>(type: text, maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: text, maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: bigint, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigint, nullable: false),
                    CompletedAt = table.Column<long>(type: bigint, nullable: true),
                    Revision = table.Column<long>(type: bigint, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_runs", x => x.Id);
                    table.UniqueConstraint("AK_recommendation_runs_Id_TenantId_OwnerUserId", x => new { x.Id, x.TenantId, x.OwnerUserId });
                    table.ForeignKey(
                        name: "FK_recommendation_runs_durable_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_runs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_runs_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "generated_sets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    RunId = table.Column<Guid>(type: guid, nullable: false),
                    TenantId = table.Column<Guid>(type: guid, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guid, nullable: false),
                    Protocol = table.Column<string>(type: text, maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: text, maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: text, maxLength: 300, nullable: false),
                    Name = table.Column<string>(type: text, maxLength: 200, nullable: false),
                    CreatedAt = table.Column<long>(type: bigint, nullable: false),
                    TargetCredentialReferenceId = table.Column<Guid>(type: guid, nullable: true),
                    MaterializationState = table.Column<string>(type: text, maxLength: 32, nullable: false),
                    BackendPlaylistId = table.Column<string>(type: text, maxLength: 500, nullable: true),
                    TargetRevision = table.Column<string>(type: text, maxLength: 300, nullable: true),
                    LastErrorCode = table.Column<string>(type: text, maxLength: 100, nullable: true),
                    MaterializedAt = table.Column<long>(type: bigint, nullable: true),
                    UpdatedAt = table.Column<long>(type: bigint, nullable: false),
                    Revision = table.Column<long>(type: bigint, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generated_sets", x => x.Id);
                    table.UniqueConstraint("AK_generated_sets_Id_TenantId_OwnerUserId", x => new { x.Id, x.TenantId, x.OwnerUserId });
                    table.ForeignKey(
                        name: "FK_generated_sets_recommendation_runs_RunId_TenantId_OwnerUserId",
                        columns: x => new { x.RunId, x.TenantId, x.OwnerUserId },
                        principalTable: "recommendation_runs",
                        principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_generated_sets_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_generated_sets_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recommendation_candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    RunId = table.Column<Guid>(type: guid, nullable: false),
                    TenantId = table.Column<Guid>(type: guid, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guid, nullable: false),
                    Position = table.Column<int>(type: integer, nullable: false),
                    TrackKey = table.Column<string>(type: text, maxLength: 500, nullable: false),
                    Score = table.Column<double>(type: real, nullable: false),
                    Source = table.Column<string>(type: text, maxLength: 100, nullable: false),
                    SignalsJson = table.Column<string>(type: text, nullable: false),
                    CreatedAt = table.Column<long>(type: bigint, nullable: false),
                    IdentityJson = table.Column<string>(type: text, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_candidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recommendation_candidates_recommendation_runs_RunId_TenantId_OwnerUserId",
                        columns: x => new { x.RunId, x.TenantId, x.OwnerUserId },
                        principalTable: "recommendation_runs",
                        principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generated_set_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    GeneratedSetId = table.Column<Guid>(type: guid, nullable: false),
                    TenantId = table.Column<Guid>(type: guid, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guid, nullable: false),
                    Position = table.Column<int>(type: integer, nullable: false),
                    TrackKey = table.Column<string>(type: text, maxLength: 500, nullable: false),
                    ExplanationJson = table.Column<string>(type: text, nullable: false),
                    IdentityJson = table.Column<string>(type: text, nullable: false),
                    Score = table.Column<double>(type: real, nullable: false),
                    Source = table.Column<string>(type: text, maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generated_set_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_generated_set_entries_generated_sets_GeneratedSetId_TenantId_OwnerUserId",
                        columns: x => new { x.GeneratedSetId, x.TenantId, x.OwnerUserId },
                        principalTable: "generated_sets",
                        principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_generated_set_entries_GeneratedSetId_Position",
                table: "generated_set_entries",
                columns: new[] { "GeneratedSetId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generated_set_entries_GeneratedSetId_TenantId_OwnerUserId",
                table: "generated_set_entries",
                columns: new[] { "GeneratedSetId", "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_generated_set_credential_reference",
                table: "generated_sets",
                column: "TargetCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_generated_sets_RunId",
                table: "generated_sets",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generated_sets_RunId_TenantId_OwnerUserId",
                table: "generated_sets",
                columns: new[] { "RunId", "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_generated_sets_TenantId_OwnerUserId",
                table: "generated_sets",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_intelligence_policy_credential_reference",
                table: "intelligence_policies",
                column: "TargetCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_intelligence_policy_scope",
                table: "intelligence_policies",
                columns: new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_listening_profiles_TenantId_OwnerUserId_CreatedAt",
                table: "listening_profiles",
                columns: new[] { "TenantId", "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_listening_signal_idempotency",
                table: "listening_signals",
                columns: new[] { "TenantId", "OwnerUserId", "SignalKey" },
                unique: true,
                filter: "\"SignalKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_listening_signals_SourceJobId",
                table: "listening_signals",
                column: "SourceJobId");

            migrationBuilder.CreateIndex(
                name: "IX_listening_signals_TenantId_OwnerUserId_ExpiresAt",
                table: "listening_signals",
                columns: new[] { "TenantId", "OwnerUserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_playback_delivery_idempotency",
                table: "playback_delivery_checkpoints",
                columns: new[] { "TenantId", "OwnerUserId", "SignalKey", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_RunId_Position",
                table: "recommendation_candidates",
                columns: new[] { "RunId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_RunId_TenantId_OwnerUserId",
                table: "recommendation_candidates",
                columns: new[] { "RunId", "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_run_credential_reference",
                table: "recommendation_runs",
                column: "TargetCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_run_idempotency",
                table: "recommendation_runs",
                columns: new[] { "TenantId", "OwnerUserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_runs_JobId",
                table: "recommendation_runs",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "generated_set_entries");

            migrationBuilder.DropTable(
                name: "intelligence_policies");

            migrationBuilder.DropTable(
                name: "listening_profiles");

            migrationBuilder.DropTable(
                name: "listening_signals");

            migrationBuilder.DropTable(
                name: "playback_delivery_checkpoints");

            migrationBuilder.DropTable(
                name: "recommendation_candidates");

            migrationBuilder.DropTable(
                name: "generated_sets");

            migrationBuilder.DropTable(
                name: "recommendation_runs");

        }
    }
}
