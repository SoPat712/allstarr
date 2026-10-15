using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Downloads;

public sealed partial class EfProviderDownloadArtifactStore
{
    public async Task<bool> SupersedeAsync(VerifiedProviderDownloadArtifact expected, long accountRevision,
        int attempt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (expected.OwnerUserId is not { } owner || expected.ProviderAccountId is not { } account ||
            expected.SourceAccountRevision is not { } oldRevision || accountRevision <= oldRevision ||
            string.IsNullOrWhiteSpace(expected.SourceExternalTrackId)) return false;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.ProviderAccounts.AnyAsync(item => item.Id == account && item.ProviderId == expected.ProviderId &&
                item.Enabled && item.Revision == accountRevision && item.OwnerUserId == owner, cancellationToken) ||
            !await db.Jobs.AnyAsync(item => item.Id == expected.DurableJobId && item.OwnerUserId == owner &&
                item.State == DurableJobState.Running && item.AttemptCount == attempt &&
                item.CancellationRequestedAt == null, cancellationToken)) return false;
        var row = await db.ProviderDownloadArtifacts.SingleOrDefaultAsync(item => item.Id == expected.Id &&
            item.OwnerUserId == owner && item.DurableJobId == expected.DurableJobId &&
            item.ProviderId == expected.ProviderId && item.ProviderAccountId == account &&
            item.SourceExternalTrackId == expected.SourceExternalTrackId && item.SourceAccountRevision == oldRevision &&
            item.WorkspaceRecordId == expected.WorkspaceRecordId && item.ContentSha256 == expected.ContentSha256 &&
            item.Length == expected.Length, cancellationToken);
        if (row == null || row.State is not (ProviderDownloadArtifactState.Verified or ProviderDownloadArtifactState.Placed) ||
            !await db.ProviderDownloadWorkspaces.AnyAsync(item => item.Id == row.WorkspaceRecordId &&
                item.WorkspaceId == row.WorkspaceId && item.DurableJobId == row.DurableJobId &&
                item.OwnerUserId == owner && item.ProviderId == row.ProviderId && item.ProviderAccountId == account,
                cancellationToken)) return false;
        if (row.SupersededAt == null)
        {
            row.SupersededAt = now;
            row.Revision++;
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ProviderDownloadWorkspaceEntity>> FindDiscardableWorkspacesAsync(
        Guid ownerUserId, Guid durableJobId, string? providerId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var pendingPrefix = $"pending:kept:{durableJobId:N}:";
        // Released reservations retain their source until the grace-period cleanup removes the file.
        if (await (from reference in db.ManagedFileReferences
                   join file in db.ManagedFiles on reference.ManagedFileId equals file.Id
                   where reference.ReferenceKey.StartsWith(pendingPrefix) && file.RemovedAt == null
                   select reference.Id).AnyAsync(cancellationToken)) return [];
        return await db.ProviderDownloadWorkspaces.AsNoTracking().Where(workspace =>
            workspace.OwnerUserId == ownerUserId && workspace.DurableJobId == durableJobId &&
            (providerId == null || workspace.ProviderId == providerId) &&
            db.ProviderDownloadArtifacts.Any(artifact => artifact.WorkspaceRecordId == workspace.Id &&
                artifact.WorkspaceId == workspace.WorkspaceId && artifact.OwnerUserId == ownerUserId &&
                artifact.DurableJobId == durableJobId && artifact.ProviderId == workspace.ProviderId &&
                artifact.ProviderAccountId == workspace.ProviderAccountId && artifact.SupersededAt != null) &&
            !db.ProviderDownloadArtifacts.Any(artifact => artifact.WorkspaceRecordId == workspace.Id &&
                (artifact.SupersededAt == null ||
                 db.ManagedFiles.Any(file => file.Id == artifact.ManagedFileId && file.RemovedAt == null) ||
                 db.Set<RecordingKeptRouteEntity>().Any(route => route.SourceArtifactId == artifact.Id))))
            .ToArrayAsync(cancellationToken);
    }
}
