namespace allstarr.Core.Capabilities;

public enum ProviderDownloadAvailabilityState
{
    Available,
    Unavailable,
    RegionRestricted,
    AccountRequired,
    Incompatible
}

public enum ProviderDownloadProgressStage
{
    Queued,
    Resolving,
    Transferring,
    Verifying,
    Completed
}

public sealed record ProviderManagedWorkspaceReference
{
    public ProviderManagedWorkspaceReference(string workspaceId)
    {
        WorkspaceId = ProviderContractValidation.RequiredText(workspaceId, nameof(workspaceId), 200);
    }

    public string WorkspaceId { get; }
}

public sealed record ProviderDownloadAvailabilityRequest
{
    public ProviderDownloadAvailabilityRequest(
        ProviderExternalResourceId trackId,
        ProviderAudioQuality requestedQuality = ProviderAudioQuality.Any)
    {
        ArgumentNullException.ThrowIfNull(trackId);
        trackId.RequireOwner(trackId.ProviderId, ProviderResourceKind.Track);
        if (!Enum.IsDefined(requestedQuality))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedQuality));
        }

        TrackId = trackId;
        RequestedQuality = requestedQuality;
    }

    public ProviderExternalResourceId TrackId { get; }

    public ProviderAudioQuality RequestedQuality { get; }
}

public sealed record ProviderDownloadAvailability
{
    public ProviderDownloadAvailability(
        ProviderDownloadAvailabilityState state,
        IEnumerable<ProviderAudioQuality>? availableQualities = null,
        long? estimatedBytes = null)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (estimatedBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedBytes));
        }

        var qualities = (availableQualities ?? []).OrderBy(item => item).ToArray();
        if (qualities.Any(item => !Enum.IsDefined(item)) || qualities.Distinct().Count() != qualities.Length)
        {
            throw new ArgumentException("Available qualities must be valid and unique.", nameof(availableQualities));
        }

        State = state;
        AvailableQualities = Array.AsReadOnly(qualities);
        EstimatedBytes = estimatedBytes;
    }

    public ProviderDownloadAvailabilityState State { get; }

    public IReadOnlyList<ProviderAudioQuality> AvailableQualities { get; }

    public long? EstimatedBytes { get; }
}

public sealed record ProviderDownloadRequest
{
    public ProviderDownloadRequest(
        ProviderExternalResourceId trackId,
        Guid durableJobId,
        ProviderManagedWorkspaceReference workspace,
        ProviderAudioQuality requestedQuality)
    {
        ArgumentNullException.ThrowIfNull(trackId);
        ArgumentNullException.ThrowIfNull(workspace);
        trackId.RequireOwner(trackId.ProviderId, ProviderResourceKind.Track);
        if (durableJobId == Guid.Empty)
        {
            throw new ArgumentException("A durable job ID is required.", nameof(durableJobId));
        }

        if (!Enum.IsDefined(requestedQuality))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedQuality));
        }

        TrackId = trackId;
        DurableJobId = durableJobId;
        Workspace = workspace;
        RequestedQuality = requestedQuality;
    }

    public ProviderExternalResourceId TrackId { get; }

    public Guid DurableJobId { get; }

    public ProviderManagedWorkspaceReference Workspace { get; }

    public ProviderAudioQuality RequestedQuality { get; }
}

public sealed record ProviderDownloadProgress
{
    public ProviderDownloadProgress(
        ProviderDownloadProgressStage stage,
        long bytesCompleted,
        long? totalBytes = null)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        if (bytesCompleted < 0 || totalBytes < 0 || totalBytes.HasValue && bytesCompleted > totalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesCompleted));
        }

        Stage = stage;
        BytesCompleted = bytesCompleted;
        TotalBytes = totalBytes;
    }

    public ProviderDownloadProgressStage Stage { get; }

    public long BytesCompleted { get; }

    public long? TotalBytes { get; }
}

public sealed record ProviderDownloadedArtifact
{
    public ProviderDownloadedArtifact(
        string artifactId,
        string sha256,
        long sizeBytes,
        ProviderMediaFormat media,
        bool verified)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (sizeBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        if (!verified)
        {
            throw new ArgumentException(
                "A successful download result must reference a verified managed artifact.",
                nameof(verified));
        }

        if (sha256.Length != 64 || sha256.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Artifact SHA-256 must be 64 lowercase hexadecimal characters.", nameof(sha256));
        }

        ArtifactId = ProviderContractValidation.RequiredText(artifactId, nameof(artifactId), 200);
        Sha256 = sha256;
        SizeBytes = sizeBytes;
        Media = media;
        Verified = true;
    }

    public string ArtifactId { get; }

    public string Sha256 { get; }

    public long SizeBytes { get; }

    public ProviderMediaFormat Media { get; }

    public bool Verified { get; }
}

public interface IProviderDownloadCapability : IProviderCapability
{
    Task<ProviderOutcome<ProviderDownloadAvailability>> CheckAvailabilityAsync(
        ProviderExecutionContext context,
        ProviderDownloadAvailabilityRequest request);

    Task<ProviderOutcome<ProviderDownloadedArtifact>> DownloadAsync(
        ProviderExecutionContext context,
        ProviderDownloadRequest request,
        IProgress<ProviderDownloadProgress>? progress = null);
}
