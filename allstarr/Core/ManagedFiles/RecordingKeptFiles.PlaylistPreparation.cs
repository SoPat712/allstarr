using System.Globalization;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace allstarr.Core.ManagedFiles;

internal sealed record PlaylistInterestPreparation(AllstarrDbContext Database, Guid LinkId, long LinkRevision,
    long DataVersion, DurablePlaylistProjection? Projection, IReadOnlyDictionary<string, Guid> Desired);

public sealed partial class RecordingKeptFiles
{
    internal async Task<PlaylistInterestPreparation?> PreparePlaylistInterestsAsync(AllstarrDbContext db,
        Guid linkId, long expectedRevision, Guid ownerId, BackendLibraryAccessContext access,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction != null)
            throw new InvalidOperationException("Playlist preparation must precede the writer transaction.");
        // Keep the same connection open: data_version is comparable only on that
        // connection, and changes when another connection commits database changes.
        await db.Database.OpenConnectionAsync(cancellationToken);
        var version = await ReadPlaylistDataVersionAsync(db, cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == linkId &&
            item.OwnerUserId == ownerId, cancellationToken);
        if (link == null || link.Revision != expectedRevision || link.TrackRetention == PlaylistTrackRetention.KeepAll)
            return null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == ownerId && item.Enabled,
            cancellationToken);
        if (user == null || user.BackendType != link.TargetProtocol || user.BackendInstanceId != link.TargetBackendInstanceId)
            throw Denied();
        var result = await ReadPlaylistInterestsAsync(db, link, user, access, cancellationToken);
        if (version != await ReadPlaylistDataVersionAsync(db, cancellationToken)) return null;
        return new(db, link.Id, link.Revision, version, result.Projection, result.Desired);
    }

    private static async Task<long> ReadPlaylistDataVersionAsync(AllstarrDbContext db, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA data_version";
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }
}
