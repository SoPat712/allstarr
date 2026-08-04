using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260803210000_OptimizeListeningAnalyticsIndex")]
public sealed class OptimizeListeningAnalyticsIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_listening_event_scope_history",
            table: "listening_events");

        migrationBuilder.CreateIndex(
            name: "IX_listening_event_scope_history",
            table: "listening_events",
            columns:
            [
                "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId",
                "State", "ListenedAt", "Id"
            ]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_listening_event_scope_history",
            table: "listening_events");

        migrationBuilder.CreateIndex(
            name: "IX_listening_event_scope_history",
            table: "listening_events",
            columns: ["TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId", "ListenedAt"]);
    }
}
