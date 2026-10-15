using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed partial class PlaylistTrackRetentionQueue
{
    private static async Task<IReadOnlyList<DurableJobEnqueueRequest<PlaylistTrackRetentionJobPayload>>> ResolveCancelledLifecyclesAsync(
        AllstarrDbContext db, IReadOnlyList<DurableJobEnqueueRequest<PlaylistTrackRetentionJobPayload>> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0) return requests;
        var owner = requests[0].OwnerUserId ?? throw new InvalidOperationException("Playlist retention requires an owner.");
        var scope = DurableJobQueue.CreateUserScopeKey(owner);
        async Task<Dictionary<string, DurableJobRecord>> ReadAsync(string[] keys) =>
            await db.Jobs.AsTracking().Where(item => item.ScopeKey == scope &&
                    item.Type == PlaylistTrackRetentionJobHandler.JobTypeName && EF.Parameter(keys).Contains(item.IdempotencyKey))
                .ToDictionaryAsync(item => item.IdempotencyKey, cancellationToken);
        static bool Retired(DurableJobRecord job) =>
            job.State == DurableJobState.Cancelled || job.CancellationRequestedAt != null;
        static string Following(string original, Guid predecessor) => $"{original}:after:{predecessor:N}";

        var keys = requests.Select(item => item.IdempotencyKey).Distinct(StringComparer.Ordinal).ToArray();
        var existing = await ReadAsync(keys);
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = existing.Values.Where(Retired)
            .Select(item => (Original: item.IdempotencyKey, Next: Following(item.IdempotencyKey, item.Id))).ToArray();
        // Follow immutable predecessors in batches. Repeated restoration has bounded
        // key length and never revives a cancelled or still-cancelling execution.
        while (pending.Length > 0)
        {
            var following = await ReadAsync(pending.Select(item => item.Next).Distinct(StringComparer.Ordinal).ToArray());
            var next = new List<(string Original, string Next)>();
            foreach (var item in pending)
            {
                if (following.TryGetValue(item.Next, out var job) && Retired(job))
                    next.Add((item.Original, Following(item.Original, job.Id)));
                else resolved[item.Original] = item.Next;
            }
            pending = next.ToArray();
        }
        return requests.Select(item => item with
        {
            IdempotencyKey = resolved.GetValueOrDefault(item.IdempotencyKey, item.IdempotencyKey)
        }).ToArray();
    }
}
