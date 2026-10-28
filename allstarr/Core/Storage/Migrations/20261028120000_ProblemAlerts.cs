using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public partial class ProblemAlerts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE notification_channels (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "OwnerUserId" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL,
                "Household" INTEGER NOT NULL,
                "DestinationReferenceId" TEXT NOT NULL,
                "HealthScheduleId" TEXT NULL,
                "Revision" INTEGER NOT NULL,
                FOREIGN KEY("OwnerUserId") REFERENCES users("Id") ON DELETE RESTRICT,
                FOREIGN KEY("DestinationReferenceId") REFERENCES secret_references("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IX_notification_channels_HealthScheduleId ON notification_channels("HealthScheduleId");
            CREATE INDEX IX_notification_channels_OwnerUserId ON notification_channels("OwnerUserId");
            CREATE INDEX IX_notification_channels_DestinationReferenceId ON notification_channels("DestinationReferenceId");
            CREATE TABLE problem_incidents (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ScopeKey" TEXT NOT NULL,
                "OwnerUserId" TEXT NULL,
                "Resource" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "DetailJson" TEXT NOT NULL,
                "Severity" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "Transition" INTEGER NOT NULL,
                "Failures" INTEGER NOT NULL,
                "Successes" INTEGER NOT NULL,
                "WindowStartedAt" INTEGER NULL,
                "LastObservedAt" INTEGER NOT NULL,
                "OpenedAt" INTEGER NULL,
                "ResolvedAt" INTEGER NULL,
                "LastNotifiedAt" INTEGER NULL,
                "Revision" INTEGER NOT NULL,
                FOREIGN KEY("OwnerUserId") REFERENCES users("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IX_problem_incidents_ScopeKey_Resource_Kind ON problem_incidents("ScopeKey","Resource","Kind");
            CREATE INDEX IX_problem_incidents_OwnerUserId ON problem_incidents("OwnerUserId");
            CREATE TABLE notification_deliveries (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "IncidentId" TEXT NULL,
                "Transition" INTEGER NOT NULL,
                "ChannelId" TEXT NOT NULL,
                "ChannelRevision" INTEGER NOT NULL,
                "JobId" TEXT NOT NULL,
                "HealthRunId" TEXT NULL,
                "Event" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "DetailJson" TEXT NOT NULL,
                "Severity" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "Attempt" INTEGER NOT NULL,
                "CreatedAt" INTEGER NOT NULL,
                "NextAttemptAt" INTEGER NULL,
                "SafeError" TEXT NULL,
                FOREIGN KEY("IncidentId") REFERENCES problem_incidents("Id") ON DELETE RESTRICT,
                FOREIGN KEY("ChannelId") REFERENCES notification_channels("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IX_notification_deliveries_IncidentId_Transition_ChannelId ON notification_deliveries("IncidentId","Transition","ChannelId");
            CREATE UNIQUE INDEX IX_notification_deliveries_JobId ON notification_deliveries("JobId");
            CREATE INDEX IX_notification_deliveries_ChannelId ON notification_deliveries("ChannelId");
            CREATE TABLE problem_alert_configuration (
                "Id" INTEGER NOT NULL PRIMARY KEY,
                "ThresholdsJson" TEXT NOT NULL,
                "AllowedAuthoritiesJson" TEXT NOT NULL,
                "Revision" INTEGER NOT NULL
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE alert_downgrade_guard (Allowed INTEGER NOT NULL CHECK(Allowed=1));
            INSERT INTO alert_downgrade_guard SELECT NOT EXISTS(SELECT 1 FROM notification_channels)
                AND NOT EXISTS(SELECT 1 FROM problem_incidents) AND NOT EXISTS(SELECT 1 FROM notification_deliveries)
                AND NOT EXISTS(SELECT 1 FROM problem_alert_configuration);
            DROP TABLE alert_downgrade_guard;
            """);
        migrationBuilder.DropTable("notification_deliveries");
        migrationBuilder.DropTable("problem_incidents");
        migrationBuilder.DropTable("notification_channels");
        migrationBuilder.DropTable("problem_alert_configuration");
    }
}
