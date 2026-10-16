using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public partial class VerifyCopiedManagedFiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "managed_storage_identity",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                MarkerDeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                MarkerFileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Generation = table.Column<Guid>(type: "TEXT", nullable: false),
                VerificationJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_managed_storage_identity", x => x.Id);
                table.CheckConstraint("CK_managed_storage_singleton", "\"Id\" = 1 AND \"Revision\" > 0");
            });
        migrationBuilder.Sql("""
            DROP TRIGGER "TR_legacy_original_file";
            CREATE TRIGGER "TR_legacy_original_file" BEFORE UPDATE ON managed_files
            WHEN EXISTS (SELECT 1 FROM legacy_media_adoptions a WHERE a."ManagedFileId"=OLD."Id")
              AND (NEW."CanonicalPath" IS NOT OLD."CanonicalPath" OR NEW."TargetRootPath" IS NOT OLD."TargetRootPath"
                OR NEW."RootId" IS NOT OLD."RootId" OR NEW."OwnerUserId" IS NOT NULL OR NEW."SourceJobId" IS NOT NULL
                OR NEW."ContentSha256" IS NOT OLD."ContentSha256" OR NEW."Length" IS NOT OLD."Length"
                OR NEW."ScopeKey" IS NOT OLD."ScopeKey" OR NEW."PlacementMethod"<>'Adopted'
                OR NEW."RemovedAt" IS NOT NULL OR NEW."IsManaged"<>1
                OR ((NEW."FileSystemDeviceId" IS NOT OLD."FileSystemDeviceId" OR NEW."FileSystemFileId" IS NOT OLD."FileSystemFileId"
                     OR NEW."FileSystemLinkCount" IS NOT OLD."FileSystemLinkCount") AND NOT EXISTS (
                    SELECT 1 FROM audit_events a
                    JOIN durable_jobs j ON j."Id"=upper(json_extract(CASE WHEN json_valid(a."DetailsJson") THEN a."DetailsJson" ELSE '{}' END,'$.jobId'))
                    JOIN managed_storage_identity s ON s."Id"=1 AND s."VerificationJobId"=j."Id"
                    WHERE a."CorrelationId"='file-identity:' || lower(replace(OLD."Id",'-','')) || ':' || CAST(OLD."Revision" AS TEXT)
                      AND a."Category"='managed-storage' AND a."Action"='file.verify-copy' AND a."Outcome"='succeeded'
                      AND json_valid(a."DetailsJson") AND j."Type"='storage.verify-files' AND j."State"='Running'
                      AND j."OwnerUserId" IS NULL AND j."ProviderAccountId" IS NULL AND j."ProviderCapability" IS NULL
                      AND j."CancellationRequestedAt" IS NULL AND j."AttemptCount"=json_extract(a."DetailsJson",'$.attempt')
                      AND lower(s."Generation")=lower(json_extract(a."DetailsJson",'$.generation'))
                      AND lower(OLD."Id")=lower(json_extract(a."DetailsJson",'$.fileId'))
                      AND lower(OLD."RootId")=lower(json_extract(a."DetailsJson",'$.rootId'))
                      AND OLD."ScopeKey"=json_extract(a."DetailsJson",'$.scopeKey')
                      AND OLD."ContentSha256"=json_extract(a."DetailsJson",'$.sha256') AND OLD."Length"=json_extract(a."DetailsJson",'$.length')
                      AND OLD."Revision"=json_extract(a."DetailsJson",'$.oldRevision') AND NEW."Revision"=OLD."Revision"+1
                      AND OLD."FileSystemDeviceId" IS json_extract(a."DetailsJson",'$.oldDevice')
                      AND OLD."FileSystemFileId" IS json_extract(a."DetailsJson",'$.oldFile')
                      AND NEW."FileSystemDeviceId"=json_extract(a."DetailsJson",'$.newDevice')
                      AND NEW."FileSystemFileId"=json_extract(a."DetailsJson",'$.newFile')
                      AND NEW."FileSystemLinkCount"=json_extract(a."DetailsJson",'$.newLinks'))))
            BEGIN SELECT RAISE(ABORT, 'CK_legacy_original_file'); END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE copied_media_down_guard (n INTEGER CONSTRAINT CK_legacy_media_requires_backup_restore CHECK(n=0));
            INSERT INTO copied_media_down_guard SELECT count(*) FROM legacy_media_adoptions;
            DROP TABLE copied_media_down_guard;
            CREATE TEMP TABLE copied_storage_down_guard (n INTEGER CONSTRAINT CK_managed_storage_requires_backup_restore CHECK(n=0));
            INSERT INTO copied_storage_down_guard SELECT count(*) FROM managed_storage_identity WHERE "VerificationJobId" IS NOT NULL;
            DROP TABLE copied_storage_down_guard;
            DROP TRIGGER "TR_legacy_original_file";
            CREATE TRIGGER "TR_legacy_original_file" BEFORE UPDATE ON managed_files
            WHEN EXISTS (SELECT 1 FROM legacy_media_adoptions a WHERE a."ManagedFileId"=OLD."Id")
            AND (NEW."CanonicalPath" IS NOT OLD."CanonicalPath" OR NEW."TargetRootPath" IS NOT OLD."TargetRootPath"
            OR NEW."RootId" IS NOT OLD."RootId" OR NEW."OwnerUserId" IS NOT NULL OR NEW."SourceJobId" IS NOT NULL
            OR NEW."ContentSha256" IS NOT OLD."ContentSha256" OR NEW."Length" IS NOT OLD."Length"
            OR NEW."FileSystemDeviceId" IS NOT OLD."FileSystemDeviceId" OR NEW."FileSystemFileId" IS NOT OLD."FileSystemFileId"
            OR NEW."ScopeKey" IS NOT OLD."ScopeKey" OR NEW."PlacementMethod"<>'Adopted'
            OR NEW."RemovedAt" IS NOT NULL OR NEW."IsManaged"<>1)
            BEGIN SELECT RAISE(ABORT, 'CK_legacy_original_file'); END;
            """);
        migrationBuilder.DropTable(name: "managed_storage_identity");
    }
}
