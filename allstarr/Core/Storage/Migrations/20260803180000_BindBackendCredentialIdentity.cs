using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260803180000_BindBackendCredentialIdentity")]
public sealed class BindBackendCredentialIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "BackendIdentityId",
            table: "secret_references",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql("""
            WITH credential_scopes AS (
                SELECT "TargetCredentialReferenceId" AS credential_id, "TenantId" AS tenant_id,
                       "OwnerUserId" AS owner_id, "TargetProtocol" AS protocol,
                       "TargetBackendInstanceId" AS backend_id
                FROM playlist_links
                WHERE "TargetCredentialReferenceId" IS NOT NULL AND "TargetProtocol" = 'subsonic'
                UNION ALL
                SELECT "TargetCredentialReferenceId", "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId"
                FROM intelligence_policies
                WHERE "TargetCredentialReferenceId" IS NOT NULL AND "Protocol" = 'subsonic'
            ), exact_bindings AS (
                SELECT scopes.credential_id, MIN(identity."Id"::text)::uuid AS identity_id
                FROM credential_scopes scopes
                LEFT JOIN backend_identities identity
                  ON identity."TenantId" = scopes.tenant_id
                 AND identity."UserId" = scopes.owner_id
                 AND identity."BackendType" = scopes.protocol
                 AND identity."BackendInstanceId" = scopes.backend_id
                GROUP BY scopes.credential_id
                HAVING COUNT(*) = COUNT(identity."Id") AND COUNT(DISTINCT identity."Id") = 1
            )
            UPDATE secret_references secret
               SET "BackendIdentityId" = binding.identity_id
              FROM exact_bindings binding
             WHERE secret."Id" = binding.credential_id
               AND secret."Purpose" = 'playlist-backend:subsonic';
            """);

        migrationBuilder.CreateIndex(
            name: "IX_secret_references_BackendIdentityId",
            table: "secret_references",
            column: "BackendIdentityId");

        migrationBuilder.AddForeignKey(
            name: "FK_secret_references_backend_identities_BackendIdentityId",
            table: "secret_references",
            column: "BackendIdentityId",
            principalTable: "backend_identities",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_secret_references_backend_identities_BackendIdentityId",
            table: "secret_references");
        migrationBuilder.DropIndex(
            name: "IX_secret_references_BackendIdentityId",
            table: "secret_references");
        migrationBuilder.DropColumn(
            name: "BackendIdentityId",
            table: "secret_references");
    }
}
