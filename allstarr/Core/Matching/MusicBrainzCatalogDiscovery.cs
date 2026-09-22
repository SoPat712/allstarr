using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Services.MusicBrainz;

namespace allstarr.Core.Matching;

public interface IMusicBrainzCatalogRefreshQueue
{
    Task<DurableJobEnqueueResult> EnqueueRecordingAsync(
        ProviderActorContext actor,
        string recordingMbid,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<DurableJobEnqueueResult> EnqueueReleaseAsync(
        ProviderActorContext actor,
        string releaseMbid,
        string correlationId,
        CancellationToken cancellationToken = default);
}

public sealed class MusicBrainzCatalogRefreshQueue(
    DurableJobQueue jobs,
    IMusicBrainzCatalogClient client,
    IPlatformClock clock) : IMusicBrainzCatalogRefreshQueue
{
    public Task<DurableJobEnqueueResult> EnqueueRecordingAsync(
        ProviderActorContext actor,
        string recordingMbid,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            MusicBrainzCatalogDiscoveryJobHandler.Type,
            Mbid(recordingMbid),
            actor,
            correlationId,
            id => new MusicBrainzCatalogDiscoveryJobPayload(id),
            cancellationToken);

    public Task<DurableJobEnqueueResult> EnqueueReleaseAsync(
        ProviderActorContext actor,
        string releaseMbid,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            MusicBrainzCatalogRefreshJobHandler.Type,
            Mbid(releaseMbid),
            actor,
            correlationId,
            id => new MusicBrainzCatalogRefreshJobPayload(id),
            cancellationToken);

    private Task<DurableJobEnqueueResult> EnqueueAsync<TPayload>(
        string jobType,
        string mbid,
        ProviderActorContext actor,
        string correlationId,
        Func<string, TPayload> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var ownerUserId = actor.EffectiveUserId ??
            throw new UnauthorizedAccessException("Catalog refresh requires a scoped user.");
        var normalizedCorrelation = string.IsNullOrWhiteSpace(correlationId)
            ? throw new ArgumentException("A correlation ID is required.", nameof(correlationId))
            : correlationId.Trim();
        var generation = clock.UtcNow.UtcTicks / MusicBrainzCatalogRefreshService.RefreshInterval.Ticks;
        var identity = $"{jobType}|{actor.TenantId:N}|{ownerUserId:N}|{mbid}|{client.ConfiguredSourceRevision}|{generation}";
        var idempotencyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return jobs.EnqueueAsync(new DurableJobEnqueueRequest<TPayload>(
            jobType,
            idempotencyKey,
            payload(mbid),
            actor.TenantId,
            ownerUserId,
            MaxAttempts: 8,
            Capability: "metadata",
            CorrelationId: normalizedCorrelation), cancellationToken);
    }

    private static string Mbid(string? value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed.ToString("D")
            : throw new ArgumentException("A valid MusicBrainz ID is required.", nameof(value));
}

public sealed record MusicBrainzCatalogDiscoveryJobPayload(string RecordingMbid);

public sealed class MusicBrainzCatalogDiscoveryJobHandler(
    IMusicBrainzCatalogClient client,
    IMusicBrainzCatalogRefreshQueue queue) : IDurableJobHandler
{
    public const string Type = "catalog.musicbrainz.discover-recording";
    public const int MaximumReleasesPerRecording = 50;
    public string JobType => Type;

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        MusicBrainzCatalogDiscoveryJobPayload? payload;
        try { payload = context.Claim.Payload.Deserialize<MusicBrainzCatalogDiscoveryJobPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload == null ||
            !Guid.TryParse(payload.RecordingMbid, out var recordingId) ||
            recordingId == Guid.Empty ||
            !context.Claim.TenantId.HasValue ||
            !context.Claim.OwnerUserId.HasValue)
            return DurableJobCompletion.Failure(
                "catalog_discovery_payload_invalid",
                "The canonical catalog discovery payload is invalid.");

        try
        {
            var recordingMbid = recordingId.ToString("D");
            var recording = await client.LookupByMbidAsync(recordingMbid, cancellationToken);
            if (recording == null)
                return DurableJobCompletion.Failure(
                    "catalog_recording_missing",
                    "The canonical catalog recording no longer exists upstream.");
            var rawReleaseIds = (recording.Releases ?? []).Select(release => release.Id).ToArray();
            if (rawReleaseIds.Any(id => !Guid.TryParse(id, out var parsed) || parsed == Guid.Empty))
                return DurableJobCompletion.Failure(
                    "catalog_recording_hierarchy_invalid",
                    "The canonical recording contains an invalid release identity.");
            var releaseIds = rawReleaseIds
                .Select(id => Guid.Parse(id!).ToString("D"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (releaseIds.Length == 0 || releaseIds.Length > MaximumReleasesPerRecording)
                return DurableJobCompletion.Failure(
                    "catalog_recording_hierarchy_invalid",
                    "The canonical recording has an invalid release fan-out.");

            var actor = new ProviderActorContext(
                context.Claim.TenantId.Value,
                ProviderActorKind.SystemJob,
                null,
                durableJobId: context.Claim.JobId,
                actingForUserId: context.Claim.OwnerUserId.Value);
            var completed = 0;
            foreach (var releaseId in releaseIds)
            {
                await queue.EnqueueReleaseAsync(
                    actor, releaseId, context.Claim.CorrelationId, cancellationToken);
                completed++;
                await context.ReportProgressAsync(new(
                    "catalog.discovery",
                    $"Queued {completed} of {releaseIds.Length} canonical editions.",
                    completed,
                    releaseIds.Length), cancellationToken);
            }
            return DurableJobCompletion.Success();
        }
        catch (MusicBrainzLookupException error) when (error.Retryable)
        {
            return DurableJobCompletion.Retry(error.Code, error.Message, error.RetryAfter);
        }
        catch (MusicBrainzLookupException error)
        {
            return DurableJobCompletion.Failure(error.Code, error.Message);
        }
    }
}
