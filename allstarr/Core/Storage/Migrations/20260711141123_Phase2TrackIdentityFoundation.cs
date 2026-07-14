using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Phase2TrackIdentityFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
            var textType = postgres ? "text" : "TEXT";
            var guidType = postgres ? "uuid" : "TEXT";
            var integerType = postgres ? "integer" : "INTEGER";
            var bigintType = postgres ? "bigint" : "INTEGER";

            migrationBuilder.DropIndex(
                name: "IX_users_TenantId_Id",
                table: "users");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_users_TenantId_Id",
                table: "users",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_provider_accounts_Id_ProviderId",
                table: "provider_accounts",
                columns: new[] { "Id", "ProviderId" });

            migrationBuilder.CreateTable(
                name: "canonical_recordings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: guidType, nullable: false),
                    Isrc = table.Column<string>(type: textType, maxLength: 32, nullable: true),
                    MusicBrainzRecordingId = table.Column<string>(type: textType, maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_recordings", x => x.Id);
                    table.UniqueConstraint("AK_canonical_recordings_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_canonical_recordings_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_canonical_recordings_users_TenantId_CreatedByUserId",
                        columns: x => new { x.TenantId, x.CreatedByUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_track_identities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    CanonicalRecordingId = table.Column<Guid>(type: guidType, nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: guidType, nullable: true),
                    ProviderId = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    ResourceKind = table.Column<string>(type: textType, maxLength: 50, nullable: false),
                    CatalogNamespace = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    Scope = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                    ExternalIdHash = table.Column<string>(type: textType, maxLength: 64, nullable: false),
                    Verification = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    VerificationMethod = table.Column<string>(type: textType, maxLength: 50, nullable: false),
                    DecisionVersion = table.Column<int>(type: integerType, nullable: false),
                    VerifiedAt = table.Column<long>(type: bigintType, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_track_identities", x => x.Id);
                    table.CheckConstraint("CK_provider_track_identities_decision_version", "\"DecisionVersion\" > 0");
                    table.CheckConstraint("CK_provider_track_identities_external_hash", "length(\"ExternalIdHash\") = 64");
                    table.CheckConstraint("CK_provider_track_identities_scope_shape", "(\"Scope\" = 'Catalog' AND \"ProviderAccountId\" IS NULL) OR (\"Scope\" = 'Account' AND \"ProviderAccountId\" IS NOT NULL)");
                    table.CheckConstraint("CK_provider_track_identities_track_only", "\"ResourceKind\" = 'Track'");
                    table.CheckConstraint("CK_provider_track_identities_verification", "\"Verification\" IN ('Verified', 'Pinned')");
                    table.ForeignKey(
                        name: "FK_provider_track_identities_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_track_identity_canonical_recording",
                        columns: x => new { x.TenantId, x.CanonicalRecordingId },
                        principalTable: "canonical_recordings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_track_identity_provider_account",
                        columns: x => new { x.ProviderAccountId, x.ProviderId },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "Id", "ProviderId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_provider_accounts_scope_shape",
                table: "provider_accounts",
                sql: "(\"Scope\" = 'Global' AND \"TenantId\" IS NULL AND \"OwnerUserId\" IS NULL AND \"LibraryScopeId\" IS NULL) OR (\"Scope\" = 'User' AND \"TenantId\" IS NOT NULL AND \"OwnerUserId\" IS NOT NULL AND \"LibraryScopeId\" IS NULL) OR (\"Scope\" = 'Library' AND \"TenantId\" IS NOT NULL AND \"OwnerUserId\" IS NULL AND \"LibraryScopeId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recordings_TenantId_CreatedByUserId",
                table: "canonical_recordings",
                columns: new[] { "TenantId", "CreatedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recordings_TenantId_Isrc",
                table: "canonical_recordings",
                columns: new[] { "TenantId", "Isrc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recordings_TenantId_MusicBrainzRecordingId",
                table: "canonical_recordings",
                columns: new[] { "TenantId", "MusicBrainzRecordingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_track_identities_ProviderAccountId_ProviderId",
                table: "provider_track_identities",
                columns: new[] { "ProviderAccountId", "ProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_track_identities_TenantId_CanonicalRecordingId",
                table: "provider_track_identities",
                columns: new[] { "TenantId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_track_identity_account_exact",
                table: "provider_track_identities",
                columns: new[] { "TenantId", "ProviderId", "ResourceKind", "CatalogNamespace", "ProviderAccountId", "ExternalIdHash" },
                unique: true,
                filter: "\"Scope\" = 'Account'");

            migrationBuilder.CreateIndex(
                name: "IX_provider_track_identity_catalog_exact",
                table: "provider_track_identities",
                columns: new[] { "TenantId", "ProviderId", "ResourceKind", "CatalogNamespace", "ExternalIdHash" },
                unique: true,
                filter: "\"Scope\" = 'Catalog'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_track_identities");

            migrationBuilder.DropTable(
                name: "canonical_recordings");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_users_TenantId_Id",
                table: "users");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_provider_accounts_Id_ProviderId",
                table: "provider_accounts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_provider_accounts_scope_shape",
                table: "provider_accounts");

            migrationBuilder.CreateIndex(
                name: "IX_users_TenantId_Id",
                table: "users",
                columns: new[] { "TenantId", "Id" },
                unique: true);
        }
    }
}
