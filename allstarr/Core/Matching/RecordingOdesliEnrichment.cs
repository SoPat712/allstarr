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
    DurableJobQueue jobs, IPlatformClock clock,
    ILogger<RecordingOdesliEnrichmentQueue> logger)
{
    public const string JobType = "recording.odesli.enrich";

    // ISRC lookups use the providers already configured, so the job runs whether or not Odesli is enabled.
    public async Task EnqueueAsync(Guid userId, Guid sourceIdentityId, string correlationId, CancellationToken cancellationToken)
    {
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
        try
        {
            var actor = new ProviderActorContext(ProviderActorKind.SystemJob, null,
                durableJobId: execution.Claim.JobId, actingForUserId: execution.Claim.OwnerUserId);
            var context = EvidenceContext(actor, source.ProviderId, account, execution.Claim.CorrelationId, cancellationToken);
            var sourceTrack = new ProviderExternalResourceId(source.ProviderId, ProviderResourceKind.Track, source.ExternalId,
                source.CatalogNamespace == "default" ? null : source.CatalogNamespace);
            var isrc = await db.RecordingIdentifiers.AsNoTracking()
                .Where(item => item.RecordingId == source.CanonicalRecordingId && item.Kind == RecordingIdentifierKinds.Isrc)
                .Select(item => item.Value)
                .FirstOrDefaultAsync(cancellationToken);
            var added = isrc == null
                ? 0
                : await identities.AddExactAliasesAsync(context, sourceTrack,
                    await FindByIsrcAsync(actor, source.ProviderId, isrc, execution.Claim.CorrelationId, cancellationToken),
                    "isrc-lookup", cancellationToken);
            var url = settings.Value.Enabled ? OdesliService.BuildTrackUrl(source.ProviderId, source.ExternalId) : null;
            if (url != null)
            {
                var links = await odesli.GetTrackLinksAsync(url, cancellationToken);
                var aliases = registry.Providers.Select(provider =>
                        links.TryGetValue(OdesliService.NormalizeProviderId(provider.Id), out var id)
                            ? new ProviderExternalResourceId(provider.Id, ProviderResourceKind.Track, id) : null)
                    .OfType<ProviderExternalResourceId>().Take(32).ToArray();
                added += await identities.AddExactAliasesAsync(context, sourceTrack, aliases, "odesli-link", cancellationToken);
            }
            if (added > 0 || await db.ProviderTrackIdentities.AnyAsync(item =>
                    item.CanonicalRecordingId == source.CanonicalRecordingId &&
                    (item.VerificationMethod == "odesli-link" || item.VerificationMethod == "isrc-lookup"),
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

    /// <summary>
    /// The same recording on other account-free providers, found by an exact ISRC match.
    /// A provider that cannot look up ISRCs, or fails, simply adds nothing.
    /// </summary>
    private async Task<IReadOnlyList<ProviderExternalResourceId>> FindByIsrcAsync(
        ProviderActorContext actor, string sourceProviderId, string isrc, string correlationId,
        CancellationToken cancellationToken)
    {
        var lookups = registry.Providers
            .Where(provider => provider.Id != sourceProviderId && provider.Capabilities.Any(capability =>
                capability.Capability == ProviderCapabilityKind.Metadata &&
                capability.AccountRequirement != ProviderAccountRequirement.Required))
            .Select(async provider =>
            {
                if (!registry.TryGetCapability<IProviderMetadataCapability>(
                        provider.Id, ProviderCapabilityKind.Metadata, out var metadata) || metadata == null) return null;
                try
                {
                    var outcome = await metadata.LookupByIsrcAsync(
                        EvidenceContext(actor, provider.Id, null, correlationId, cancellationToken),
                        new ProviderIsrcLookupRequest(isrc));
                    return outcome.IsSuccess &&
                           string.Equals(outcome.RequireValue().Isrc, isrc, StringComparison.OrdinalIgnoreCase)
                        ? outcome.RequireValue().Id
                        : null;
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
            });
        return (await Task.WhenAll(lookups)).OfType<ProviderExternalResourceId>().Take(32).ToArray();
    }

    private ProviderExecutionContext EvidenceContext(ProviderActorContext actor, string providerId,
        ProviderAccountContext? account, string correlationId, CancellationToken cancellationToken) =>
        new(actor, providerId, account,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, false, true, false, [providerId]),
            "cross-provider-evidence", correlationId, clock.UtcNow.AddSeconds(30), cancellationToken);
}
