using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260804080000_BackfillV3CompatibilityState")]
public sealed class BackfillV3CompatibilityState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            WITH ranked AS (
                SELECT "TenantId", "CreatedAt", "UpdatedAt",
                       CASE "Key"
                           WHEN 'AppleDownload:Quality' THEN CASE lower("ValueJson"::jsonb #>> '{}')
                               WHEN 'aac-96' THEN 0 WHEN 'aac-320' THEN 1 WHEN 'alac-16-44' THEN 2
                               WHEN 'alac-24-48' THEN 3 WHEN 'alac-24-96' THEN 3 WHEN 'alac-24-192' THEN 4 ELSE 2 END
                           WHEN 'Deezer:Quality' THEN CASE upper("ValueJson"::jsonb #>> '{}')
                               WHEN 'MP3_128' THEN 0 WHEN '128' THEN 0 WHEN 'MP3_320' THEN 1 WHEN '320' THEN 1 ELSE 4 END
                           ELSE CASE upper("ValueJson"::jsonb #>> '{}')
                               WHEN 'MP3_320' THEN 1 WHEN 'MP3' THEN 1 WHEN 'FLAC_16' THEN 2 WHEN 'CD' THEN 2
                               WHEN 'FLAC_24_LOW' THEN 3 WHEN '24_96' THEN 3 ELSE 4 END
                       END AS quality_rank
                  FROM tenant_runtime_settings
                 WHERE "Key" IN ('AppleDownload:Quality', 'Deezer:Quality', 'Qobuz:Quality')
            ), shared AS (
                SELECT "TenantId", MIN(quality_rank) AS quality_rank,
                       MIN("CreatedAt") AS created_at, MAX("UpdatedAt") AS updated_at
                  FROM ranked
                 GROUP BY "TenantId"
            )
            INSERT INTO tenant_runtime_settings
                ("Id", "TenantId", "Key", "ValueType", "ValueJson", "Source", "UpdatedByUserId",
                 "CreatedAt", "UpdatedAt", "Revision")
            SELECT md5(shared."TenantId"::text || '|Audio:Quality')::uuid, shared."TenantId", 'Audio:Quality', 'String',
                   to_jsonb(CASE shared.quality_rank WHEN 0 THEN 'DataSaver' WHEN 1 THEN 'High'
                              WHEN 2 THEN 'CdLossless' WHEN 3 THEN 'HiResLossless' ELSE 'BestAvailable' END)::text,
                   'v3-compatibility-migration', NULL, shared.created_at, shared.updated_at, 1
              FROM shared
             WHERE NOT EXISTS (
                 SELECT 1 FROM tenant_runtime_settings current
                  WHERE current."TenantId" = shared."TenantId" AND current."Key" = 'Audio:Quality');

            WITH legacy AS (
                SELECT "Id", 'spotiflac-' || "ExtensionId" AS canonical_id
                  FROM extension_packages
                 WHERE "ExtensionId" NOT LIKE 'spotiflac-%'
                   AND "ManifestJson"::jsonb ->> 'compatibility' = 'spotiflac-v1'
            )
            UPDATE extension_logs log
               SET "ExtensionId" = legacy.canonical_id
              FROM legacy
             WHERE log."ExtensionPackageId" = legacy."Id";

            UPDATE extension_packages
               SET "ExtensionId" = 'spotiflac-' || "ExtensionId",
                   "Revision" = CASE WHEN "Revision" < 1 THEN 1 ELSE "Revision" + 1 END
             WHERE "ExtensionId" NOT LIKE 'spotiflac-%'
               AND "ManifestJson"::jsonb ->> 'compatibility' = 'spotiflac-v1';

            UPDATE recommendation_candidates
               SET "SourceRevision" = 'run:' || replace("RunId"::text, '-', ''),
                   "Revision" = CASE WHEN "Revision" < 1 THEN 1 ELSE "Revision" + 1 END
             WHERE "SourceRevision" = 'legacy';

            UPDATE recommendation_candidates
               SET "Revision" = 1
             WHERE "Revision" < 1;

            WITH exact_account AS (
                SELECT candidate."Id", MIN(account."Id"::text)::uuid AS account_id
                  FROM recommendation_candidates candidate
                  JOIN recommendation_runs run ON run."Id" = candidate."RunId"
                  JOIN provider_accounts account ON account."ProviderId" = candidate."Source"
                   AND (account."Scope" = 'Global'
                     OR (account."Scope" = 'User' AND account."TenantId" = run."TenantId"
                         AND account."OwnerUserId" = run."OwnerUserId")
                     OR (account."Scope" = 'Library' AND account."TenantId" = run."TenantId"
                         AND account."LibraryScopeId" = run."LibraryScopeId"))
                 WHERE candidate."ProviderAccountId" IS NULL
                 GROUP BY candidate."Id"
                HAVING COUNT(*) = 1
            )
            UPDATE recommendation_candidates candidate
               SET "ProviderAccountId" = exact_account.account_id,
                   "Revision" = candidate."Revision" + 1
              FROM exact_account
             WHERE candidate."Id" = exact_account."Id";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Data stays in forms understood by the previous build. The legacy quality rows and
        // manifests remain intact, so rollback and idempotent reapplication need no reverse write.
    }
}
