namespace allstarr.Core.Capabilities;

public enum ProviderStreamRetryBehavior
{
    DoNotRetry,
    RetrySameLeaseOnce,
    RefreshLease
}

public sealed record ProviderStreamLeaseRequest
{
    public ProviderStreamLeaseRequest(
        ProviderExternalResourceId trackId,
        ProviderAudioQuality requestedQuality = ProviderAudioQuality.Any,
        long? rangeStart = null)
    {
        ArgumentNullException.ThrowIfNull(trackId);
        trackId.RequireOwner(trackId.ProviderId, ProviderResourceKind.Track);
        if (!Enum.IsDefined(requestedQuality))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedQuality));
        }

        if (rangeStart is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeStart));
        }

        TrackId = trackId;
        RequestedQuality = requestedQuality;
        RangeStart = rangeStart;
    }

    public ProviderExternalResourceId TrackId { get; }

    public ProviderAudioQuality RequestedQuality { get; }

    public long? RangeStart { get; }
}

/// <summary>
/// A protected proxy-boundary lease. SourceUri may be signed and must never be logged or returned by a controller.
/// </summary>
public sealed class ProviderStreamLease
{
    public ProviderStreamLease(
        string leaseId,
        Uri sourceUri,
        DateTimeOffset expiresAt,
        bool supportsByteRanges,
        bool supportsSeeking,
        ProviderMediaFormat media,
        ProviderStreamRetryBehavior retryBehavior)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        ArgumentNullException.ThrowIfNull(media);
        if (!sourceUri.IsAbsoluteUri ||
            sourceUri.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(sourceUri.UserInfo))
        {
            throw new ArgumentException("Stream lease sources must use HTTP or HTTPS.", nameof(sourceUri));
        }

        if (expiresAt == default)
        {
            throw new ArgumentException("A stream lease expiry is required.", nameof(expiresAt));
        }

        if (!Enum.IsDefined(retryBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(retryBehavior));
        }

        LeaseId = ProviderContractValidation.RequiredText(leaseId, nameof(leaseId), 200);
        ProtectedSourceUri = sourceUri;
        ExpiresAt = expiresAt;
        SupportsByteRanges = supportsByteRanges;
        SupportsSeeking = supportsSeeking;
        Media = media;
        RetryBehavior = retryBehavior;
    }

    public string LeaseId { get; }

    internal Uri ProtectedSourceUri { get; }

    public DateTimeOffset ExpiresAt { get; }

    public bool SupportsByteRanges { get; }

    public bool SupportsSeeking { get; }

    public ProviderMediaFormat Media { get; }

    public ProviderStreamRetryBehavior RetryBehavior { get; }

    public override string ToString() =>
        $"ProviderStreamLease {{ LeaseId = {LeaseId}, ExpiresAt = {ExpiresAt:O}, SourceUri = \u003Credacted\u003E }}";
}

public sealed record ProviderStreamProbeResult(
    bool Available,
    DateTimeOffset ObservedAt,
    ProviderMediaFormat? Media = null);

public interface IProviderStreamingCapability : IProviderCapability
{
    Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request);

    Task<ProviderOutcome<ProviderStreamProbeResult>> ProbeStreamAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request);
}
