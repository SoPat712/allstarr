using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260922000029_ProjectCanonicalAliases")]
public sealed class ProjectCanonicalAliases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "canonical_recordings"
            SET "IsProvisional" = TRUE
            WHERE "MusicBrainzRecordingId" IS NULL;

            INSERT INTO "canonical_catalog_aliases"
                ("Id", "TenantId", "EntityKind", "CanonicalEntityId", "Namespace",
                 "ExternalId", "ExternalIdHash", "CreatedAt", "LastSeenAt")
            SELECT
                substr(encode(sha256(convert_to(
                    'canonical-alias-v1' || E'\n' || 'isrc' || E'\n' ||
                    recording."TenantId"::text || E'\n' || recording."Id"::text,
                    'UTF8')), 'hex'), 1, 32)::uuid,
                recording."TenantId",
                'Recording',
                recording."Id",
                'isrc',
                recording."Isrc",
                encode(sha256(convert_to(recording."Isrc", 'UTF8')), 'hex'),
                recording."CreatedAt",
                recording."UpdatedAt"
            FROM "canonical_recordings" recording
            WHERE recording."Isrc" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM "canonical_catalog_aliases" alias
                  WHERE alias."TenantId" = recording."TenantId"
                    AND alias."Namespace" = 'isrc'
                    AND alias."EntityKind" = 'Recording'
                    AND alias."ExternalIdHash" = encode(
                        sha256(convert_to(recording."Isrc", 'UTF8')), 'hex'));

            INSERT INTO "canonical_catalog_aliases"
                ("Id", "TenantId", "EntityKind", "CanonicalEntityId", "Namespace",
                 "ExternalId", "ExternalIdHash", "CreatedAt", "LastSeenAt")
            SELECT
                substr(encode(sha256(convert_to(
                    'canonical-alias-v1' || E'\n' || 'musicbrainz' || E'\n' ||
                    recording."TenantId"::text || E'\n' || recording."Id"::text,
                    'UTF8')), 'hex'), 1, 32)::uuid,
                recording."TenantId",
                'Recording',
                recording."Id",
                'musicbrainz',
                recording."MusicBrainzRecordingId",
                encode(sha256(convert_to(recording."MusicBrainzRecordingId", 'UTF8')), 'hex'),
                recording."CreatedAt",
                recording."UpdatedAt"
            FROM "canonical_recordings" recording
            WHERE recording."MusicBrainzRecordingId" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM "canonical_catalog_aliases" alias
                  WHERE alias."TenantId" = recording."TenantId"
                    AND alias."Namespace" = 'musicbrainz'
                    AND alias."EntityKind" = 'Recording'
                    AND alias."ExternalIdHash" = encode(
                        sha256(convert_to(recording."MusicBrainzRecordingId", 'UTF8')), 'hex'));

            INSERT INTO "canonical_catalog_aliases"
                ("Id", "TenantId", "EntityKind", "CanonicalEntityId", "Namespace",
                 "ExternalId", "ExternalIdHash", "CreatedAt", "LastSeenAt")
            SELECT
                substr(encode(sha256(convert_to(
                    'canonical-alias-v1' || E'\n' || 'provider' || E'\n' || identity."Id"::text,
                    'UTF8')), 'hex'), 1, 32)::uuid,
                identity."TenantId",
                'Recording',
                identity."CanonicalRecordingId",
                'provider:' || encode(sha256(convert_to(
                    identity."ProviderId" || E'\n' || identity."ResourceKind" || E'\n' ||
                    identity."CatalogNamespace" || E'\n' || identity."Scope" || E'\n' ||
                    COALESCE(identity."ProviderAccountId"::text, ''),
                    'UTF8')), 'hex'),
                identity."ExternalId",
                identity."ExternalIdHash",
                identity."CreatedAt",
                identity."UpdatedAt"
            FROM "provider_track_identities" identity
            WHERE NOT EXISTS (
                SELECT 1
                FROM "canonical_catalog_aliases" alias
                WHERE alias."TenantId" = identity."TenantId"
                  AND alias."Namespace" = 'provider:' || encode(sha256(convert_to(
                      identity."ProviderId" || E'\n' || identity."ResourceKind" || E'\n' ||
                      identity."CatalogNamespace" || E'\n' || identity."Scope" || E'\n' ||
                      COALESCE(identity."ProviderAccountId"::text, ''),
                      'UTF8')), 'hex')
                  AND alias."EntityKind" = 'Recording'
                  AND alias."ExternalIdHash" = identity."ExternalIdHash");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM "canonical_catalog_aliases" alias
            USING "provider_track_identities" identity
            WHERE alias."Id" = substr(encode(sha256(convert_to(
                'canonical-alias-v1' || E'\n' || 'provider' || E'\n' || identity."Id"::text,
                'UTF8')), 'hex'), 1, 32)::uuid;

            DELETE FROM "canonical_catalog_aliases" alias
            USING "canonical_recordings" recording
            WHERE alias."Id" = substr(encode(sha256(convert_to(
                'canonical-alias-v1' || E'\n' || 'isrc' || E'\n' ||
                recording."TenantId"::text || E'\n' || recording."Id"::text,
                'UTF8')), 'hex'), 1, 32)::uuid;

            DELETE FROM "canonical_catalog_aliases" alias
            USING "canonical_recordings" recording
            WHERE alias."Id" = substr(encode(sha256(convert_to(
                'canonical-alias-v1' || E'\n' || 'musicbrainz' || E'\n' ||
                recording."TenantId"::text || E'\n' || recording."Id"::text,
                'UTF8')), 'hex'), 1, 32)::uuid;

            """);
    }
}
