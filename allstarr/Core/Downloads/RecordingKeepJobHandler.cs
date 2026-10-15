using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;

namespace allstarr.Core.Downloads;

public sealed class RecordingKeepJobHandler(ManagedTrackDownloadService downloads, RecordingKeptFiles kept) : IDurableJobHandler
{
    public string JobType => RecordingKeptFiles.ExplicitKeepJobType;

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var claim = context.Claim;
        var request = RecordingKeptFiles.ReadKeepPayload(claim.Payload.GetRawText());
        if (request == null || claim.OwnerUserId is not { } owner)
            return DurableJobCompletion.Failure("keep_request_invalid", "The saved keep request is invalid. Refresh the track and try again.");
        try
        {
            if (await kept.RetainRequestedKeepAsync(claim, cancellationToken) != null)
                return DurableJobCompletion.Success();
            await context.ReportProgressAsync(new("recording.keep.download", "Downloading the track for Kept.",
                Provider: request.ProviderId, Track: request.Metadata.Title), cancellationToken);
            var result = await downloads.ExecuteAsync(new(owner, claim.JobId, request.ProviderId, request.ExternalId,
                claim.CorrelationId, $"keep:{claim.JobId:N}", claim.AttemptNumber, "recording-keep"), cancellationToken);
            if (!result.Succeeded)
                return result.Retryable
                    ? DurableJobCompletion.Retry(result.ErrorCode ?? "keep_download_temporary", result.SafeMessage ?? "The track download temporarily failed.")
                    : DurableJobCompletion.Failure(result.ErrorCode ?? "keep_download_failed", result.SafeMessage ?? "The track could not be downloaded.");
            if (result.Artifact is not { State: ProviderDownloadArtifactState.Verified } artifact ||
                artifact.OwnerUserId != owner || artifact.DurableJobId != claim.JobId)
                return DurableJobCompletion.Failure("keep_artifact_invalid", "The downloaded track could not be verified for this request.");
            await kept.PublishRequestedKeepAsync(claim, artifact, cancellationToken);
            await context.ReportProgressAsync(new("recording.keep.complete", "Track saved to Kept.", Completed: 1, Total: 1,
                Provider: artifact.ProviderId, Track: request.Metadata.Title), cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (UnauthorizedAccessException) { return DurableJobCompletion.Cancelled(); }
        catch (IOException)
        {
            return DurableJobCompletion.Retry("keep_storage_temporary", "The track could not be saved right now. It will retry.");
        }
        catch (ArgumentException)
        {
            return DurableJobCompletion.Failure("keep_metadata_invalid", "The song information could not be saved. Refresh the track and try again.");
        }
    }
}
