using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record IsrcRecordingMergePayload(Guid UserId);

public sealed class IsrcRecordingMergeQueue(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs)
{
    public const string JobType = "recording.isrc-merge";
    internal const string Category = "isrc-recording-merge";
    internal const string CompletionAction = "isrc-recording-merge.complete";

    public async Task<int> EnqueuePendingAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.AuditEvents.AnyAsync(item => item.Category == Category && item.Action == CompletionAction, cancellationToken))
            return 0;
        if (!await db.ListeningEvents.AnyAsync(item => item.Isrc != null && item.CanonicalRecordingId != null, cancellationToken) &&
            !await db.LibraryTracks.AnyAsync(item => item.Isrc != null && item.CanonicalRecordingId != null, cancellationToken) &&
            !await db.RecordingIdentifiers.AnyAsync(item => item.Kind == RecordingIdentifierKinds.Isrc, cancellationToken))
            return 0;
        // Recordings are shared by the household, so the merge acts for its first administrator, or its first listener.
        var owner = await db.Users.AsNoTracking().Where(user => user.Enabled).OrderByDescending(user => user.IsAdmin)
            .ThenBy(user => user.CreatedAt).ThenBy(user => user.Id).Select(user => (Guid?)user.Id).FirstOrDefaultAsync(cancellationToken);
        if (owner is not { } user) return 0;
        var receipt = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<IsrcRecordingMergePayload>(
            JobType, "isrc-recording-merge", new(user), user, Priority: -100, CorrelationId: Category), cancellationToken);
        return receipt.Created ? 1 : 0;
    }
}

public sealed partial class IsrcRecordingMergeJobHandler(ITrackIdentityService identities,
    IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock, int batchSize = 100) : IDurableJobHandler
{
    internal static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);
    // The matcher's own song length tolerance. Copies further apart under one ISRC are an edit and the full version,
    // which some services hand out under a single code.
    internal static readonly long LengthToleranceMilliseconds = new TrackMatchPolicy().DurationToleranceSeconds * 1000L;
    private const string CheckpointKind = "isrc-recording-merge-checkpoint";
    private static readonly string CheckpointKey = CanonicalCatalogKeys.Hash(IsrcRecordingMergeQueue.Category);

    internal sealed record Counts(int GroupsFound = 0, int GroupsMerged = 0, int RecordingsMerged = 0, int SkippedLengths = 0,
        int SkippedConflicts = 0, int SkippedManualChoices = 0, int SkippedChanged = 0, int IsrcsRecorded = 0);
    private sealed record Checkpoint(string? LastIsrc, Counts Counts);
    private sealed record Member(Guid Id, DateTimeOffset CreatedAt, long? DurationMilliseconds);

    public string JobType => IsrcRecordingMergeQueue.JobType;

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken cancellationToken)
    {
        IsrcRecordingMergePayload? payload;
        try { payload = execution.Claim.Payload.Deserialize<IsrcRecordingMergePayload>(); }
        catch (JsonException) { payload = null; }
        if (payload?.UserId is not { } user || user == Guid.Empty || execution.Claim.OwnerUserId != user)
            return DurableJobCompletion.Failure("isrc_recording_merge_invalid", "The saved recording merge request is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.AuditEvents.AnyAsync(item => item.Category == IsrcRecordingMergeQueue.Category &&
                item.Action == IsrcRecordingMergeQueue.CompletionAction, cancellationToken))
            return DurableJobCompletion.Success();
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == user && item.Enabled, cancellationToken);
        if (owner == null)
            return DurableJobCompletion.Failure("isrc_recording_merge_owner", "The listener the recording merge acts for is unavailable.");
        var saved = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == user &&
                item.ProviderId == "allstarr" && item.ResourceKind == CheckpointKind && item.ExternalIdHash == CheckpointKey)
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(cancellationToken);
        Checkpoint checkpoint;
        try { checkpoint = saved == null ? new(null, new()) : JsonSerializer.Deserialize<Checkpoint>(saved.PayloadJson) ?? throw new JsonException(); }
        catch (JsonException)
        {
            return DurableJobCompletion.Failure("isrc_recording_merge_checkpoint_invalid", "The saved recording merge progress is invalid.");
        }

        var (recordings, byIsrc, isrcsByRecording, identified) = await ReadEvidenceAsync(db, cancellationToken);
        var codes = byIsrc.Keys.Order(StringComparer.Ordinal).ToArray();
        var pending = codes.Where(code => checkpoint.LastIsrc == null || string.CompareOrdinal(code, checkpoint.LastIsrc) > 0)
            .Take(batchSize).ToArray();
        var counts = checkpoint.Counts;
        if (pending.Length == 0)
        {
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = user,
                Category = IsrcRecordingMergeQueue.Category,
                Action = IsrcRecordingMergeQueue.CompletionAction,
                Outcome = "completed",
                CorrelationId = execution.Claim.CorrelationId,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    groupsFound = counts.GroupsFound,
                    groupsMerged = counts.GroupsMerged,
                    recordingsMerged = counts.RecordingsMerged,
                    skipped = new
                    {
                        lengths = counts.SkippedLengths,
                        conflicts = counts.SkippedConflicts,
                        manualChoices = counts.SkippedManualChoices,
                        changed = counts.SkippedChanged
                    },
                    isrcsRecorded = counts.IsrcsRecorded
                }),
                CreatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
            await execution.ReportProgressAsync(new("completed", $"{counts.GroupsFound} shared ISRCs, {counts.GroupsMerged} merged, " +
                $"{counts.GroupsFound - counts.GroupsMerged} skipped.", codes.Length, codes.Length), cancellationToken);
            return DurableJobCompletion.Success();
        }

        var actor = new ProviderActorContext(ProviderActorKind.SystemJob, null, durableJobId: execution.Claim.JobId, actingForUserId: user);
        foreach (var code in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = byIsrc[code].Select(id => recordings[id]).ToArray();
            var before = counts;
            if (members.Length > 1)
                counts = await MergeGroupAsync(db, actor, code, members, counts, execution.Claim.CorrelationId, cancellationToken);
            // A recording known by this one ISRC alone records it, so the next copy carrying it joins this recording.
            else if (!identified.Contains(members[0].Id) && isrcsByRecording[members[0].Id].Count == 1)
            {
                try
                {
                    if ((await identities.MergeRecordingsByIsrcAsync(actor, code, members[0].Id, [], execution.Claim.CorrelationId,
                            cancellationToken)).Isrc != null)
                        counts = counts with { IsrcsRecorded = counts.IsrcsRecorded + 1 };
                }
                catch (KeyNotFoundException) { }
            }
            checkpoint = new(code, counts);
            // Progress is saved after each shared ISRC; a batch rerun after a restart skips the ISRCs it already recorded.
            if (counts.GroupsFound != before.GroupsFound) await SaveAsync();
        }
        await SaveAsync();
        var done = codes.Count(code => string.CompareOrdinal(code, checkpoint.LastIsrc) <= 0);
        await execution.ReportProgressAsync(new("merging", $"Checked {done} of {codes.Length} ISRCs; {counts.GroupsMerged} shared ones merged.",
            done, codes.Length), cancellationToken);
        return DurableJobCompletion.Continue(Pause);

        async Task SaveAsync()
        {
            var json = JsonSerializer.Serialize(checkpoint);
            var snapshot = new ExternalMetadataSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = user,
                Protocol = owner.BackendType,
                BackendInstanceId = owner.BackendInstanceId,
                BackendPrincipalId = owner.BackendPrincipalId,
                ProviderId = "allstarr",
                ResourceKind = CheckpointKind,
                ExternalIdHash = CheckpointKey,
                SnapshotVersion = (saved?.SnapshotVersion ?? 0) + 1,
                ProviderRevision = "checkpoint",
                PayloadJson = json,
                PayloadSha256 = CanonicalCatalogKeys.Hash(json),
                CorrelationId = execution.Claim.CorrelationId,
                RetrievedAt = clock.UtcNow
            };
            db.ExternalMetadataSnapshots.Add(snapshot);
            saved = snapshot;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<Counts> MergeGroupAsync(AllstarrDbContext db, ProviderActorContext actor, string code, Member[] members,
        Counts counts, string correlationId, CancellationToken cancellationToken)
    {
        counts = counts with { GroupsFound = counts.GroupsFound + 1 };
        var ids = members.Select(item => item.Id).ToArray();
        // A listener's pinned or released service copy is a hand choice the merge would override for the other songs.
        if (await db.ProviderTrackIdentities.AsNoTracking().AnyAsync(item => ids.Contains(item.CanonicalRecordingId) &&
                (item.Verification == ProviderIdentityVerification.Pinned ||
                 item.VerificationMethod == ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod ||
                 item.VerificationMethod == ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod), cancellationToken))
            return counts with { SkippedManualChoices = counts.SkippedManualChoices + 1 };
        var library = await db.LibraryTracks.AsNoTracking().Where(item => item.CanonicalRecordingId != null &&
                ids.Contains(item.CanonicalRecordingId.Value))
            .Select(item => new { Recording = item.CanonicalRecordingId!.Value, item.DurationMilliseconds }).ToArrayAsync(cancellationToken);
        var plays = await db.ListeningEvents.AsNoTracking().Where(item => item.CanonicalRecordingId != null &&
                ids.Contains(item.CanonicalRecordingId.Value))
            .Select(item => new { Recording = item.CanonicalRecordingId!.Value, item.DurationMilliseconds }).ToArrayAsync(cancellationToken);
        long? Median(IEnumerable<long?> values)
        {
            var known = values.Where(value => value > 0).Select(value => value!.Value).Order().ToArray();
            return known.Length == 0 ? null : known[known.Length / 2];
        }
        var lengths = members.Select(member => member.DurationMilliseconds is > 0 ? member.DurationMilliseconds
                : Median(library.Where(item => item.Recording == member.Id).Select(item => item.DurationMilliseconds))
                  ?? Median(plays.Where(item => item.Recording == member.Id).Select(item => item.DurationMilliseconds)))
            .OfType<long>().ToArray();
        if (lengths.Length > 1 && lengths.Max() - lengths.Min() > LengthToleranceMilliseconds)
            return counts with { SkippedLengths = counts.SkippedLengths + 1 };
        var keep = members.OrderByDescending(member => library.Any(item => item.Recording == member.Id))
            .ThenByDescending(member => plays.Count(item => item.Recording == member.Id))
            .ThenBy(member => member.CreatedAt).ThenBy(member => member.Id).First();
        var merged = ids.Where(id => id != keep.Id).ToArray();
        try { await identities.MergeRecordingsByIsrcAsync(actor, code, keep.Id, merged, correlationId, cancellationToken); }
        catch (RecordingMergeConflictException) { return counts with { SkippedConflicts = counts.SkippedConflicts + 1 }; }
        catch (KeyNotFoundException) { return counts with { SkippedChanged = counts.SkippedChanged + 1 }; }
        return counts with { GroupsMerged = counts.GroupsMerged + 1, RecordingsMerged = counts.RecordingsMerged + merged.Length };
    }

    private static async Task<(Dictionary<Guid, Member> Recordings, Dictionary<string, HashSet<Guid>> ByIsrc,
        Dictionary<Guid, HashSet<string>> IsrcsByRecording, HashSet<Guid> Identified)> ReadEvidenceAsync(
        AllstarrDbContext db, CancellationToken cancellationToken)
    {
        var all = await db.CanonicalRecordings.AsNoTracking()
            .Select(item => new { item.Id, item.MergedIntoId, item.CreatedAt, item.DurationMilliseconds }).ToArrayAsync(cancellationToken);
        var live = all.Where(item => item.MergedIntoId == null)
            .ToDictionary(item => item.Id, item => new Member(item.Id, item.CreatedAt, item.DurationMilliseconds));
        // A merge points every earlier merged recording at its survivor, so one step reaches the live recording.
        var roots = all.ToDictionary(item => item.Id, item => item.MergedIntoId ?? item.Id);
        var plays = await db.ListeningEvents.AsNoTracking().Where(item => item.Isrc != null && item.CanonicalRecordingId != null)
            .Select(item => new { item.Isrc, Recording = item.CanonicalRecordingId!.Value }).Distinct().ToArrayAsync(cancellationToken);
        var library = await db.LibraryTracks.AsNoTracking().Where(item => item.Isrc != null && item.CanonicalRecordingId != null)
            .Select(item => new { item.Isrc, Recording = item.CanonicalRecordingId!.Value }).Distinct().ToArrayAsync(cancellationToken);
        var stored = await db.RecordingIdentifiers.AsNoTracking().Where(item => item.Kind == RecordingIdentifierKinds.Isrc)
            .Select(item => new { Isrc = (string?)item.Value, Recording = item.RecordingId }).ToArrayAsync(cancellationToken);
        var byIsrc = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        var byRecording = new Dictionary<Guid, HashSet<string>>();
        foreach (var pair in plays.Concat(library).Concat(stored))
        {
            if (Normalize(pair.Isrc) is not { } code || !roots.TryGetValue(pair.Recording, out var root) || !live.ContainsKey(root)) continue;
            (byIsrc.TryGetValue(code, out var recordings) ? recordings : byIsrc[code] = new()).Add(root);
            (byRecording.TryGetValue(root, out var codes) ? codes : byRecording[root] = new(StringComparer.Ordinal)).Add(code);
        }
        var identified = stored.Where(item => roots.TryGetValue(item.Recording, out var root) && root == item.Recording)
            .Select(item => item.Recording).ToHashSet();
        return (live, byIsrc, byRecording, identified);
    }

    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var code = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return IsrcPattern().IsMatch(code) ? code : null;
    }

    [GeneratedRegex("^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}$", RegexOptions.CultureInvariant)]
    private static partial Regex IsrcPattern();
}

public sealed class IsrcRecordingMergeDiscoveryService(IsrcRecordingMergeQueue queue, DurableStorageState storage,
    ILogger<IsrcRecordingMergeDiscoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }
            try
            {
                await queue.EnqueuePendingAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("Shared ISRC recording merge discovery will retry ({ErrorType})", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
