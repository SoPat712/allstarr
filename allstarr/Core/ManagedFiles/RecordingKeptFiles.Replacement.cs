using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed partial class RecordingKeptFiles
{
    private async Task<(bool CanonicalReplaced, IReadOnlyList<Guid> RetiredFiles)> ReplaceUnavailableKeptFilesAsync(AllstarrDbContext db,
        IManagedFileOwnershipStore ownership, IReadOnlyCollection<ManagedFileRecord> unavailable,
        ManagedFileRecord serving, Guid recordingId, RecordingKeptRouteEntity? winner,
        KeptRecordingPublication publication, string correlationId, CancellationToken cancellationToken)
    {
        var replacedCanonical = false;
        var retired = new List<Guid>();
        foreach (var previous in unavailable.DistinctBy(item => item.Id))
        {
            ValidateHousehold(previous);
            if (previous.Id == serving.Id || previous.RootId != serving.RootId ||
                previous.ScopeKey != serving.ScopeKey) throw Denied();
            var routes = await db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                .Where(item => item.ManagedFileId == previous.Id).ToArrayAsync(cancellationToken);
            if (routes.Length == 0) continue;
            var sameContent = previous.ContentSha256 == serving.ContentSha256 && previous.Length == serving.Length;
            if (!sameContent)
            {
                var ids = routes.Select(item => item.CanonicalRecordingId).ToArray();
                var aliases = await db.CanonicalRecordings.CountAsync(item => ids.Contains(item.Id) &&
                    (item.Id == recordingId || item.MergedIntoId == recordingId), cancellationToken);
                if (aliases != ids.Length)
                    throw new IOException("A shared kept file needs a replacement with the same verified content.");
            }
            var references = await db.ManagedFileReferences.AsNoTracking().Where(item =>
                item.ManagedFileId == previous.Id && item.ReleasedAt == null).ToArrayAsync(cancellationToken);
            foreach (var reference in references)
            {
                await ownership.AddReferenceAsync(serving.Id, new(Guid.CreateVersion7(), serving.Id,
                    reference.OwnerUserId, serving.ScopeKey, reference.ReferenceKey, reference.CreatedAt), cancellationToken);
                await ownership.ReleaseReferenceAsync(previous.Id, reference.ReferenceKey, cancellationToken);
            }
            var oldRoutes = db.Set<RecordingKeptRouteEntity>().Where(item => item.ManagedFileId == previous.Id);
            if (sameContent)
                await oldRoutes.ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.ManagedFileId, serving.Id)
                    .SetProperty(item => item.UpdatedAt, clock.UtcNow)
                    .SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
            else
                await oldRoutes.ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.ManagedFileId, serving.Id)
                    .SetProperty(item => item.PublishedByUserId, winner == null ? publication.InitiatingUserId : winner.PublishedByUserId)
                    .SetProperty(item => item.PublicationJobId, winner == null ? publication.DurableJobId : winner.PublicationJobId)
                    .SetProperty(item => item.SourceArtifactId, winner == null ? publication.SourceArtifactId : winner.SourceArtifactId)
                    .SetProperty(item => item.UpdatedAt, clock.UtcNow)
                    .SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
            if (!await new EfManagedFileOwnershipStore(db).StageKeptRemovalAsync(previous.Id, recordingId,
                    clock.UtcNow, cancellationToken, allowUnpublished: true))
                throw new DbUpdateConcurrencyException("Kept interests changed during replacement.");
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = publication.InitiatingUserId,
                Category = "kept-recording",
                Action = "replaced-unavailable",
                Outcome = "success",
                CorrelationId = correlationId,
                SubjectType = "managed-file",
                SubjectId = previous.Id.ToString("D"),
                CreatedAt = clock.UtcNow,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { managedFileId = serving.Id })
            });
            replacedCanonical |= routes.Any(item => item.CanonicalRecordingId == recordingId);
            retired.Add(previous.Id);
        }
        return (replacedCanonical, retired);
    }
}
