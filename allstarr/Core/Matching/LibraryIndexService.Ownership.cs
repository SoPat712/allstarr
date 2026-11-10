using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class LibraryIndexService
{
    private readonly Dictionary<string, OwnershipEntry> _ownershipCandidates = new(StringComparer.Ordinal);
    private sealed record OwnershipCandidates(LibraryTrackRecord[] Tracks, TrackMatchCandidateSet Candidates);
    private sealed record OwnershipEntry(long Revision, Lazy<Task<OwnershipCandidates>> Work);

    public async Task WarmOwnershipAsync(ProtocolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Principal == null) return;
        EnsureStorageReady();
        var access = await _libraryAccess.ResolveAsync(context, cancellationToken);
        if (!access.Succeeded || access.LibraryIds.Length == 0) return;
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await RequireUserAsync(db, context.Principal, cancellationToken);
        await OwnershipCandidatesAsync(context, access, cancellationToken);
    }

    private async Task<OwnershipCandidates> OwnershipCandidatesAsync(ProtocolExecutionContext context,
        BackendLibraryAccess access, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var revision = db.LibraryReadRevision;
        var key = JsonSerializer.Serialize(new
        {
            context.Protocol,
            context.BackendInstanceId,
            Libraries = access.LibraryIds.Order(StringComparer.Ordinal)
        });
        OwnershipEntry entry;
        lock (_ownershipCandidates)
        {
            if (!_ownershipCandidates.TryGetValue(key, out entry!) || entry.Revision != revision ||
                entry.Work.IsValueCreated && (entry.Work.Value.IsFaulted || entry.Work.Value.IsCanceled))
            {
                if (!_ownershipCandidates.ContainsKey(key) && _ownershipCandidates.Count >= 32)
                    _ownershipCandidates.Remove(_ownershipCandidates.Keys.First());
                // Only the wait is cancellable: a foreground search can join a startup build even if its first waiter leaves.
                entry = new(revision, new(() => Task.Run(async () =>
                {
                    await using var read = await _contextFactory.CreateDbContextAsync();
                    var tracks = OrderOwnershipTracks(await LibraryTrackAccess.Query(read, context, access).ToArrayAsync());
                    return new OwnershipCandidates(tracks,
                        new TrackMatchDecisionEngine().PrepareCandidates(tracks.Select(LocalCopyPreference.ToCandidate)));
                })));
                _ownershipCandidates[key] = entry;
            }
        }
        return await entry.Work.Value.WaitAsync(cancellationToken).WaitAsync(context.CancellationToken);
    }

    private static LibraryTrackRecord[] OrderOwnershipTracks(IEnumerable<LibraryTrackRecord> tracks) => tracks
        .OrderByDescending(item => LocalCopyPreference.FormatRank(Path.GetExtension(item.FilePath)))
        .ThenByDescending(item => item.SourceModifiedAt)
        .ThenBy(item => item.BackendLibraryId).ThenBy(item => item.BackendItemId).ThenBy(item => item.Id).ToArray();
}
