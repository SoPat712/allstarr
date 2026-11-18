using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

// Match reviews are cached until a total over every snapshot and match changes. Those totals read each wide row (about
// 80 ms on every song details, mix and track read); narrow indexes holding just the summed columns cover them instead.
[DbContext(typeof(AllstarrDbContext))]
[Migration("20261118070000_MatchReviewRevisionIndexes")]
public class MatchReviewRevisionIndexes : ListeningTrackReferenceIndex
{
    public const string SnapshotRevisionIndexName = "IX_external_metadata_snapshots_SnapshotVersion_RetrievedAt";
    public const string MatchRevisionIndexName = "IX_track_matches_Revision_DecidedAt";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""CREATE INDEX "{SnapshotRevisionIndexName}" ON "external_metadata_snapshots" ("SnapshotVersion", "RetrievedAt");""");
        migrationBuilder.Sql($"""CREATE INDEX "{MatchRevisionIndexName}" ON "track_matches" ("Revision", "DecidedAt");""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""DROP INDEX IF EXISTS "{MatchRevisionIndexName}";""");
        migrationBuilder.Sql($"""DROP INDEX IF EXISTS "{SnapshotRevisionIndexName}";""");
    }
}
