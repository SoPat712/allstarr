using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public static class TrackMatchRevision
{
    public static async Task<string> ReadAsync(AllstarrDbContext db, ExternalMetadataSnapshotRecord snapshot,
        Guid userId, CancellationToken token)
    {
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
            item.OwnerUserId == userId && item.ProviderId == snapshot.ProviderId && item.ResourceKind == "track" &&
            item.ExternalIdHash == snapshot.ExternalIdHash && item.Protocol == snapshot.Protocol &&
            item.BackendInstanceId == snapshot.BackendInstanceId && item.BackendPrincipalId == snapshot.BackendPrincipalId)
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.SnapshotVersion, item.PayloadSha256 }).ToArrayAsync(token);
        var ids = snapshots.Select(item => item.Id).ToArray();
        var decisions = await db.TrackMatches.AsNoTracking().Where(item => item.OwnerUserId == userId && ids.Contains(item.ExternalSnapshotId))
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.Revision, item.DecisionVersion }).ToArrayAsync(token);
        var authorities = await ManualTrackOverrides.ForSource(db, snapshot).AsNoTracking()
            .Where(item => item.OwnerUserId == userId || item.OwnerUserId == null).OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Revision, item.RevokedAt }).ToArrayAsync(token);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { userId, snapshots, decisions, authorities })));
    }
}
