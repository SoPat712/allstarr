using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialDurableFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
            var textType = postgres ? "text" : "TEXT";
            var guidType = postgres ? "uuid" : "TEXT";
            var integerType = postgres ? "integer" : "INTEGER";
            var bigintType = postgres ? "bigint" : "INTEGER";
            var booleanType = postgres ? "boolean" : "INTEGER";
            var blobType = postgres ? "bytea" : "BLOB";

            migrationBuilder.CreateTable(
                name: "backups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    StorageProvider = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    ArtifactPath = table.Column<string>(type: textType, maxLength: 1000, nullable: false),
                    Sha256 = table.Column<string>(type: textType, maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    ApplicationVersion = table.Column<string>(type: textType, maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: textType, maxLength: 50, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    VerifiedAt = table.Column<long>(type: bigintType, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    Slug = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: true),
                    Type = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: textType, nullable: false),
                    State = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    AvailableAt = table.Column<long>(type: bigintType, nullable: false),
                    AttemptCount = table.Column<int>(type: integerType, nullable: false),
                    LeaseOwner = table.Column<string>(type: textType, maxLength: 200, nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: bigintType, nullable: true),
                    DeliveredAt = table.Column<long>(type: bigintType, nullable: true),
                    LastErrorCode = table.Column<string>(type: textType, maxLength: 100, nullable: true),
                    LastErrorMessage = table.Column<string>(type: textType, maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_outbox_messages_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "secret_references",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: true),
                    Purpose = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    ActiveVersion = table.Column<int>(type: integerType, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    RevokedAt = table.Column<long>(type: bigintType, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_references", x => x.Id);
                    table.ForeignKey(
                        name: "FK_secret_references_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    DisplayName = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_users_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "secret_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    SecretReferenceId = table.Column<Guid>(type: guidType, nullable: false),
                    Version = table.Column<int>(type: integerType, nullable: false),
                    KeyId = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    Nonce = table.Column<byte[]>(type: blobType, nullable: false),
                    Ciphertext = table.Column<byte[]>(type: blobType, nullable: false),
                    AuthenticationTag = table.Column<byte[]>(type: blobType, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    RetiredAt = table.Column<long>(type: bigintType, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_secret_versions_secret_references_SecretReferenceId",
                        column: x => x.SecretReferenceId,
                        principalTable: "secret_references",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: true),
                    ActorUserId = table.Column<Guid>(type: guidType, nullable: true),
                    Category = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    Outcome = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    CorrelationId = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    DetailsJson = table.Column<string>(type: textType, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_audit_events_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_audit_events_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backend_identities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    UserId = table.Column<Guid>(type: guidType, nullable: false),
                    BackendType = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    PrincipalId = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    DisplayName = table.Column<string>(type: textType, maxLength: 200, nullable: true),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    LastSeenAt = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backend_identities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_backend_identities_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backend_identities_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "durable_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    ScopeKey = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: true),
                    OwnerUserId = table.Column<Guid>(type: guidType, nullable: true),
                    Type = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: textType, nullable: false),
                    IdempotencyKey = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    State = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    Priority = table.Column<int>(type: integerType, nullable: false),
                    AttemptCount = table.Column<int>(type: integerType, nullable: false),
                    MaxAttempts = table.Column<int>(type: integerType, nullable: false),
                    AvailableAt = table.Column<long>(type: bigintType, nullable: false),
                    LeaseOwner = table.Column<string>(type: textType, maxLength: 200, nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: bigintType, nullable: true),
                    CancellationRequestedAt = table.Column<long>(type: bigintType, nullable: true),
                    StartedAt = table.Column<long>(type: bigintType, nullable: true),
                    CompletedAt = table.Column<long>(type: bigintType, nullable: true),
                    LastErrorCode = table.Column<string>(type: textType, maxLength: 100, nullable: true),
                    LastErrorMessage = table.Column<string>(type: textType, maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_durable_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_durable_jobs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_durable_jobs_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: true),
                    OwnerUserId = table.Column<Guid>(type: guidType, nullable: true),
                    ProviderId = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    LibraryScopeId = table.Column<string>(type: textType, maxLength: 300, nullable: true),
                    SecretReferenceId = table.Column<Guid>(type: guidType, nullable: true),
                    Enabled = table.Column<bool>(type: booleanType, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_accounts_secret_references_SecretReferenceId",
                        column: x => x.SecretReferenceId,
                        principalTable: "secret_references",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_accounts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_accounts_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "job_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    JobId = table.Column<Guid>(type: guidType, nullable: false),
                    AttemptNumber = table.Column<int>(type: integerType, nullable: false),
                    WorkerId = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    StartedAt = table.Column<long>(type: bigintType, nullable: false),
                    CompletedAt = table.Column<long>(type: bigintType, nullable: true),
                    Outcome = table.Column<string>(type: textType, maxLength: 50, nullable: true),
                    ErrorCode = table.Column<string>(type: textType, maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: textType, maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_attempts_durable_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_circuits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: guidType, nullable: false),
                    Capability = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    State = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: integerType, nullable: false),
                    OpenedAt = table.Column<long>(type: bigintType, nullable: true),
                    RetryAfter = table.Column<long>(type: bigintType, nullable: true),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_circuits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_circuits_provider_accounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_health_samples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: guidType, nullable: false),
                    Capability = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    State = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    LatencyMilliseconds = table.Column<long>(type: bigintType, nullable: true),
                    FailureCode = table.Column<string>(type: textType, maxLength: 100, nullable: true),
                    ObservedAt = table.Column<long>(type: bigintType, nullable: false),
                    ExpiresAt = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_health_samples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_health_samples_provider_accounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ActorUserId",
                table: "audit_events",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_CorrelationId",
                table: "audit_events",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_TenantId_CreatedAt",
                table: "audit_events",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backend_identities_BackendType_BackendInstanceId_PrincipalId",
                table: "backend_identities",
                columns: new[] { "BackendType", "BackendInstanceId", "PrincipalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backend_identities_TenantId_UserId",
                table: "backend_identities",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_backend_identities_UserId",
                table: "backend_identities",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_backups_CreatedAt",
                table: "backups",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_OwnerUserId",
                table: "durable_jobs",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_ScopeKey_Type_IdempotencyKey",
                table: "durable_jobs",
                columns: new[] { "ScopeKey", "Type", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_State_AvailableAt_Priority",
                table: "durable_jobs",
                columns: new[] { "State", "AvailableAt", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_TenantId",
                table: "durable_jobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_job_attempts_JobId_AttemptNumber",
                table: "job_attempts",
                columns: new[] { "JobId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_State_AvailableAt",
                table: "outbox_messages",
                columns: new[] { "State", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_TenantId",
                table: "outbox_messages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_OwnerUserId",
                table: "provider_accounts",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_ProviderId_TenantId_OwnerUserId",
                table: "provider_accounts",
                columns: new[] { "ProviderId", "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_SecretReferenceId",
                table: "provider_accounts",
                column: "SecretReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_TenantId",
                table: "provider_accounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_circuits_ProviderAccountId_Capability",
                table: "provider_circuits",
                columns: new[] { "ProviderAccountId", "Capability" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_account_capability_observed",
                table: "provider_health_samples",
                columns: new[] { "ProviderAccountId", "Capability", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_secret_references_TenantId_Purpose",
                table: "secret_references",
                columns: new[] { "TenantId", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_secret_versions_SecretReferenceId_Version",
                table: "secret_versions",
                columns: new[] { "SecretReferenceId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenants_Slug",
                table: "tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_TenantId_Id",
                table: "users",
                columns: new[] { "TenantId", "Id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "backend_identities");

            migrationBuilder.DropTable(
                name: "backups");

            migrationBuilder.DropTable(
                name: "job_attempts");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "provider_circuits");

            migrationBuilder.DropTable(
                name: "provider_health_samples");

            migrationBuilder.DropTable(
                name: "secret_versions");

            migrationBuilder.DropTable(
                name: "durable_jobs");

            migrationBuilder.DropTable(
                name: "provider_accounts");

            migrationBuilder.DropTable(
                name: "secret_references");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
