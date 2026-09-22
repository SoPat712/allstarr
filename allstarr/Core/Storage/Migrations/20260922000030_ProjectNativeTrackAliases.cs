using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260922000030_ProjectNativeTrackAliases")]
public sealed class ProjectNativeTrackAliases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            WITH candidates AS (
                SELECT
                    track."Id" AS "LibraryTrackId",
                    track."TenantId",
                    track."CanonicalRecordingId",
                    'native:' || encode(sha256(convert_to(
                        track."Protocol" || E'\n' || track."BackendInstanceId",
                        'UTF8')), 'hex') AS "Namespace",
                    track."BackendItemId" AS "ExternalId",
                    encode(sha256(convert_to(track."BackendItemId", 'UTF8')), 'hex') AS "ExternalIdHash",
                    track."IndexedAt",
                    track."UpdatedAt"
                FROM "library_tracks" track
                WHERE track."CanonicalRecordingId" IS NOT NULL
            ), consistent AS (
                SELECT
                    candidate."TenantId",
                    candidate."Namespace",
                    candidate."ExternalIdHash",
                    min(candidate."ExternalId") AS "ExternalId",
                    min(candidate."CanonicalRecordingId"::text)::uuid AS "CanonicalRecordingId",
                    min(candidate."LibraryTrackId"::text)::uuid AS "LibraryTrackId",
                    min(candidate."IndexedAt") AS "CreatedAt",
                    max(candidate."UpdatedAt") AS "LastSeenAt"
                FROM candidates candidate
                GROUP BY
                    candidate."TenantId",
                    candidate."Namespace",
                    candidate."ExternalIdHash"
                HAVING count(DISTINCT candidate."ExternalId") = 1
                   AND count(DISTINCT candidate."CanonicalRecordingId") = 1
            )
            INSERT INTO "canonical_catalog_aliases"
                ("Id", "TenantId", "EntityKind", "CanonicalEntityId", "Namespace",
                 "ExternalId", "ExternalIdHash", "CreatedAt", "LastSeenAt")
            SELECT
                substr(encode(sha256(convert_to(
                    'canonical-alias-v1' || E'\n' || 'native' || E'\n' ||
                    consistent."LibraryTrackId"::text,
                    'UTF8')), 'hex'), 1, 32)::uuid,
                consistent."TenantId",
                'Recording',
                consistent."CanonicalRecordingId",
                consistent."Namespace",
                consistent."ExternalId",
                consistent."ExternalIdHash",
                consistent."CreatedAt",
                consistent."LastSeenAt"
            FROM consistent
            WHERE NOT EXISTS (
                SELECT 1
                FROM "canonical_catalog_aliases" alias
                WHERE alias."TenantId" = consistent."TenantId"
                  AND alias."Namespace" = consistent."Namespace"
                  AND alias."EntityKind" = 'Recording'
                  AND alias."ExternalIdHash" = consistent."ExternalIdHash");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM "canonical_catalog_aliases" alias
            USING "library_tracks" track
            WHERE alias."Id" = substr(encode(sha256(convert_to(
                'canonical-alias-v1' || E'\n' || 'native' || E'\n' || track."Id"::text,
                'UTF8')), 'hex'), 1, 32)::uuid;
            """);
    }
}
