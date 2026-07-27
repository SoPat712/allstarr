using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableOnboardingState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "onboarding_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CompletedStepsJson = table.Column<string>(type: "text", nullable: false),
                    CompletionSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                    ReopenedAt = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onboarding_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_onboarding_states_users_TenantId_UserId",
                        columns: x => new { x.TenantId, x.UserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_states_TenantId_UserId",
                table: "onboarding_states",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO onboarding_states
                    ("Id", "TenantId", "UserId", "SchemaVersion", "CompletedStepsJson",
                     "CompletionSource", "CompletedAt", "ReopenedAt", "CreatedAt", "UpdatedAt", "Revision")
                SELECT
                    md5(users."TenantId"::text || ':' || users."Id"::text)::uuid,
                    users."TenantId",
                    users."Id",
                    'onboarding-v1',
                    '["backend-identity"]',
                    'schema-backfill',
                    639102595370000000,
                    NULL,
                    639102595370000000,
                    639102595370000000,
                    1
                FROM users
                WHERE EXISTS (
                    SELECT 1
                    FROM backend_identities
                    WHERE backend_identities."TenantId" = users."TenantId"
                      AND backend_identities."UserId" = users."Id")
                ON CONFLICT ("TenantId", "UserId") DO NOTHING
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "onboarding_states");
        }
    }
}
