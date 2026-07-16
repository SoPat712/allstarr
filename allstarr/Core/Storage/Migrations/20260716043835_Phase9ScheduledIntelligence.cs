using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Phase9ScheduledIntelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isPostgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleId",
                table: "recommendation_runs",
                type: isPostgres ? "uuid" : "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ScheduledFor",
                table: "recommendation_runs",
                type: isPostgres ? "bigint" : "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayloadTemplateJson",
                table: "job_schedules",
                type: isPostgres ? "text" : "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleId",
                table: "generated_sets",
                type: isPostgres ? "uuid" : "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_run_schedule_history",
                table: "recommendation_runs",
                columns: new[] { "TenantId", "OwnerUserId", "ScheduleId", "ScheduledFor" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_run_schedule_occurrence",
                table: "recommendation_runs",
                columns: new[] { "ScheduleId", "ScheduledFor" },
                unique: true,
                filter: "\"ScheduleId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_recommendation_run_schedule_pair",
                table: "recommendation_runs",
                sql: "(\"ScheduleId\" IS NULL AND \"ScheduledFor\" IS NULL) OR (\"ScheduleId\" IS NOT NULL AND \"ScheduledFor\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_generated_set_schedule",
                table: "generated_sets",
                column: "ScheduleId");

            migrationBuilder.AddForeignKey(
                name: "FK_generated_sets_job_schedules_ScheduleId",
                table: "generated_sets",
                column: "ScheduleId",
                principalTable: "job_schedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_recommendation_runs_job_schedules_ScheduleId",
                table: "recommendation_runs",
                column: "ScheduleId",
                principalTable: "job_schedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_generated_sets_job_schedules_ScheduleId",
                table: "generated_sets");

            migrationBuilder.DropForeignKey(
                name: "FK_recommendation_runs_job_schedules_ScheduleId",
                table: "recommendation_runs");

            migrationBuilder.DropIndex(
                name: "IX_recommendation_run_schedule_history",
                table: "recommendation_runs");

            migrationBuilder.DropIndex(
                name: "IX_recommendation_run_schedule_occurrence",
                table: "recommendation_runs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_recommendation_run_schedule_pair",
                table: "recommendation_runs");

            migrationBuilder.DropIndex(
                name: "IX_generated_set_schedule",
                table: "generated_sets");

            migrationBuilder.DropColumn(
                name: "ScheduleId",
                table: "recommendation_runs");

            migrationBuilder.DropColumn(
                name: "ScheduledFor",
                table: "recommendation_runs");

            migrationBuilder.DropColumn(
                name: "PayloadTemplateJson",
                table: "job_schedules");

            migrationBuilder.DropColumn(
                name: "ScheduleId",
                table: "generated_sets");
        }
    }
}
