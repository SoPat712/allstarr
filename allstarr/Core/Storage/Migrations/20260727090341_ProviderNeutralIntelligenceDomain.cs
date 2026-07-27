using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProviderNeutralIntelligenceDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CanonicalRecordingId",
                table: "recommendation_candidates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExclusionsJson",
                table: "recommendation_candidates",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "ProviderAccountId",
                table: "recommendation_candidates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "recommendation_candidates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "SourceRevision",
                table: "recommendation_candidates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_recommendation_candidates_Id_TenantId_OwnerUserId",
                table: "recommendation_candidates",
                columns: new[] { "Id", "TenantId", "OwnerUserId" });

            migrationBuilder.CreateTable(
                name: "recommendation_feedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Protocol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LibraryScopeId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TrackKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_feedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recommendation_feedback_recommendation_candidates_CandidateId_TenantId_OwnerUserId",
                        columns: x => new { x.CandidateId, x.TenantId, x.OwnerUserId },
                        principalTable: "recommendation_candidates",
                        principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_recommendation_feedback_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recommendation_feedback_users_TenantId_OwnerUserId",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_ProviderAccountId",
                table: "recommendation_candidates",
                column: "ProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_candidates_TenantId_CanonicalRecordingId",
                table: "recommendation_candidates",
                columns: new[] { "TenantId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_feedback_CandidateId_TenantId_OwnerUserId",
                table: "recommendation_feedback",
                columns: new[] { "CandidateId", "TenantId", "OwnerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_feedback_TenantId_OwnerUserId_Protocol_BackendInstanceId_LibraryScopeId_TrackKey",
                table: "recommendation_feedback",
                columns: new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId", "TrackKey" });

            migrationBuilder.AddForeignKey(
                name: "FK_recommendation_candidates_canonical_recordings_TenantId_CanonicalRecordingId",
                table: "recommendation_candidates",
                columns: new[] { "TenantId", "CanonicalRecordingId" },
                principalTable: "canonical_recordings",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_recommendation_candidates_provider_accounts_ProviderAccountId",
                table: "recommendation_candidates",
                column: "ProviderAccountId",
                principalTable: "provider_accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_recommendation_candidates_canonical_recordings_TenantId_CanonicalRecordingId",
                table: "recommendation_candidates");

            migrationBuilder.DropForeignKey(
                name: "FK_recommendation_candidates_provider_accounts_ProviderAccountId",
                table: "recommendation_candidates");

            migrationBuilder.DropTable(
                name: "recommendation_feedback");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_recommendation_candidates_Id_TenantId_OwnerUserId",
                table: "recommendation_candidates");

            migrationBuilder.DropIndex(
                name: "IX_recommendation_candidates_ProviderAccountId",
                table: "recommendation_candidates");

            migrationBuilder.DropIndex(
                name: "IX_recommendation_candidates_TenantId_CanonicalRecordingId",
                table: "recommendation_candidates");

            migrationBuilder.DropColumn(
                name: "CanonicalRecordingId",
                table: "recommendation_candidates");

            migrationBuilder.DropColumn(
                name: "ExclusionsJson",
                table: "recommendation_candidates");

            migrationBuilder.DropColumn(
                name: "ProviderAccountId",
                table: "recommendation_candidates");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "recommendation_candidates");

            migrationBuilder.DropColumn(
                name: "SourceRevision",
                table: "recommendation_candidates");
        }
    }
}
