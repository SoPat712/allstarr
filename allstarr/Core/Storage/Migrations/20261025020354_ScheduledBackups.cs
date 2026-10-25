using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE backup_upgrade_guard (Allowed INTEGER NOT NULL CONSTRAINT CK_backup_upgrade CHECK(Allowed=1));
                INSERT INTO backup_upgrade_guard SELECT NOT EXISTS(SELECT 1 FROM job_schedules WHERE "JobType"='storage.backup');
                DROP TABLE backup_upgrade_guard;
                """);
            ChangeOwnerNullability(migrationBuilder, true);
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_backup_schedule_owner_insert BEFORE INSERT ON job_schedules
                WHEN (NEW."JobType"='storage.backup' AND NEW."OwnerUserId" IS NOT NULL)
                    OR (NEW."JobType"<>'storage.backup' AND NEW."OwnerUserId" IS NULL)
                BEGIN SELECT RAISE(ABORT,'schedule_owner_invalid'); END;
                CREATE TRIGGER TR_backup_schedule_owner_update BEFORE UPDATE OF "OwnerUserId","JobType" ON job_schedules
                WHEN (NEW."JobType"='storage.backup' AND NEW."OwnerUserId" IS NOT NULL)
                    OR (NEW."JobType"<>'storage.backup' AND NEW."OwnerUserId" IS NULL)
                BEGIN SELECT RAISE(ABORT,'schedule_owner_invalid'); END;
                CREATE UNIQUE INDEX IX_household_backup_schedule ON job_schedules("JobType")
                    WHERE "JobType"='storage.backup' AND "OwnerUserId" IS NULL;
                """);

            migrationBuilder.CreateTable(
                name: "backup_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Origin = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduledFor = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Directory = table.Column<string>(type: "TEXT", nullable: false),
                    PolicyJson = table.Column<string>(type: "TEXT", nullable: false),
                    StorageBinding = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    DirectoryDeviceId = table.Column<string>(type: "TEXT", nullable: true),
                    DirectoryFileId = table.Column<string>(type: "TEXT", nullable: true),
                    FileDeviceId = table.Column<string>(type: "TEXT", nullable: true),
                    FileId = table.Column<string>(type: "TEXT", nullable: true),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FileWriteTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    SchemaVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ApplicationVersion = table.Column<string>(type: "TEXT", nullable: false),
                    VerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RemovedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastFailureCode = table.Column<string>(type: "TEXT", nullable: true),
                    LastFailureMessage = table.Column<string>(type: "TEXT", nullable: true),
                    LastFailureAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_artifacts", x => x.Id);
                    table.CheckConstraint("CK_backup_origin", "\"Origin\" IN ('manual','scheduled')");
                    table.ForeignKey(
                        name: "FK_backup_artifacts_durable_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backup_artifacts_CreatedAt",
                table: "backup_artifacts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_backup_artifacts_JobId",
                table: "backup_artifacts",
                column: "JobId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE backup_downgrade_guard (Allowed INTEGER NOT NULL CONSTRAINT CK_backups_require_restore CHECK(Allowed=1));
                INSERT INTO backup_downgrade_guard SELECT NOT EXISTS(SELECT 1 FROM backup_artifacts)
                    AND NOT EXISTS(SELECT 1 FROM job_schedules WHERE "JobType"='storage.backup')
                    AND NOT EXISTS(SELECT 1 FROM durable_jobs WHERE "Type"='storage.backup');
                DROP TABLE backup_downgrade_guard;
                """);
            migrationBuilder.Sql("""
                DROP TRIGGER TR_backup_schedule_owner_insert;
                DROP TRIGGER TR_backup_schedule_owner_update;
                DROP INDEX IX_household_backup_schedule;
                """);
            ChangeOwnerNullability(migrationBuilder, false);
            migrationBuilder.DropTable(name: "backup_artifacts");
        }

        private static void ChangeOwnerNullability(MigrationBuilder migrationBuilder, bool nullable)
        {
            const string original = """
                CREATE TABLE "job_schedules" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_job_schedules" PRIMARY KEY,
                    "OwnerUserId" TEXT NOT NULL,
                    "JobType" TEXT NOT NULL,
                    "CronExpression" TEXT NOT NULL,
                    "TimeZoneId" TEXT NOT NULL,
                    "OverlapPolicy" TEXT NOT NULL,
                    "MisfirePolicy" TEXT NOT NULL,
                    "RetryPolicyJson" TEXT NOT NULL,
                    "PayloadTemplateJson" TEXT NOT NULL DEFAULT '{}',
                    "NextRunAt" INTEGER NULL,
                    "Enabled" INTEGER NOT NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "UpdatedAt" INTEGER NOT NULL,
                    "Revision" INTEGER NOT NULL,
                    CONSTRAINT "FK_JobSchedule_User" FOREIGN KEY ("OwnerUserId") REFERENCES "users" ("Id") ON DELETE RESTRICT
                )
                """;
            var changed = original.Replace("\"OwnerUserId\" TEXT NOT NULL", "\"OwnerUserId\" TEXT NULL", StringComparison.Ordinal);
            var expected = nullable ? original : changed;
            var replacement = nullable ? changed : original;
            var version = nullable ? 2026102501 : 2026102502;
            static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
            migrationBuilder.Sql($"""
                /* backup-owner-constraint */
                CREATE TEMP TABLE backup_owner_schema_guard (Allowed INTEGER NOT NULL CONSTRAINT CK_backup_owner_schema_exact CHECK(Allowed=1));
                INSERT INTO backup_owner_schema_guard SELECT COUNT(*)=1 AND MIN(sql)={Literal(expected)}
                    AND (SELECT schema_version FROM pragma_schema_version)<>{version}
                    FROM sqlite_schema WHERE type='table' AND name='job_schedules';
                PRAGMA writable_schema=ON;
                UPDATE sqlite_schema SET sql={Literal(replacement)}
                    WHERE type='table' AND name='job_schedules' AND sql={Literal(expected)};
                PRAGMA writable_schema=RESET;
                INSERT INTO backup_owner_schema_guard VALUES(changes());
                PRAGMA schema_version={version};
                INSERT INTO backup_owner_schema_guard SELECT
                    (SELECT schema_version FROM pragma_schema_version)={version}
                    AND (SELECT sql FROM sqlite_schema WHERE type='table' AND name='job_schedules')={Literal(replacement)}
                    AND NOT EXISTS(SELECT 1 FROM pragma_foreign_key_check)
                    AND (SELECT COUNT(*) FROM pragma_integrity_check WHERE integrity_check<>'ok')=0;
                DROP TABLE backup_owner_schema_guard;
                """);
        }
    }
}
