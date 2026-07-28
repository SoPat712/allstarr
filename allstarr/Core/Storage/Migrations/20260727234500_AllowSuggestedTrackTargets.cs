using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260727234500_AllowSuggestedTrackTargets")]
public sealed class AllowSuggestedTrackTargets : Migration
{
    private const string SuggestedShape =
        "(\"State\" IN ('Accepted', 'Suggested') AND (\"LibraryTrackId\" IS NOT NULL OR \"CanonicalRecordingId\" IS NOT NULL)) OR " +
        "(\"State\" = 'Pinned' AND \"LibraryTrackId\" IS NOT NULL) OR " +
        "(\"State\" IN ('Unresolved', 'Rejected', 'Ambiguous') AND \"LibraryTrackId\" IS NULL)";

    private const string AcceptedShape =
        "(\"State\" = 'Accepted' AND (\"LibraryTrackId\" IS NOT NULL OR \"CanonicalRecordingId\" IS NOT NULL)) OR " +
        "(\"State\" = 'Pinned' AND \"LibraryTrackId\" IS NOT NULL) OR " +
        "(\"State\" IN ('Unresolved', 'Suggested', 'Rejected', 'Ambiguous') AND \"LibraryTrackId\" IS NULL)";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_track_matches_selected_shape",
            table: "track_matches");
        migrationBuilder.AddCheckConstraint(
            name: "CK_track_matches_selected_shape",
            table: "track_matches",
            sql: SuggestedShape);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_track_matches_selected_shape",
            table: "track_matches");
        migrationBuilder.AddCheckConstraint(
            name: "CK_track_matches_selected_shape",
            table: "track_matches",
            sql: AcceptedShape);
    }
}
