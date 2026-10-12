using allstarr.Core.Playback;

namespace allstarr.Core.Music;

/// <summary>
/// Records one playback observation in the playback signal pipeline for a linked user. Each protocol
/// decides which observations it reports; this owns how they are recorded. A recording failure is
/// logged and never changes the protocol response.
/// </summary>
public sealed class MusicPlaybackObservations(IPlaybackSignalPipeline? pipeline, ILogger logger)
{
    /// <summary>Returns false when the request was cancelled, so callers stop recording further observations.</summary>
    public async Task<bool> RecordAsync(PlaybackSignalRequest request, CancellationToken cancellationToken)
    {
        if (pipeline == null || string.IsNullOrWhiteSpace(request.ItemId) ||
            !request.ExecutionContext.CanRunUserScopedWork) return true;
        try
        {
            await pipeline.RecordAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Could not durably record playback signal {Transition} ({ExceptionType})",
                request.Transition,
                ex.GetType().Name);
        }
        return true;
    }
}
