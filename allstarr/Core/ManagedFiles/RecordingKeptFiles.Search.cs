using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed partial class RecordingKeptFiles
{
    internal async Task<IReadOnlySet<Guid>> FindAvailableAsync(ProviderActorContext actor,
        IReadOnlyCollection<Guid> recordingIds, CancellationToken cancellationToken)
    {
        if (actor.EffectiveUserId == null || recordingIds.Count == 0) return new HashSet<Guid>();
        var requested = recordingIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (requested.Length == 0) return new HashSet<Guid>();

        try
        {
            var resolved = await identities.ResolveRecordingsAsync(actor,
                requested.Select(id => id.ToString("D")).ToArray(), cancellationToken);
            var targets = resolved.Values.Where(item => item != null).Select(item => item!.Id).Distinct().ToArray();
            if (targets.Length == 0) return new HashSet<Guid>();

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            var files = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
            var canonical = await db.CanonicalRecordings.AsNoTracking()
                .Where(item => targets.Contains(item.Id) || item.MergedIntoId.HasValue && targets.Contains(item.MergedIntoId.Value))
                .Select(item => new { item.Id, item.MergedIntoId }).ToArrayAsync(cancellationToken);
            var targetByCanonical = canonical.ToDictionary(item => item.Id,
                item => targets.Contains(item.Id) ? item.Id : item.MergedIntoId!.Value);
            var canonicalIds = targetByCanonical.Keys.ToArray();
            var routes = await db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                .Where(item => canonicalIds.Contains(item.CanonicalRecordingId))
                .Select(item => new { item.CanonicalRecordingId, item.ManagedFileId, item.CreatedAt }).ToArrayAsync(cancellationToken);
            var adoptions = await db.Set<LegacyMediaAdoptionRecord>().AsNoTracking()
                .Where(item => item.CanonicalRecordingId.HasValue && canonicalIds.Contains(item.CanonicalRecordingId.Value))
                .Select(item => new { CanonicalRecordingId = item.CanonicalRecordingId!.Value, item.ManagedFileId, item.Kept, item.CreatedAt }).ToArrayAsync(cancellationToken);
            var fileIds = routes.Select(item => item.ManagedFileId).Concat(adoptions.Select(item => item.ManagedFileId)).Distinct().ToArray();
            var managed = (await db.ManagedFiles.AsNoTracking().Where(item => fileIds.Contains(item.Id) && item.RemovedAt == null)
                .ToArrayAsync(cancellationToken)).Select(EfManagedFileOwnershipStore.Map).OfType<ManagedFileRecord>()
                .ToDictionary(item => item.Id);
            var availableTargets = new HashSet<Guid>();
            foreach (var target in targets)
            {
                var routeCandidates = routes.Where(item => targetByCanonical[item.CanonicalRecordingId] == target)
                    .OrderByDescending(item => item.CanonicalRecordingId == target).ThenBy(item => item.CreatedAt).ThenBy(item => item.ManagedFileId);
                var adoptionCandidates = adoptions.Where(item => targetByCanonical[item.CanonicalRecordingId] == target)
                    .OrderByDescending(item => item.Kept).ThenBy(item => item.CreatedAt).ThenBy(item => item.ManagedFileId);
                foreach (var fileId in routeCandidates.Select(item => item.ManagedFileId).Concat(adoptionCandidates.Select(item => item.ManagedFileId)))
                {
                    if (!managed.TryGetValue(fileId, out var file)) continue;
                    try
                    {
                        ValidateHousehold(file);
                        await files.VerifyOwnedFileAsync(file, cancellationToken);
                        availableTargets.Add(target);
                        break;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
            }

            return requested.Where(id => resolved.TryGetValue(id.ToString("D"), out var recording) && recording != null &&
                availableTargets.Contains(recording.Id)).ToHashSet();
        }
        catch (UnauthorizedAccessException) { return new HashSet<Guid>(); }
    }
}
