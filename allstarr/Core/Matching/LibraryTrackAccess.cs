using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public static class LibraryTrackAccess
{
    public static IQueryable<LibraryTrackRecord> Query(AllstarrDbContext db, BackendLibraryAccessContext access) =>
        access.Context == null ? db.LibraryTracks.AsNoTracking().Where(_ => false) : Query(db, access.Context, access.Access);

    public static IQueryable<LibraryTrackRecord> Query(AllstarrDbContext db,
        ProtocolExecutionContext context, BackendLibraryAccess access)
    {
        var principal = context.Principal ?? throw new UnauthorizedAccessException("A linked viewer is required.");
        var protocol = context.Protocol.ToString().ToLowerInvariant();
        var libraries = access.Succeeded ? access.LibraryIds : [];
        return db.LibraryTracks.AsNoTracking().Where(track =>
            track.Protocol == protocol &&
            track.BackendInstanceId == context.BackendInstanceId && libraries.Contains(track.BackendLibraryId));
    }

    public static bool Allows(LibraryTrackRecord track, ProtocolExecutionContext context, BackendLibraryAccess access) =>
        context.Principal != null &&
        track.Protocol == context.Protocol.ToString().ToLowerInvariant() &&
        track.BackendInstanceId == context.BackendInstanceId && access.Allows(track.BackendLibraryId);
}
