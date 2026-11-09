using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

// Listeners' newest listens by the time Allstarr shows for them: when the song finished, or when it started if it
// never finished. SQLite needs the same coalesce in the index for those lookups to skip older history.
[DbContext(typeof(AllstarrDbContext))]
[Migration("20261108060000_ListenerActivityIndex")]
public class ListenerActivityIndex : HomeNotableActivity
{
    public const string IndexName = "IX_listening_event_scope_time";

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql($"""CREATE INDEX "{IndexName}" ON "listening_events" ("OwnerUserId", "Protocol", "BackendInstanceId", coalesce("ListenedAt", "StartedAt"), "Id");""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql($"""DROP INDEX IF EXISTS "{IndexName}";""");
}
