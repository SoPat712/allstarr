using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace allstarr.Core.Operations;

public static class PlatformDiagnostics
{
    public const string SourceName = "Allstarr.Platform";
    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName, AppVersion.Version);

    public static readonly Counter<long> JobsEnqueued = Meter.CreateCounter<long>("allstarr.jobs.enqueued");
    public static readonly Counter<long> JobsClaimed = Meter.CreateCounter<long>("allstarr.jobs.claimed");
    public static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>("allstarr.jobs.completed");
    public static readonly Counter<long> JobContextDenied = Meter.CreateCounter<long>("allstarr.jobs.context_denied");
    public static readonly Counter<long> OutboxDelivered = Meter.CreateCounter<long>("allstarr.outbox.delivered");
    public static readonly Counter<long> OutboxDeliveryFailed = Meter.CreateCounter<long>("allstarr.outbox.delivery_failed");
    public static readonly Counter<long> OutboxTerminalFailed = Meter.CreateCounter<long>("allstarr.outbox.terminal_failed");
    public static readonly Counter<long> ProviderHealthSamples = Meter.CreateCounter<long>("allstarr.provider.health.samples");
    public static readonly Counter<long> SidecarProbes = Meter.CreateCounter<long>("allstarr.sidecar.probes");
    public static readonly Histogram<double> ProviderProbeLatency = Meter.CreateHistogram<double>(
        "allstarr.provider.probe.duration",
        unit: "ms");
    public static readonly Histogram<double> SidecarProbeLatency = Meter.CreateHistogram<double>(
        "allstarr.sidecar.probe.duration",
        unit: "ms");
}
