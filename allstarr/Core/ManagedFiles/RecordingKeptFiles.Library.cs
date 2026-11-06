using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed partial class RecordingKeptFiles
{
    public async Task ReleasePromotedCopyAsync(Guid userId, Guid recordingId, string libraryId, Guid operationId,
        string correlationId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == userId && item.Enabled, token);
        if (!await db.LibraryTracks.AnyAsync(item => item.OwnerUserId == userId && item.BackendInstanceId == user.BackendInstanceId &&
                item.BackendLibraryId == libraryId && item.CanonicalRecordingId == recordingId, token))
            throw new InvalidOperationException("The library link must be confirmed before releasing a kept copy.");
        var route = await db.Set<RecordingKeptRouteEntity>().SingleOrDefaultAsync(item => item.CanonicalRecordingId == recordingId, token);
        if (route == null) return;
        var now = clock.UtcNow;
        var keepPrefix = $"keep:user:{userId:N}:";
        var hearts = await db.Set<allstarr.Core.Favorites.FavoriteStateRecord>().AsNoTracking()
            .Where(item => item.OwnerUserId == userId).Select(item => item.Id).ToArrayAsync(token);
        var playlistIds = await db.Set<PlaylistLinkRecord>().AsNoTracking().Where(item => item.OwnerUserId == userId).Select(item => item.Id).ToArrayAsync(token);
        var heartKeys = hearts.Select(id => $"heart:{id:N}").ToHashSet(StringComparer.Ordinal);
        var playlistPrefixes = playlistIds.Select(id => $"playlist:{id:N}:").ToArray();
        var references = await db.ManagedFileReferences.AsNoTracking().Where(item => item.ManagedFileId == route.ManagedFileId && item.ReleasedAt == null)
            .Select(item => item.ReferenceKey).ToArrayAsync(token);
        foreach (var key in references.Where(key => key.StartsWith(keepPrefix, StringComparison.Ordinal) || heartKeys.Contains(key) ||
                     playlistPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))))
            await StageReleaseInterestAsync(db, key, userId, operationId, now, correlationId, token, route.ManagedFileId);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
}
