using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public static class LibraryTrackAccess
{
    /// <summary>The library songs this viewer may read. Songs the media server no longer has are left out unless asked
    /// for, which only the work that hands them over to a copy added again does.</summary>
    public static IQueryable<LibraryTrackRecord> Query(AllstarrDbContext db, BackendLibraryAccessContext access, bool includeGone = false) =>
        access.Context == null ? db.LibraryTracks.AsNoTracking().Where(_ => false) : Query(db, access.Context, access.Access, includeGone);

    public static IQueryable<LibraryTrackRecord> Query(AllstarrDbContext db,
        ProtocolExecutionContext context, BackendLibraryAccess access, bool includeGone = false)
    {
        var principal = context.Principal ?? throw new UnauthorizedAccessException("A linked viewer is required.");
        var protocol = context.Protocol.ToString().ToLowerInvariant();
        var libraries = access.Succeeded ? access.LibraryIds : [];
        var gone = LibraryTrackRecord.Gone;
        return db.LibraryTracks.AsNoTracking().Where(track =>
            track.Protocol == protocol &&
            track.BackendInstanceId == context.BackendInstanceId && libraries.Contains(track.BackendLibraryId) &&
            (includeGone || track.IndexedAt != gone));
    }

    public static bool Allows(LibraryTrackRecord track, ProtocolExecutionContext context, BackendLibraryAccess access) =>
        context.Principal != null &&
        track.Protocol == context.Protocol.ToString().ToLowerInvariant() &&
        track.BackendInstanceId == context.BackendInstanceId && access.Allows(track.BackendLibraryId);
}
