using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260729010000_ActivateImportedPlaylistSchedules")]
public sealed class ActivateImportedPlaylistSchedules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        UPDATE job_schedules AS schedule
        SET "Enabled" = TRUE,
            "NextRunAt" = COALESCE(
                schedule."NextRunAt",
                (extract(epoch FROM clock_timestamp()) * 10000000)::bigint + 621355968000000000),
            "UpdatedAt" = (extract(epoch FROM clock_timestamp()) * 10000000)::bigint + 621355968000000000,
            "Revision" = schedule."Revision" + 1
        FROM playlist_links AS link
        WHERE link."ScheduleId" = schedule."Id"
          AND link."RuleVersion" = 'legacy-env-import-v1'
          AND link."PolicyVersion" = 'legacy-env-import-v1'
          AND (NOT schedule."Enabled" OR schedule."NextRunAt" IS NULL);

        UPDATE playlist_links AS link
        SET "Enabled" = TRUE,
            "UpdatedAt" = (extract(epoch FROM clock_timestamp()) * 10000000)::bigint + 621355968000000000,
            "Revision" = link."Revision" + 1
        WHERE link."RuleVersion" = 'legacy-env-import-v1'
          AND link."PolicyVersion" = 'legacy-env-import-v1'
          AND NOT link."Enabled";
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
