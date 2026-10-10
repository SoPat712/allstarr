using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackMatchCommandService
{
    public async Task<TrackMatchRecord> RecordDecisionAsync(
        ProtocolExecutionContext context,
        MatchDecisionInput input,
        CancellationToken cancellationToken = default) =>
        (await RecordDecisionsAsync(context, [input], cancellationToken)).Single();

    public async Task<IReadOnlyList<TrackMatchRecord>> RecordDecisionsAsync(
        ProtocolExecutionContext context,
        IReadOnlyCollection<MatchDecisionInput> inputs,
        CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        var requested = inputs.ToArray();
        if (requested.Length == 0)
            return [];
        foreach (var input in requested)
            ValidateDecisionInput(input);
        if (requested.Select(item => (item.ExternalSnapshotId, item.DecisionVersion)).Distinct().Count() !=
            requested.Length)
            throw new ArgumentException("A match decision version may appear only once.", nameof(inputs));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshotIds = requested.Select(item => item.ExternalSnapshotId).Distinct().ToArray();
        var snapshots = await db.ExternalMetadataSnapshots
            .Where(item => snapshotIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (snapshots.Count != snapshotIds.Length)
            throw new UnauthorizedAccessException("A source snapshot is outside the actor scope.");
        foreach (var snapshot in snapshots.Values)
        {
            PersistenceGuard.RequireOwner(actor, snapshot.OwnerUserId);

        }

        var libraryTrackIds = requested
            .Where(item => item.LibraryTrackId.HasValue)
            .Select(item => item.LibraryTrackId!.Value)
            .Distinct()
            .ToArray();
        var access = await libraryAccess.ResolveAsync(context, cancellationToken);
        var libraryTracks = await LibraryTrackAccess.Query(db, context, access)
            .Where(item => libraryTrackIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        foreach (var input in requested.Where(item => item.LibraryTrackId.HasValue))
        {
            var snapshot = snapshots[input.ExternalSnapshotId];
            if (!libraryTracks.TryGetValue(input.LibraryTrackId!.Value, out var libraryTrack) ||
                libraryTrack.BackendInstanceId != snapshot.BackendInstanceId)
                throw new UnauthorizedAccessException(
                    "The selected library track is unavailable to this viewer.");
        }

        var versions = requested.Select(item => item.DecisionVersion).Distinct().ToArray();
        var existing = await db.TrackMatches.AsNoTracking()
            .Where(item => snapshotIds.Contains(item.ExternalSnapshotId) &&
                           versions.Contains(item.DecisionVersion))
            .ToDictionaryAsync(
                item => (item.ExternalSnapshotId, item.DecisionVersion),
                cancellationToken);
        var now = clock.UtcNow;
        var records = new List<TrackMatchRecord>(requested.Length);
        foreach (var input in requested)
        {
            if (existing.TryGetValue((input.ExternalSnapshotId, input.DecisionVersion), out var stored))
            {
                if (!MatchesImmutableDecision(stored, input))
                    throw new InvalidOperationException(
                        "The match decision version already exists with different content.");
                records.Add(stored);
                continue;
            }

            var snapshot = snapshots[input.ExternalSnapshotId];
            var record = ToRecord(
                input, snapshot.OwnerUserId,
                context.CorrelationId, now);
            db.TrackMatches.Add(record);
            records.Add(record);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return records;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winners = await db.TrackMatches.AsNoTracking()
                .Where(item => snapshotIds.Contains(item.ExternalSnapshotId) &&
                               versions.Contains(item.DecisionVersion))
                .ToDictionaryAsync(
                    item => (item.ExternalSnapshotId, item.DecisionVersion),
                    cancellationToken);
            foreach (var input in requested)
            {
                if (!winners.TryGetValue(
                        (input.ExternalSnapshotId, input.DecisionVersion), out var winner))
                    throw;
                if (!MatchesImmutableDecision(winner, input))
                    throw new InvalidOperationException(
                        "A concurrent match decision used the same version with different content.");
            }
            return requested
                .Select(input => winners[(input.ExternalSnapshotId, input.DecisionVersion)])
                .ToArray();
        }
    }
}
