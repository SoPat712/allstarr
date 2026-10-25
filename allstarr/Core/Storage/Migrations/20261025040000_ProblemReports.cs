using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public partial class ProblemReports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE problem_reports (
                "Id" TEXT NOT NULL CONSTRAINT PK_problem_reports PRIMARY KEY,
                "ActorUserId" TEXT NOT NULL,
                "SubjectUserId" TEXT NULL,
                "RequestId" TEXT NOT NULL,
                "RequestFingerprint" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Note" TEXT NOT NULL,
                "ReporterName" TEXT NOT NULL,
                "ContextJson" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "CreatedAt" INTEGER NOT NULL,
                "UpdatedAt" INTEGER NOT NULL,
                "StatusChangedByUserId" TEXT NULL,
                "Revision" INTEGER NOT NULL,
                CONSTRAINT CK_problem_report_status CHECK("Status" IN ('new','seen','fixed')),
                CONSTRAINT CK_problem_report_kind CHECK("Kind" IN ('playback','seek','wrong-match','wrong-lyrics','metadata','other')),
                CONSTRAINT CK_problem_report_bounds CHECK(length("Note") BETWEEN 1 AND 2000 AND length("ReporterName") BETWEEN 1 AND 200 AND length("RequestFingerprint")=64 AND length(CAST("ContextJson" AS BLOB))<=32768 AND "Revision">0),
                CONSTRAINT CK_problem_report_context CHECK(json_valid("ContextJson") AND coalesce(json_extract("ContextJson",'$.schemaVersion')=1,0)),
                CONSTRAINT FK_problem_reports_users_ActorUserId FOREIGN KEY("ActorUserId") REFERENCES users("Id") ON DELETE RESTRICT,
                CONSTRAINT FK_problem_reports_users_SubjectUserId FOREIGN KEY("SubjectUserId") REFERENCES users("Id") ON DELETE RESTRICT,
                CONSTRAINT FK_problem_reports_users_StatusChangedByUserId FOREIGN KEY("StatusChangedByUserId") REFERENCES users("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IX_problem_reports_ActorUserId_RequestId ON problem_reports("ActorUserId","RequestId");
            CREATE INDEX IX_problem_reports_CreatedAt_Id ON problem_reports("CreatedAt","Id");
            CREATE INDEX IX_problem_reports_Status_CreatedAt_Id ON problem_reports("Status","CreatedAt","Id");
            CREATE INDEX IX_problem_reports_SubjectUserId ON problem_reports("SubjectUserId");
            CREATE INDEX IX_problem_reports_StatusChangedByUserId ON problem_reports("StatusChangedByUserId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE problem_report_downgrade_guard (Allowed INTEGER NOT NULL CONSTRAINT CK_problem_reports_require_restore CHECK(Allowed=1));
            INSERT INTO problem_report_downgrade_guard SELECT NOT EXISTS(SELECT 1 FROM problem_reports);
            DROP TABLE problem_report_downgrade_guard;
            """);
        migrationBuilder.DropTable("problem_reports");
    }
}
