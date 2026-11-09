using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record PromotionAcquisitionResult(VerifiedProviderDownloadArtifact? Artifact, bool Pending, string Reason);
public interface IPromotionAcquisition
{
    Task<PromotionAcquisitionResult> AcquireAsync(Guid owner, Guid runId, PromotionTrack track, PromotionProfile profile, CancellationToken token);
}
internal sealed record PromotionDownloadSource(string ProviderId, string ExternalId, string? Codec, int? BitDepth, int? SampleRate, int? Bitrate)
{
    public Song ToSong() => new() { Codec = Codec, BitDepth = BitDepth, SampleRate = SampleRate, Bitrate = Bitrate };
}
internal sealed record PromotionDownloadPayload(Guid RunId, Guid RecordingId, PromotionDownloadSource[] Sources, PromotionProfile Profile);

public sealed class PromotionAcquisition(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs,
    ITrackIdentityService identities, ProviderDownloadArtifactResolver artifacts, RecordingKeptFiles? kept = null) : IPromotionAcquisition
{
    public const string JobType = "library.promote.acquire";
    public async Task<PromotionAcquisitionResult> AcquireAsync(Guid owner, Guid runId, PromotionTrack track, PromotionProfile profile, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (track.AcquisitionJobId == null)
        {
            var actor = new ProviderActorContext(ProviderActorKind.SystemJob, null, durableJobId: (await db.Set<PromotionRunRecord>()
                .SingleAsync(item => item.Id == runId, token)).JobId, actingForUserId: owner);
            if (track.SourceAttempt == 0 && kept != null &&
                await kept.FindPromotionSourceAsync(actor, track.RecordingId, profile, token) is { } local)
                return new(local, false, "Reused the verified kept copy meeting the saved quality floor.");
            var songs = await identities.GetStoredSongsAsync(actor, track.RecordingId.ToString("D"), token);
            var sources = PromotionQuality.Order(profile, songs.Append(track.Song)
                .Where(song => !song.IsLocal && !string.IsNullOrWhiteSpace(song.ExternalProvider) && !string.IsNullOrWhiteSpace(song.ExternalId) &&
                    !track.RejectedProviders.Contains(song.ExternalProvider)).DistinctBy(song => (song.ExternalProvider, song.ExternalId))).ToArray();
            if (sources.Length == 0) return new(null, false, "No authorized download identity remains for this recording.");
            var queued = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<PromotionDownloadPayload>(JobType,
                $"promotion:{runId:N}:{track.RecordingId:N}:{track.SourceAttempt}", new(runId, track.RecordingId,
                    sources.Select(song => new PromotionDownloadSource(song.ExternalProvider!, song.ExternalId!, song.Codec, song.BitDepth, song.SampleRate, song.Bitrate)).ToArray(), profile), owner,
                MaxAttempts: 3), token);
            track.AcquisitionJobId = queued.JobId;
            return new(null, true, "Waiting for the managed download.");
        }
        var job = await db.Jobs.AsNoTracking().SingleAsync(item => item.Id == track.AcquisitionJobId, token);
        if (job.State is DurableJobState.Failed or DurableJobState.Cancelled) return new(null, false, "Download sources were exhausted or cancelled.");
        if (job.State != DurableJobState.Succeeded) return new(null, true, "The managed download is running.");
        var candidates = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().Where(item => item.DurableJobId == job.Id &&
            item.OwnerUserId == owner && item.State == ProviderDownloadArtifactState.Verified && item.SupersededAt == null).ToArrayAsync(token);
        foreach (var candidate in candidates.OrderByDescending(item => PromotionQuality.IsLossless(item.Codec)).ThenByDescending(item => Math.Min(item.BitDepth ?? 0, profile.HiResCeiling.MaxBitDepth))
                     .ThenByDescending(item => Math.Min(item.SampleRate ?? 0, profile.HiResCeiling.MaxSampleRate)).ThenByDescending(item => item.Bitrate))
        {
            if (!PromotionQuality.Accepts(profile, candidate.Codec, candidate.BitDepth, candidate.SampleRate)) continue;
            var resolved = await artifacts.FindByJobAsync(job.Id, candidate.ProviderId, token);
            if (resolved?.Id == candidate.Id) return new(resolved, false, "A complete source meeting the quality floor was acquired.");
        }
        return new(null, false, "No measured download meets the quality floor.");
    }
}

public sealed class PromotionDownloadJobHandler(IDbContextFactory<AllstarrDbContext> factory,
    ManagedTrackDownloadService downloads) : IDurableJobHandler
{
    public string JobType => PromotionAcquisition.JobType;
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        var payload = execution.Claim.Payload.Deserialize<PromotionDownloadPayload>();
        if (payload == null || execution.Claim.OwnerUserId is not { } owner || payload.Sources.Length == 0)
            return DurableJobCompletion.Failure("promotion_download_invalid", "The saved download request is invalid.");
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Set<PromotionRunRecord>().AnyAsync(item => item.Id == payload.RunId && item.OwnerUserId == owner && item.Outcome != "Cancelled", token))
            return DurableJobCompletion.Cancelled();
        ManagedTrackDownloadResult? temporaryFailure = null;
        VerifiedProviderDownloadArtifact? best = null;
        foreach (var source in payload.Sources)
        {
            if (best != null && !PromotionQuality.CanImprove(payload.Profile, source.ToSong(), best.Codec, best.BitDepth, best.SampleRate, best.Bitrate)) continue;
            var result = await downloads.ExecuteAsync(new(owner, execution.Claim.JobId, source.ProviderId, source.ExternalId,
                execution.Claim.CorrelationId, $"promotion:{execution.Claim.JobId:N}", execution.Claim.AttemptNumber, "library-promotion")
            {
                RequestedQuality = ProviderAudioQuality.HighResolution,
                ProviderPriority = [source.ProviderId],
                AcceptArtifact = artifact => PromotionQuality.Accepts(payload.Profile, artifact.Codec, artifact.BitDepth, artifact.SampleRate)
            }, token);
            if (result.Succeeded && result.Artifact is { } candidate && (best == null || PromotionQuality.CanImprove(payload.Profile,
                    new Song { Codec = candidate.Codec, BitDepth = candidate.BitDepth, SampleRate = candidate.SampleRate, Bitrate = candidate.Bitrate },
                    best.Codec, best.BitDepth, best.SampleRate, best.Bitrate))) best = candidate;
            if (result.Retryable) temporaryFailure = result;
        }
        if (best != null) return DurableJobCompletion.Success();
        if (temporaryFailure != null) return DurableJobCompletion.Retry("promotion_download_temporary", "Authorized download sources are temporarily unavailable.");
        return DurableJobCompletion.Failure("promotion_sources_exhausted", "No download source met the quality floor.");
    }
}
