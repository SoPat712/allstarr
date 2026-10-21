using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Models.Settings;
using allstarr.Services.MusicBrainz;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Matching;

public sealed record RecordingMusicBrainzPayload(Guid RecordingId);

public sealed class RecordingMusicBrainzEnrichmentQueue(
    IDbContextFactory<AllstarrDbContext> factory,
    DurableJobQueue jobs,
    IOptions<MusicBrainzSettings> settings,
    IMusicBrainzCatalogClient musicBrainz,
    IPlatformClock clock)
{
    public const string JobType = "recording.musicbrainz.enrich";

    public async Task<int> EnqueuePendingAsync(CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return 0;
        var queued = 0;
        // Unresolved or exhausted work may be attempted again the following week.
        var week = clock.UtcNow.ToUnixTimeSeconds() / (7 * 24 * 60 * 60);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        for (var offset = 0; ; offset += 100)
        {
            var pending = await db.CanonicalRecordings.AsNoTracking().Where(item =>
                item.MergedIntoId == null &&
                item.Identifiers.Any(identifier => identifier.Kind == RecordingIdentifierKinds.Isrc) &&
                !item.Identifiers.Any(identifier => identifier.Kind == RecordingIdentifierKinds.MusicBrainz) &&
                db.Users.Any(user => user.Id == item.CreatedByUserId && user.Enabled))
                .OrderBy(item => item.Id).Skip(offset).Take(100)
                .Select(item => new { item.Id, item.CreatedByUserId }).ToArrayAsync(cancellationToken);
            foreach (var item in pending)
            {
                var receipt = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<RecordingMusicBrainzPayload>(
                    JobType, $"{item.Id:N}:{musicBrainz.ConfiguredSourceRevision}:{week}", new(item.Id),
                    item.CreatedByUserId, Priority: -100, MaxAttempts: 3, CorrelationId: "recording-enrichment"), cancellationToken);
                if (receipt.Created) queued++;
            }
            if (pending.Length < 100) return queued;
        }
    }
}

public sealed class RecordingMusicBrainzEnrichmentService(
    RecordingMusicBrainzEnrichmentQueue queue,
    DurableStorageState storage,
    IOptions<MusicBrainzSettings> settings,
    ILogger<RecordingMusicBrainzEnrichmentService> logger, BackgroundTaskActivity? taskActivity = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.Enabled) return;
        taskActivity?.Register("recording-enrichment", "Recording identity backfill", TimeSpan.FromHours(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }
            taskActivity?.Started("recording-enrichment");
            try
            {
                await queue.EnqueuePendingAsync(stoppingToken);
                if (taskActivity != null) await taskActivity.CompletedAsync("recording-enrichment", true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("Recording enrichment discovery failed ({ErrorType})", error.GetType().Name);
                if (taskActivity != null) await taskActivity.CompletedAsync("recording-enrichment", false, stoppingToken);
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}

public sealed class RecordingMusicBrainzEnrichmentJobHandler(
    IDbContextFactory<AllstarrDbContext> factory,
    IMusicBrainzCatalogClient musicBrainz,
    IOptions<MusicBrainzSettings> settings,
    ITrackIdentityService identities,
    IEffectiveProviderPolicyResolver? providerPolicies = null) : IDurableJobHandler
{
    public string JobType => RecordingMusicBrainzEnrichmentQueue.JobType;

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return DurableJobCompletion.Success();
        RecordingMusicBrainzPayload? payload;
        try { payload = execution.Claim.Payload.Deserialize<RecordingMusicBrainzPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload?.RecordingId is not { } recordingId || recordingId == Guid.Empty || execution.Claim.OwnerUserId == null)
            return DurableJobCompletion.Failure("recording_evidence_invalid", "The saved recording request is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AnyAsync(item => item.Id == execution.Claim.OwnerUserId && item.Enabled, cancellationToken))
            return DurableJobCompletion.Failure("recording_evidence_user_unavailable", "The initiating user is unavailable.");
        var recording = await db.CanonicalRecordings.AsNoTracking().Include(item => item.Identifiers)
            .SingleOrDefaultAsync(item => item.Id == recordingId, cancellationToken);
        if (recording == null || recording.MergedIntoId != null ||
            recording.Identifiers.Any(item => item.Kind == RecordingIdentifierKinds.MusicBrainz))
            return DurableJobCompletion.Success();
        if (providerPolicies != null)
        {
            var policy = await providerPolicies.ResolveForUserAsync(
                execution.Claim.OwnerUserId.Value, cancellationToken);
            var providers = await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => item.CanonicalRecordingId == recording.Id &&
                               item.ResourceKind == ProviderResourceKind.Track)
                .Select(item => item.ProviderId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            if (providers.Length > 0 && !providers.Any(policy.IsAutomatic))
                return DurableJobCompletion.Success();
        }
        var isrc = recording.Identifiers.Where(item => item.Kind == RecordingIdentifierKinds.Isrc)
            .Select(item => item.Value).Order(StringComparer.Ordinal).FirstOrDefault();
        if (isrc == null) return DurableJobCompletion.Success();
        try
        {
            var evidence = await musicBrainz.LookupByIsrcAsync(isrc, cancellationToken);
            if (evidence == null) return DurableJobCompletion.Success();
            var actor = new ProviderActorContext(ProviderActorKind.SystemJob, null,
                durableJobId: execution.Claim.JobId, actingForUserId: execution.Claim.OwnerUserId);
            await identities.EnrichRecordingAsync(actor, recordingId, isrc, evidence.Id!, evidence.Isrcs ?? [],
                execution.Claim.CorrelationId, cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DurableJobCompletion.Cancelled();
        }
        catch (MusicBrainzLookupException error)
        {
            return error.Retryable
                ? DurableJobCompletion.Retry(error.Code, error.Message, error.RetryAfter)
                : DurableJobCompletion.Failure(error.Code, error.Message);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return DurableJobCompletion.Failure("recording_evidence_conflict", "The returned evidence could not safely enrich this recording.");
        }
        catch (Exception)
        {
            return DurableJobCompletion.Retry("recording_evidence_unavailable", "Recording enrichment will retry.");
        }
    }
}
