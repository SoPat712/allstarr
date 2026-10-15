using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AdoptLegacyMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legacy_media_adoptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ManagedFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Kept = table.Column<bool>(type: "INTEGER", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_media_adoptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_legacy_media_adoptions_canonical_recordings_CanonicalRecordingId",
                        column: x => x.CanonicalRecordingId,
                        principalTable: "canonical_recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_legacy_media_adoptions_legacy_env_imports_ImportId",
                        column: x => x.ImportId,
                        principalTable: "legacy_env_imports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_legacy_media_adoptions_managed_files_ManagedFileId",
                        column: x => x.ManagedFileId,
                        principalTable: "managed_files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_legacy_media_adoptions_CanonicalRecordingId",
                table: "legacy_media_adoptions",
                column: "CanonicalRecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_legacy_media_adoptions_ImportId",
                table: "legacy_media_adoptions",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_legacy_media_adoptions_ManagedFileId",
                table: "legacy_media_adoptions",
                column: "ManagedFileId",
                unique: true);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_legacy_media_provenance" BEFORE INSERT ON legacy_media_adoptions
                WHEN NOT EXISTS (
                    SELECT 1 FROM managed_files f
                    JOIN legacy_env_imports i ON i."Id"=NEW."ImportId"
                    JOIN users u ON u."Id"=i."ActorUserId"
                    JOIN managed_file_references r ON r."ManagedFileId"=f."Id"
                    WHERE f."Id"=NEW."ManagedFileId" AND f."IsManaged"=1 AND f."RemovedAt" IS NULL
                      AND f."PlacementMethod"='Adopted' AND f."OwnerUserId" IS NULL AND f."SourceJobId" IS NULL
                      AND f."FileSystemDeviceId" IS NOT NULL AND f."FileSystemFileId" IS NOT NULL
                      AND f."ScopeKey"='managed:shared:' || lower(replace(f."RootId",'-',''))
                      AND r."ReferenceKey"='legacy-original:' || lower(replace(f."Id",'-',''))
                      AND r."ScopeKey"=f."ScopeKey" AND r."OwnerUserId" IS NULL AND r."ReleasedAt" IS NULL
                      AND u."Enabled"=1 AND u."IsAdmin"=1)
                BEGIN SELECT RAISE(ABORT, 'CK_legacy_media_provenance'); END;
                CREATE TRIGGER "TR_legacy_media_immutable_update" BEFORE UPDATE ON legacy_media_adoptions
                BEGIN SELECT RAISE(ABORT, 'CK_legacy_media_immutable'); END;
                CREATE TRIGGER "TR_legacy_media_immutable_delete" BEFORE DELETE ON legacy_media_adoptions
                BEGIN SELECT RAISE(ABORT, 'CK_legacy_media_immutable'); END;
                CREATE TRIGGER "TR_legacy_original_file" BEFORE UPDATE ON managed_files
                WHEN EXISTS (SELECT 1 FROM legacy_media_adoptions a WHERE a."ManagedFileId"=OLD."Id")
                  AND (NEW."CanonicalPath" IS NOT OLD."CanonicalPath" OR NEW."TargetRootPath" IS NOT OLD."TargetRootPath"
                    OR NEW."RootId" IS NOT OLD."RootId" OR NEW."OwnerUserId" IS NOT NULL OR NEW."SourceJobId" IS NOT NULL
                    OR NEW."ContentSha256" IS NOT OLD."ContentSha256" OR NEW."Length" IS NOT OLD."Length"
                    OR NEW."FileSystemDeviceId" IS NOT OLD."FileSystemDeviceId" OR NEW."FileSystemFileId" IS NOT OLD."FileSystemFileId"
                    OR NEW."ScopeKey" IS NOT OLD."ScopeKey" OR NEW."PlacementMethod"<>'Adopted'
                    OR NEW."RemovedAt" IS NOT NULL OR NEW."IsManaged"<>1)
                BEGIN SELECT RAISE(ABORT, 'CK_legacy_original_file'); END;
                CREATE TRIGGER "TR_legacy_original_reference" BEFORE UPDATE ON managed_file_references
                WHEN EXISTS (SELECT 1 FROM legacy_media_adoptions a WHERE a."ManagedFileId"=OLD."ManagedFileId")
                  AND OLD."ReferenceKey"='legacy-original:' || lower(replace(OLD."ManagedFileId",'-',''))
                  AND (NEW."ManagedFileId" IS NOT OLD."ManagedFileId" OR NEW."ReferenceKey" IS NOT OLD."ReferenceKey"
                    OR NEW."ScopeKey" IS NOT OLD."ScopeKey" OR NEW."OwnerUserId" IS NOT NULL OR NEW."ReleasedAt" IS NOT NULL)
                BEGIN SELECT RAISE(ABORT, 'CK_legacy_original_reference'); END;
                CREATE TRIGGER "TR_legacy_original_reference_delete" BEFORE DELETE ON managed_file_references
                WHEN EXISTS (SELECT 1 FROM legacy_media_adoptions a WHERE a."ManagedFileId"=OLD."ManagedFileId")
                  AND OLD."ReferenceKey"='legacy-original:' || lower(replace(OLD."ManagedFileId",'-',''))
                BEGIN SELECT RAISE(ABORT, 'CK_legacy_original_reference'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE legacy_media_down_guard (n INTEGER CONSTRAINT CK_legacy_media_requires_backup_restore CHECK(n=0));
                INSERT INTO legacy_media_down_guard SELECT
                    (SELECT count(*) FROM legacy_media_adoptions) + (SELECT count(*) FROM managed_files WHERE "PlacementMethod"='Adopted');
                DROP TABLE legacy_media_down_guard;
                DROP TRIGGER "TR_legacy_media_provenance";
                DROP TRIGGER "TR_legacy_media_immutable_update";
                DROP TRIGGER "TR_legacy_media_immutable_delete";
                DROP TRIGGER "TR_legacy_original_file";
                DROP TRIGGER "TR_legacy_original_reference";
                DROP TRIGGER "TR_legacy_original_reference_delete";
                """);
            migrationBuilder.DropTable(
                name: "legacy_media_adoptions");
        }
    }
}
