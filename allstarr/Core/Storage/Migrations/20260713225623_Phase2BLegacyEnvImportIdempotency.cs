using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Phase2BLegacyEnvImportIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
            var text = postgres ? "text" : "TEXT";
            var guid = postgres ? "uuid" : "TEXT";
            var bigint = postgres ? "bigint" : "INTEGER";

            migrationBuilder.CreateTable(
                name: "legacy_env_imports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    TenantId = table.Column<Guid>(type: guid, nullable: false),
                    SourceSha256 = table.Column<string>(type: text, maxLength: 64, nullable: false),
                    ActorUserId = table.Column<Guid>(type: guid, nullable: true),
                    AuditEventId = table.Column<Guid>(type: guid, nullable: false),
                    ResultJson = table.Column<string>(type: text, nullable: false),
                    AppliedAt = table.Column<long>(type: bigint, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_env_imports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_legacy_env_imports_audit_events_AuditEventId",
                        column: x => x.AuditEventId,
                        principalTable: "audit_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_legacy_env_imports_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_legacy_env_imports_users_TenantId_ActorUserId",
                        columns: x => new { x.TenantId, x.ActorUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_legacy_env_imports_AuditEventId",
                table: "legacy_env_imports",
                column: "AuditEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legacy_env_imports_TenantId_ActorUserId",
                table: "legacy_env_imports",
                columns: new[] { "TenantId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_legacy_env_imports_TenantId_SourceSha256",
                table: "legacy_env_imports",
                columns: new[] { "TenantId", "SourceSha256" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legacy_env_imports");
        }
    }
}
