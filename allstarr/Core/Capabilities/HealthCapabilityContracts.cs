namespace allstarr.Core.Capabilities;

public enum ProviderProbeStatus
{
    Healthy,
    Degraded,
    Unavailable,
    Unauthorized
}

public sealed record ProviderHealthProbeRequest
{
    public ProviderHealthProbeRequest(
        ProviderCapabilityKind targetCapability,
        bool nonDestructive = true)
    {
        if (targetCapability is not (
            ProviderCapabilityKind.Metadata or
            ProviderCapabilityKind.Playlist or
            ProviderCapabilityKind.Streaming or
            ProviderCapabilityKind.Download))
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetCapability),
                "SDK v1 health probes target metadata, playlist, streaming, or download.");
        }

        TargetCapability = targetCapability;
        NonDestructive = nonDestructive;
    }

    public ProviderCapabilityKind TargetCapability { get; }

    public bool NonDestructive { get; }
}

public sealed record ProviderHealthProbeResult
{
    public ProviderHealthProbeResult(
        ProviderProbeStatus status,
        DateTimeOffset observedAt,
        TimeSpan latency,
        string? safeCode = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (observedAt == default)
        {
            throw new ArgumentException("A health observation time is required.", nameof(observedAt));
        }

        if (latency < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(latency));
        }

        Status = status;
        ObservedAt = observedAt;
        Latency = latency;
        SafeCode = safeCode == null
            ? null
            : ProviderContractValidation.Catalog(safeCode, nameof(safeCode));
    }

    public ProviderProbeStatus Status { get; }

    public DateTimeOffset ObservedAt { get; }

    public TimeSpan Latency { get; }

    public string? SafeCode { get; }
}

public interface IProviderHealthProbeCapability : IProviderCapability
{
    Task<ProviderOutcome<ProviderHealthProbeResult>> ProbeAsync(
        ProviderExecutionContext context,
        ProviderHealthProbeRequest request);
}
