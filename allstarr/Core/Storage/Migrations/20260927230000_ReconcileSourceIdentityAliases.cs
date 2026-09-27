using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260927230000_ReconcileSourceIdentityAliases")]
public sealed class ReconcileSourceIdentityAliases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            WITH repaired AS (
                UPDATE canonical_catalog_aliases alias
                SET "CanonicalEntityId" = identity."CanonicalRecordingId",
                    "LastSeenAt" = GREATEST(alias."LastSeenAt", identity."UpdatedAt")
                FROM provider_track_identities identity, canonical_recordings previous, canonical_recordings target
                WHERE alias."TenantId" = identity."TenantId"
                  AND alias."EntityKind" = 'Recording'
                  AND alias."Namespace" = 'provider:' || encode(sha256(convert_to(
                      identity."ProviderId" || E'\n' || identity."ResourceKind" || E'\n' ||
                      identity."CatalogNamespace" || E'\n' || identity."Scope" || E'\n' ||
                      COALESCE(identity."ProviderAccountId"::text, ''), 'UTF8')), 'hex')
                  AND alias."ExternalId" = identity."ExternalId"
                  AND alias."CanonicalEntityId" <> identity."CanonicalRecordingId"
                  AND identity."Verification" = 'Verified'
                  AND identity."VerificationMethod" IN ('source-snapshot', 'source-snapshot-hash')
                  AND previous."TenantId" = alias."TenantId" AND previous."Id" = alias."CanonicalEntityId"
                  AND previous."IsProvisional" AND previous."Isrc" IS NULL AND previous."MusicBrainzRecordingId" IS NULL
                  AND target."TenantId" = identity."TenantId" AND target."Id" = identity."CanonicalRecordingId"
                RETURNING alias."Id", alias."TenantId", previous."Id" AS previous_id,
                          identity."CanonicalRecordingId" AS target_id
            )
            INSERT INTO audit_events
                ("Id", "TenantId", "Category", "Action", "Outcome", "CorrelationId", "DetailsJson", "CreatedAt")
            SELECT gen_random_uuid(), "TenantId", 'canonical-catalog', 'source-alias.reconcile', 'updated',
                'migration:20260927230000',
                jsonb_build_object('aliasId', "Id", 'previousRecordingId', previous_id, 'targetRecordingId', target_id)::text,
                (EXTRACT(EPOCH FROM now()) * 10000000)::bigint + 621355968000000000
            FROM repaired;

            WITH duplicates AS (
                SELECT legacy."Id", current."Id" AS retained_id
                FROM canonical_catalog_aliases legacy
                JOIN canonical_catalog_aliases current
                  ON current."TenantId" = legacy."TenantId" AND current."Namespace" = legacy."Namespace"
                  AND current."EntityKind" = legacy."EntityKind" AND current."ExternalId" = legacy."ExternalId"
                  AND current."CanonicalEntityId" = legacy."CanonicalEntityId"
                  AND current."ExternalIdHash" = encode(sha256(convert_to(legacy."ExternalId", 'UTF8')), 'hex')
                WHERE legacy."Namespace" LIKE 'provider:%' AND legacy."EntityKind" = 'Recording'
                  AND legacy."ExternalIdHash" <> current."ExternalIdHash"
            ), removed AS (
                DELETE FROM canonical_catalog_aliases alias USING duplicates
                WHERE alias."Id" = duplicates."Id"
                RETURNING alias."TenantId", alias."Id", duplicates.retained_id
            )
            INSERT INTO audit_events
                ("Id", "TenantId", "Category", "Action", "Outcome", "CorrelationId", "DetailsJson", "CreatedAt")
            SELECT gen_random_uuid(), "TenantId", 'canonical-catalog', 'alias.deduplicate', 'updated',
                'migration:20260927230000', jsonb_build_object('removedAliasId', "Id", 'retainedAliasId', retained_id)::text,
                (EXTRACT(EPOCH FROM now()) * 10000000)::bigint + 621355968000000000
            FROM removed;

            UPDATE canonical_catalog_aliases alias
            SET "ExternalIdHash" = encode(sha256(convert_to(alias."ExternalId", 'UTF8')), 'hex')
            WHERE alias."Namespace" LIKE 'provider:%' AND alias."EntityKind" = 'Recording'
              AND alias."ExternalIdHash" <> encode(sha256(convert_to(alias."ExternalId", 'UTF8')), 'hex')
              AND NOT EXISTS (
                  SELECT 1 FROM canonical_catalog_aliases collision
                  WHERE collision."TenantId" = alias."TenantId" AND collision."Namespace" = alias."Namespace"
                    AND collision."EntityKind" = alias."EntityKind"
                    AND collision."Id" <> alias."Id"
                    AND (collision."ExternalIdHash" = encode(sha256(convert_to(alias."ExternalId", 'UTF8')), 'hex')
                         OR collision."ExternalId" = alias."ExternalId"));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restoring stale pointers would corrupt subsequent matches; rollback uses a verified database backup.
    }
}
