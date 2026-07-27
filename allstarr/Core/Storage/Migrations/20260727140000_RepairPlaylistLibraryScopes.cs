using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260727140000_RepairPlaylistLibraryScopes")]
public sealed class RepairPlaylistLibraryScopes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        WITH resolved AS (
            SELECT link."TenantId", link."Id", link."ScheduleId", min(track."LibraryScopeId") AS scope
            FROM playlist_links AS link
            JOIN library_tracks AS track
              ON track."TenantId" = link."TenantId"
             AND track."OwnerUserId" = link."OwnerUserId"
             AND track."BackendInstanceId" = link."TargetBackendInstanceId"
             AND (track."Protocol" = link."TargetProtocol"
                  OR link."TargetProtocol" = 'subsonic' AND track."Protocol" IN ('opensubsonic', 'navidrome'))
            WHERE NOT EXISTS (
                SELECT 1 FROM library_tracks AS current_track
                WHERE current_track."TenantId" = link."TenantId"
                  AND current_track."OwnerUserId" = link."OwnerUserId"
                  AND current_track."BackendInstanceId" = link."TargetBackendInstanceId"
                  AND current_track."LibraryScopeId" = link."LibraryScopeId")
            GROUP BY link."TenantId", link."Id", link."ScheduleId"
            HAVING count(DISTINCT track."LibraryScopeId") = 1
        )
        UPDATE job_schedules AS schedule
        SET "LibraryScopeId" = resolved.scope
        FROM resolved
        WHERE schedule."TenantId" = resolved."TenantId"
          AND schedule."Id" = resolved."ScheduleId";

        WITH resolved AS (
            SELECT link."TenantId", link."Id", min(track."LibraryScopeId") AS scope
            FROM playlist_links AS link
            JOIN library_tracks AS track
              ON track."TenantId" = link."TenantId"
             AND track."OwnerUserId" = link."OwnerUserId"
             AND track."BackendInstanceId" = link."TargetBackendInstanceId"
             AND (track."Protocol" = link."TargetProtocol"
                  OR link."TargetProtocol" = 'subsonic' AND track."Protocol" IN ('opensubsonic', 'navidrome'))
            WHERE NOT EXISTS (
                SELECT 1 FROM library_tracks AS current_track
                WHERE current_track."TenantId" = link."TenantId"
                  AND current_track."OwnerUserId" = link."OwnerUserId"
                  AND current_track."BackendInstanceId" = link."TargetBackendInstanceId"
                  AND current_track."LibraryScopeId" = link."LibraryScopeId")
            GROUP BY link."TenantId", link."Id"
            HAVING count(DISTINCT track."LibraryScopeId") = 1
        )
        UPDATE playlist_links AS link
        SET "LibraryScopeId" = resolved.scope,
            "UpdatedAt" = (extract(epoch FROM clock_timestamp()) * 10000000)::bigint + 621355968000000000,
            "Revision" = link."Revision" + 1
        FROM resolved
        WHERE link."TenantId" = resolved."TenantId"
          AND link."Id" = resolved."Id";
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
