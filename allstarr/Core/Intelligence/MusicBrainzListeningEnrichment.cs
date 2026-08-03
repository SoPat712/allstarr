using System.Text.Json;
using System.Text;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.MusicBrainz;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Intelligence;

public sealed record MusicBrainzListeningEnrichmentPayload(
    IntelligenceScope Scope,
    string OccurrenceKey);

public sealed class MusicBrainzListeningEnrichmentQueue(
    DurableJobQueue jobs,
    IOptions<MusicBrainzSettings> settings)
{
    public const string JobType = "listening.musicbrainz.enrich";
    public bool Enabled => settings.Value.Enabled;

    public async Task<DurableJobEnqueueResult?> EnqueueAsync(
        IntelligenceScope scope,
        string occurrenceKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!Enabled) return null;
        return await jobs.EnqueueAsync(new DurableJobEnqueueRequest<MusicBrainzListeningEnrichmentPayload>(
            JobType,
            PlaybackSignalPipeline.Hash($"{occurrenceKey}|{MusicBrainzService.SourceRevision}"),
            new(scope, occurrenceKey),
            scope.TenantId,
            scope.OwnerUserId,
            LibraryScopeId: scope.LibraryScopeId,
            CorrelationId: correlationId), cancellationToken);
    }
}

public sealed class MusicBrainzListeningEnrichmentJobHandler(
    IDbContextFactory<AllstarrDbContext> factory,
    MusicBrainzService musicBrainz,
    IPlatformClock clock) : IDurableJobHandler
{
    public string JobType => MusicBrainzListeningEnrichmentQueue.JobType;

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext execution,
        CancellationToken cancellationToken)
    {
        var payload = execution.Claim.Payload.Deserialize<MusicBrainzListeningEnrichmentPayload>();
        if (payload == null || execution.Claim.TenantId != payload.Scope.TenantId ||
            execution.Claim.OwnerUserId != payload.Scope.OwnerUserId ||
            execution.Claim.LibraryScopeId != payload.Scope.LibraryScopeId ||
            payload.OccurrenceKey.Length != 64 || !payload.OccurrenceKey.All(Uri.IsHexDigit))
            return DurableJobCompletion.Failure(
                "musicbrainz_enrichment_scope_invalid",
                "The saved MusicBrainz enrichment scope is invalid.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var occurrence = await db.ListeningEvents.SingleOrDefaultAsync(item =>
            item.TenantId == payload.Scope.TenantId &&
            item.OwnerUserId == payload.Scope.OwnerUserId &&
            item.Protocol == payload.Scope.Protocol &&
            item.BackendInstanceId == payload.Scope.BackendInstanceId &&
            item.LibraryScopeId == payload.Scope.LibraryScopeId &&
            item.OccurrenceKey == payload.OccurrenceKey, cancellationToken);
        if (occurrence == null)
            return DurableJobCompletion.Failure(
                "musicbrainz_enrichment_occurrence_missing",
                "The accepted listening occurrence is unavailable.");
        if (occurrence.MusicBrainzSourceRevision == MusicBrainzService.SourceRevision &&
            occurrence.MusicBrainzEnrichmentState is
                MusicBrainzEnrichmentState.Resolved or MusicBrainzEnrichmentState.Unresolved)
            return DurableJobCompletion.Success();

        try
        {
            var match = await musicBrainz.ResolveRecordingAsync(
                occurrence.RecordingMusicBrainzId,
                occurrence.Isrc,
                occurrence.Title,
                occurrence.Artist,
                occurrence.DurationMilliseconds,
                cancellationToken);
            ApplyResult(occurrence, match, clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DurableJobCompletion.Cancelled();
        }
        catch (MusicBrainzLookupException exception) when (exception.Retryable)
        {
            return DurableJobCompletion.Retry(exception.Code, exception.Message, exception.RetryAfter);
        }
        catch (MusicBrainzLookupException exception)
        {
            occurrence.MusicBrainzEnrichmentState = MusicBrainzEnrichmentState.Failed;
            occurrence.MusicBrainzSourceRevision = MusicBrainzService.SourceRevision;
            occurrence.MusicBrainzEnrichmentConfidence = null;
            occurrence.MusicBrainzFactsJson = null;
            occurrence.MusicBrainzEnrichedAt = clock.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return DurableJobCompletion.Failure(exception.Code, exception.Message);
        }
        catch (ArgumentException)
        {
            occurrence.MusicBrainzEnrichmentState = MusicBrainzEnrichmentState.Failed;
            occurrence.MusicBrainzSourceRevision = MusicBrainzService.SourceRevision;
            occurrence.MusicBrainzEnrichmentConfidence = null;
            occurrence.MusicBrainzFactsJson = null;
            occurrence.MusicBrainzEnrichedAt = clock.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return DurableJobCompletion.Failure(
                "musicbrainz_enrichment_identity_invalid",
                "The accepted listening occurrence has invalid identity metadata.");
        }
        catch
        {
            return DurableJobCompletion.Retry(
                "musicbrainz_enrichment_temporary_failure",
                "MusicBrainz enrichment will retry.");
        }
    }

    internal static void ApplyResult(
        ListeningEventRecord occurrence,
        MusicBrainzRecordingMatch? match,
        DateTimeOffset enrichedAt)
    {
        string? facts = null;
        string? recordingId = null;
        if (match != null)
        {
            if (!double.IsFinite(match.Confidence) || match.Confidence is < 0 or > 1)
                throw new MusicBrainzLookupException(
                    "musicbrainz_confidence_invalid",
                    "MusicBrainz returned invalid match confidence metadata.",
                    false);
            recordingId = MusicBrainzService.NormalizeMbid(match.Recording.Id) ??
                throw new MusicBrainzLookupException(
                    "musicbrainz_identity_invalid",
                    "MusicBrainz returned invalid recording identity metadata.",
                    false);
            facts = JsonSerializer.Serialize(match.Recording);
            if (Encoding.UTF8.GetByteCount(facts) > MusicBrainzService.MaximumResponseBytes)
                throw new MusicBrainzLookupException(
                    "musicbrainz_response_too_large",
                    "MusicBrainz returned more metadata than Allstarr can safely process.",
                    false);
        }

        occurrence.MusicBrainzSourceRevision = MusicBrainzService.SourceRevision;
        occurrence.MusicBrainzEnrichedAt = enrichedAt;
        occurrence.MusicBrainzEnrichmentState = match == null
            ? MusicBrainzEnrichmentState.Unresolved
            : MusicBrainzEnrichmentState.Resolved;
        occurrence.MusicBrainzEnrichmentConfidence = match?.Confidence;
        occurrence.RecordingMusicBrainzId ??= recordingId;
        occurrence.MusicBrainzFactsJson = facts;
    }
}
