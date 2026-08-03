using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260803040000_AddListeningIntakeTokens")]
public sealed class AddListeningIntakeTokens : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "listening_intake_tokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                Protocol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                BackendInstanceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                LibraryScopeId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                SecretReferenceId = table.Column<Guid>(type: "uuid", nullable: false),
                RelayExternally = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                RevokedAt = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_listening_intake_tokens", item => item.Id);
                table.ForeignKey("FK_listening_intake_token_secret", item => item.SecretReferenceId,
                    "secret_references", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_listening_intake_tokens_tenants_TenantId", item => item.TenantId,
                    "tenants", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_listening_intake_tokens_users_TenantId_OwnerUserId",
                    item => new { item.TenantId, item.OwnerUserId }, "users", new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_listening_intake_token_secret", "listening_intake_tokens",
            "SecretReferenceId", unique: true);
        migrationBuilder.CreateIndex("IX_listening_intake_token_scope", "listening_intake_tokens",
            new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("listening_intake_tokens");
}
