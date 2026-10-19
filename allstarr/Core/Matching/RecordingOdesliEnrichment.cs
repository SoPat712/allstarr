using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record RecordingOdesliPayload(Guid SourceIdentityId);

public sealed class RecordingOdesliEnrichmentQueue(
    DurableJobQueue jobs, IPlatformClock clock,
    ILogger<RecordingOdesliEnrichmentQueue> logger)
{
    public const string JobType = "recording.odesli.enrich";

    // Queue optional evidence after the saved decision; the resolver applies the current source policy.
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
    IDbContextFactory<AllstarrDbContext> factory, ICrossProviderLinkResolver links,
    ITrackIdentityService identities, IPlatformClock clock,
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
            var recording = await db.CanonicalRecordings.AsNoTracking().SingleAsync(
                item => item.Id == source.CanonicalRecordingId, cancellationToken);
            var snapshotJson = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
                    item.ProviderTrackIdentityId == source.Id && item.OwnerUserId == execution.Claim.OwnerUserId)
                .OrderByDescending(item => item.RetrievedAt).Select(item => item.PayloadJson).FirstOrDefaultAsync(cancellationToken);
            using var metadata = JsonDocument.Parse(snapshotJson ?? "{}");
            long? Duration()
            {
                foreach (var name in new[] { "durationMilliseconds", "DurationMilliseconds", "durationMs", "DurationMs",
                             "durationSeconds", "DurationSeconds", "duration", "Duration" })
                    if (metadata.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                        value.TryGetDouble(out var number) && number > 0 && number < 86400000)
                        return (long)Math.Round(name.Contains("Milliseconds", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith("Ms", StringComparison.OrdinalIgnoreCase) ? number : number * 1000);
                return null;
            }
            string? Text(string lower, string upper) => metadata.RootElement.TryGetProperty(lower, out var value) ||
                metadata.RootElement.TryGetProperty(upper, out value)
                    ? value.ValueKind == JsonValueKind.String ? value.GetString() : null : null;
            var known = await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                    item.CanonicalRecordingId == source.CanonicalRecordingId &&
                    item.Verification == ProviderIdentityVerification.Verified && item.VerificationMethod != "automatic-suggestion" &&
                    item.VerificationMethod != ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod &&
                    item.VerificationMethod != ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod &&
                    (item.Scope == ProviderIdentityScope.Catalog || item.Id == source.Id))
                .Select(item => new ProviderExternalResourceId(item.ProviderId, ProviderResourceKind.Track, item.ExternalId,
                    item.CatalogNamespace == "default" ? null : item.CatalogNamespace)).ToArrayAsync(cancellationToken);
            var evidence = await links.ResolveAsync(new(source.Id.ToString("N"), source.ProviderId, source.ExternalId,
                    Text("title", "Title") ?? recording.Title, TrackSnapshotMetadata.Artist(metadata.RootElement) ?? "",
                    Text("album", "Album"), Text("albumArtist", "AlbumArtist"), Duration() ?? recording.DurationMilliseconds, isrc, null, recording.IsExplicit),
                known, actor, execution.Claim.CorrelationId, cancellationToken);
            var added = 0;
            foreach (var group in evidence.Where(item => item.Verified).GroupBy(item => item.Method))
                added += await identities.AddExactAliasesAsync(context, sourceTrack, group.Select(item => item.Id).Take(32).ToArray(),
                    group.Key, cancellationToken);
            if (added > 0 || await db.ProviderTrackIdentities.AnyAsync(item =>
                    item.CanonicalRecordingId == source.CanonicalRecordingId &&
                    (item.VerificationMethod == "odesli-link" || item.VerificationMethod == "isrc-lookup" ||
                     item.VerificationMethod == "cross-service-match"),
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

    private ProviderExecutionContext EvidenceContext(ProviderActorContext actor, string providerId,
        ProviderAccountContext? account, string correlationId, CancellationToken cancellationToken) =>
        new(actor, providerId, account,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, false, true, false, [providerId]),
            "cross-provider-evidence", correlationId, clock.UtcNow.AddSeconds(30), cancellationToken);
}
