using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public partial class LibraryPromotions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE promotion_runs (
                Id TEXT NOT NULL PRIMARY KEY,
                OwnerUserId TEXT NOT NULL REFERENCES users(Id) ON DELETE RESTRICT,
                JobId TEXT NOT NULL REFERENCES durable_jobs(Id) ON DELETE RESTRICT,
                RequestKey TEXT NOT NULL,
                RequestJson TEXT NOT NULL,
                RootPath TEXT NOT NULL,
                PlanJson TEXT NOT NULL,
                SourceSha256 TEXT NOT NULL,
                SourceLength INTEGER NOT NULL,
                Step TEXT NOT NULL,
                Outcome TEXT NOT NULL,
                Reason TEXT NOT NULL,
                ManagedFileId TEXT NULL REFERENCES managed_files(Id) ON DELETE RESTRICT,
                ApprovedByUserId TEXT NULL,
                ApprovalReason TEXT NULL,
                CreatedAt INTEGER NOT NULL,
                Revision INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IX_promotion_runs_OwnerUserId_RequestKey ON promotion_runs (OwnerUserId, RequestKey);
            CREATE UNIQUE INDEX IX_promotion_runs_JobId ON promotion_runs (JobId);
            CREATE INDEX IX_promotion_runs_ManagedFileId ON promotion_runs (ManagedFileId);
            CREATE TABLE promotion_steps (
                RunId TEXT NOT NULL REFERENCES promotion_runs(Id) ON DELETE RESTRICT,
                Name TEXT NOT NULL,
                ContentSha256 TEXT NOT NULL,
                Length INTEGER NOT NULL,
                Reason TEXT NOT NULL,
                ResultJson TEXT NULL,
                PRIMARY KEY (RunId, Name)
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("promotion_steps");
        migrationBuilder.DropTable("promotion_runs");
    }
}
