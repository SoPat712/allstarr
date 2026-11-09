using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

// Library writes change the tracks table's count, revisions or index and update times, which take a few milliseconds
// to read, so a copy of the library indexed for matching can be kept until one of them moves.
internal static class LibraryTrackFingerprint
{
    public static Task<string> ReadAsync(AllstarrDbContext db, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<string>("""
            SELECT COUNT(*) || ':' || COALESCE(SUM("Revision"), 0) || ':' || COALESCE(MAX("UpdatedAt"), 0) || ':' ||
                COALESCE(MAX("IndexedAt"), 0) AS "Value" FROM library_tracks
            """).SingleAsync(cancellationToken);
}
