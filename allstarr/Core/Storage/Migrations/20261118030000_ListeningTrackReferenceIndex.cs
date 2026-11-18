using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

// Play counts are looked up by recording, library song or the item id a play was reported with. Without an index on
// that last one SQLite walked a listener's whole history for every song read; with it each condition uses its own.
[DbContext(typeof(AllstarrDbContext))]
[Migration("20261118030000_ListeningTrackReferenceIndex")]
public class ListeningTrackReferenceIndex : MyLibrariesPreference
{
    public const string TrackReferenceIndexName = "IX_listening_events_TrackReference";

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql($"""CREATE INDEX "{TrackReferenceIndexName}" ON "listening_events" ("TrackReference");""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql($"""DROP INDEX IF EXISTS "{TrackReferenceIndexName}";""");
}
