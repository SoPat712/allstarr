using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Matching;

public sealed record RecordingOdesliPayload(Guid SourceIdentityId);

public sealed class RecordingOdesliEnrichmentQueue(
    DurableJobQueue jobs, IOptions<OdesliSettings> settings, IPlatformClock clock,
    ILogger<RecordingOdesliEnrichmentQueue> logger)
{
    public const string JobType = "recording.odesli.enrich";

    public async Task EnqueueAsync(Guid userId, Guid sourceIdentityId, string correlationId, CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return;
        try
        {
            var week = clock.UtcNow.ToUnixTimeSeconds() / (7 * 24 * 60 * 60);
            await jobs.EnqueueAsync(new DurableJobEnqueueRequest<RecordingOdesliPayload>(JobType,
                $"{sourceIdentityId:N}:{week}", new(sourceIdentityId), userId,
                Priority: -100, MaxAttempts: 3, CorrelationId: correlationId), cancellationToken);
        }
        catch (Exception error)
        {
            // Optional evidence cannot invalidate a decision that was already saved.
            logger.LogDebug("Could not queue cross-provider evidence ({ErrorType})", error.GetType().Name);
        }
    }
}

public sealed class RecordingOdesliEnrichmentJobHandler(
    IDbContextFactory<AllstarrDbContext> factory, OdesliService odesli, IOptions<OdesliSettings> settings,
    ITrackIdentityService identities, IProviderRegistry registry, IPlatformClock clock,
    DurableJobQueue jobs) : IDurableJobHandler
{
    public string JobType => RecordingOdesliEnrichmentQueue.JobType;

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return DurableJobCompletion.Success();
        RecordingOdesliPayload? payload;
        try { payload = execution.Claim.Payload.Deserialize<RecordingOdesliPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload?.SourceIdentityId is not { } sourceId || sourceId == Guid.Empty || execution.Claim.OwnerUserId == null)
            return DurableJobCompletion.Failure("cross_links_invalid", "The saved cross-provider request is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var source = await db.ProviderTrackIdentities.AsNoTracking().SingleOrDefaultAsync(item => item.Id == sourceId, cancellationToken);
        if (source == null || source.ResourceKind != ProviderResourceKind.Track ||
            source.Verification != ProviderIdentityVerification.Verified || source.VerificationMethod is "automatic-suggestion" or
                ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod or ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod)
            return DurableJobCompletion.Success();
        if (!await db.Users.AnyAsync(item => item.Id == execution.Claim.OwnerUserId && item.Enabled, cancellationToken))
            return DurableJobCompletion.Failure("cross_links_user_unavailable", "The initiating user is unavailable.");
        ProviderAccountContext? account = null;
        if (source.Scope == ProviderIdentityScope.Account)
        {
            var saved = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == source.ProviderAccountId && item.ProviderId == source.ProviderId && item.Enabled &&
                (item.OwnerUserId == null || item.OwnerUserId == execution.Claim.OwnerUserId), cancellationToken);
            if (saved == null) return DurableJobCompletion.Failure("cross_links_account_unavailable", "The source account is unavailable.");
            account = new(saved.Id, saved.ProviderId, saved.Scope, saved.Revision, saved.Enabled, saved.OwnerUserId);
        }
        var url = OdesliService.BuildTrackUrl(source.ProviderId, source.ExternalId);
        if (url == null) return DurableJobCompletion.Success();
        try
        {
            var links = await odesli.GetTrackLinksAsync(url, cancellationToken);
            var aliases = registry.Providers.Select(provider =>
                    links.TryGetValue(OdesliService.NormalizeProviderId(provider.Id), out var id)
                        ? new ProviderExternalResourceId(provider.Id, ProviderResourceKind.Track, id) : null)
                .OfType<ProviderExternalResourceId>().Take(32).ToArray();
            var actor = new ProviderActorContext(ProviderActorKind.SystemJob, null,
                durableJobId: execution.Claim.JobId, actingForUserId: execution.Claim.OwnerUserId);
            var context = new ProviderExecutionContext(actor, source.ProviderId, account,
                new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                    ProviderExplicitContentPolicy.Allow, false, true, false, [source.ProviderId]),
                "cross-provider-evidence", execution.Claim.CorrelationId, clock.UtcNow.AddSeconds(30), cancellationToken);
            var added = await identities.AddExactAliasesAsync(context,
                new(source.ProviderId, ProviderResourceKind.Track, source.ExternalId,
                    source.CatalogNamespace == "default" ? null : source.CatalogNamespace), aliases, cancellationToken);
            if (added > 0 || await db.ProviderTrackIdentities.AnyAsync(item =>
                    item.CanonicalRecordingId == source.CanonicalRecordingId && item.VerificationMethod == "odesli-link",
                    cancellationToken))
            {
                var snapshots = await db.ExternalMetadataSnapshots.Where(item =>
                        item.ProviderTrackIdentityId == source.Id && item.OwnerUserId == execution.Claim.OwnerUserId)
                    .OrderByDescending(item => item.RetrievedAt).Take(100).Select(item => item.Id).ToArrayAsync(cancellationToken);
                foreach (var snapshot in snapshots)
                    await jobs.EnqueueAsync(new DurableJobEnqueueRequest<TrackRematchSongPayload>(TrackRematchJobHandler.Type,
                        $"cross-links:{execution.Claim.JobId:N}:{snapshot:N}", new(snapshot), execution.Claim.OwnerUserId,
                        CorrelationId: execution.Claim.CorrelationId), cancellationToken);
            }
            return DurableJobCompletion.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return DurableJobCompletion.Cancelled(); }
        catch (OdesliLookupException error)
        {
            return error.Retryable
                ? DurableJobCompletion.Retry("cross_links_unavailable", error.Message, error.RetryAfter)
                : DurableJobCompletion.Failure("cross_links_unavailable", error.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return DurableJobCompletion.Failure("cross_links_scope_changed", "The source account is no longer available.");
        }
        catch (Exception)
        {
            return DurableJobCompletion.Retry("cross_links_unavailable", "Cross-provider evidence will retry.");
        }
    }
}
