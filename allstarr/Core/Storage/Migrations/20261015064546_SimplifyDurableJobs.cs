using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyDurableJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(name: "LastAttemptStartedAt", table: "durable_jobs", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<long>(name: "LastAttemptCompletedAt", table: "durable_jobs", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<string>(name: "LastAttemptOutcome", table: "durable_jobs", type: "TEXT", maxLength: 50, nullable: true);
            migrationBuilder.AddColumn<string>(name: "SubjectId", table: "audit_events", type: "TEXT", maxLength: 300, nullable: true);
            migrationBuilder.AddColumn<string>(name: "SubjectType", table: "audit_events", type: "TEXT", maxLength: 100, nullable: true);
            migrationBuilder.CreateIndex(name: "IX_audit_subject_history", table: "audit_events", columns: new[] { "SubjectType", "SubjectId", "CreatedAt" });

            migrationBuilder.Sql("""
                UPDATE durable_jobs SET
                    LastAttemptStartedAt = COALESCE((SELECT a.StartedAt FROM job_attempts a WHERE a.JobId = durable_jobs.Id ORDER BY a.AttemptNumber DESC LIMIT 1), StartedAt),
                    LastAttemptCompletedAt = (SELECT a.CompletedAt FROM job_attempts a WHERE a.JobId = durable_jobs.Id ORDER BY a.AttemptNumber DESC LIMIT 1),
                    LastAttemptOutcome = (SELECT COALESCE(a.Outcome, CASE WHEN a.CompletedAt IS NULL THEN 'running' ELSE 'interrupted' END) FROM job_attempts a WHERE a.JobId = durable_jobs.Id ORDER BY a.AttemptNumber DESC LIMIT 1);
                INSERT INTO audit_events
                    (Id, ActorUserId, Category, Action, Outcome, CorrelationId, SubjectType, SubjectId, DetailsJson, CreatedAt)
                SELECT a.Id, j.OwnerUserId, 'job-attempt', 'attempt-completed', COALESCE(a.Outcome, 'interrupted'),
                    j.CorrelationId, 'job', lower(j.Id),
                    json_object('jobId', lower(j.Id), 'jobType', j.Type, 'attemptNumber', a.AttemptNumber,
                        'startedAtTicks', a.StartedAt, 'completedAtTicks', a.CompletedAt,
                        'outcome', COALESCE(a.Outcome, 'interrupted'), 'errorCode', a.ErrorCode, 'errorMessage', a.ErrorMessage),
                    a.CompletedAt
                FROM job_attempts a JOIN durable_jobs j ON j.Id = a.JobId
                WHERE a.CompletedAt IS NOT NULL AND NOT EXISTS (SELECT 1 FROM audit_events e WHERE e.Id = a.Id);
                UPDATE audit_events SET SubjectType = 'job', SubjectId = lower(json_extract(DetailsJson, '$.jobId'))
                WHERE Category = 'job-attempt' AND Action = 'attempt-completed' AND json_valid(DetailsJson)
                    AND SubjectType IS NULL AND json_type(DetailsJson, '$.jobId') = 'text';
                """);
            migrationBuilder.DropTable(name: "job_attempts");
            // Native column removal preserves triggers that reference the job table.
            migrationBuilder.Sql("""
                ALTER TABLE durable_jobs DROP COLUMN LeaseOwner;
                ALTER TABLE durable_jobs DROP COLUMN LeaseExpiresAt;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "LeaseOwner", table: "durable_jobs", type: "TEXT", maxLength: 200, nullable: true);
            migrationBuilder.AddColumn<long>(name: "LeaseExpiresAt", table: "durable_jobs", type: "INTEGER", nullable: true);
            migrationBuilder.CreateTable(
                name: "job_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkerId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_attempts_durable_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_job_attempts_JobId_AttemptNumber",
                table: "job_attempts",
                columns: new[] { "JobId", "AttemptNumber" },
                unique: true);
            migrationBuilder.Sql("""
                INSERT INTO job_attempts (Id, JobId, AttemptNumber, WorkerId, StartedAt, CompletedAt, Outcome, ErrorCode, ErrorMessage)
                SELECT e.Id, j.Id, json_extract(e.DetailsJson, '$.attemptNumber'), 'local-process',
                    json_extract(e.DetailsJson, '$.startedAtTicks'), json_extract(e.DetailsJson, '$.completedAtTicks'),
                    e.Outcome, json_extract(e.DetailsJson, '$.errorCode'), json_extract(e.DetailsJson, '$.errorMessage')
                FROM audit_events e JOIN durable_jobs j ON lower(j.Id) = e.SubjectId
                WHERE e.Category = 'job-attempt' AND e.Action = 'attempt-completed' AND e.SubjectType = 'job'
                    AND json_valid(e.DetailsJson) AND json_type(e.DetailsJson, '$.attemptNumber') = 'integer'
                    AND json_type(e.DetailsJson, '$.startedAtTicks') = 'integer'
                    AND json_type(e.DetailsJson, '$.completedAtTicks') = 'integer';
                WITH latest AS MATERIALIZED (
                    SELECT j.*, lower(hex(randomblob(16))) AS new_id FROM durable_jobs j
                    WHERE j.AttemptCount > 0 AND NOT EXISTS
                        (SELECT 1 FROM job_attempts a WHERE a.JobId = j.Id AND a.AttemptNumber = j.AttemptCount)
                )
                INSERT INTO job_attempts (Id, JobId, AttemptNumber, WorkerId, StartedAt, CompletedAt, Outcome, ErrorCode, ErrorMessage)
                SELECT upper(substr(new_id, 1, 8) || '-' || substr(new_id, 9, 4) || '-' || substr(new_id, 13, 4) || '-' ||
                    substr(new_id, 17, 4) || '-' || substr(new_id, 21)), Id, AttemptCount, 'local-process',
                    COALESCE(LastAttemptStartedAt, StartedAt, UpdatedAt), COALESCE(LastAttemptCompletedAt, CompletedAt, UpdatedAt),
                    CASE WHEN State != 'Running' THEN COALESCE(LastAttemptOutcome, 'interrupted')
                        WHEN CancellationRequestedAt IS NULL THEN 'interrupted' ELSE 'cancelled' END,
                    CASE WHEN State != 'Running' THEN LastErrorCode
                        WHEN CancellationRequestedAt IS NULL THEN 'worker_interrupted' ELSE NULL END,
                    CASE WHEN State != 'Running' THEN LastErrorMessage
                        WHEN CancellationRequestedAt IS NULL THEN 'Work was interrupted by a schema rollback and is ready to resume.' ELSE NULL END
                FROM latest;
                UPDATE durable_jobs SET State = CASE WHEN CancellationRequestedAt IS NULL THEN 'Pending' ELSE 'Cancelled' END,
                    AvailableAt = UpdatedAt, CompletedAt = CASE WHEN CancellationRequestedAt IS NULL THEN NULL ELSE UpdatedAt END,
                    Revision = Revision + 1 WHERE State = 'Running';
                """);
            migrationBuilder.DropIndex(name: "IX_audit_subject_history", table: "audit_events");
            migrationBuilder.Sql("""
                ALTER TABLE durable_jobs DROP COLUMN LastAttemptStartedAt;
                ALTER TABLE durable_jobs DROP COLUMN LastAttemptCompletedAt;
                ALTER TABLE durable_jobs DROP COLUMN LastAttemptOutcome;
                ALTER TABLE audit_events DROP COLUMN SubjectType;
                ALTER TABLE audit_events DROP COLUMN SubjectId;
                """);
        }
    }
}
