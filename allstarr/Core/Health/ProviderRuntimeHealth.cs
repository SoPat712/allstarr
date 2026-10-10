using allstarr.Core.Capabilities;
using allstarr.Services.AppleMusic;
using allstarr.Core.Operations;
using allstarr.Core.Routing;
using allstarr.Services.Common;

namespace allstarr.Core.Health;

public sealed class ProviderHealthOptions
{
    public const string SectionName = "ProviderHealth";
    public int FailureThreshold { get; set; } = 3;
    public int CircuitOpenSeconds { get; set; } = 60;
    public int SampleTtlSeconds { get; set; } = 300;

    public void Validate()
    {
        if (FailureThreshold is < 1 or > 100 || CircuitOpenSeconds is < 5 or > 86400 ||
            SampleTtlSeconds is < 5 or > 86400)
            throw new InvalidOperationException("Provider health policy is outside the supported bounds.");
    }
}

public interface IProviderOutcomeObserver
{
    void Observe(ProviderExecutionContext context, ProviderCapabilityKind capability,
        ProviderError? error, TimeSpan elapsed);
}

public sealed record ProviderHealthObservation(
    long? AccountRevision,
    ProviderHealthState Health,
    DateTimeOffset ObservedAt,
    long? LatencyMilliseconds,
    string? ReasonCode,
    int ConsecutiveFailures,
    DateTimeOffset? RetryAfter);

/// <summary>Volatile observations only. Reading status never contacts a provider or writes storage.</summary>
public sealed class ProviderRuntimeHealth : IProviderOutcomeObserver
{
    private readonly object _sync = new();
    private readonly Dictionary<ProviderRuntimeStatusKey, ProviderHealthObservation> _observations = [];
    private readonly Dictionary<string, SidecarStatus> _sidecars = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProviderHealthOptions _options;
    private readonly IPlatformClock _clock;

    private AppleDownloadEndpointSnapshot? _appleDownloadSnapshot;
    public AppleDownloadEndpointSnapshot? AppleDownloadSnapshot
    {
        get { lock (_sync) return _appleDownloadSnapshot; }
        set { lock (_sync) _appleDownloadSnapshot = value; }
    }

    public ProviderRuntimeHealth(ProviderHealthOptions? options = null, IPlatformClock? clock = null,
        SidecarHealthOptions? sidecars = null)
    {
        _options = options ?? new ProviderHealthOptions();
        _options.Validate();
        _clock = clock ?? new SystemPlatformClock();
        foreach (var target in sidecars?.Targets ?? [])
        {
            var absent = string.IsNullOrWhiteSpace(target.BaseUrl);
            Set(new SidecarStatus(target.Id, target.ProviderId,
                absent ? SidecarRuntimeState.NotInstalled : SidecarRuntimeState.Unknown,
                target.Required, absent ? "sidecar_not_installed" : null, null));
        }
    }

    public void Observe(ProviderExecutionContext context, ProviderCapabilityKind capability,
        ProviderError? error, TimeSpan elapsed)
    {
        if (error?.Kind is ProviderErrorKind.Canceled or ProviderErrorKind.NotFound or
            ProviderErrorKind.NotSupported or ProviderErrorKind.IncompatibleMedia or ProviderErrorKind.Forbidden)
            return;
        Record(Key(context.ProviderId, context.Account?.AccountId, capability.ToString()),
            context.Account?.Revision, error == null, (long)Math.Max(0, elapsed.TotalMilliseconds),
            error?.Code, error?.Kind is ProviderErrorKind.Unauthorized or
                ProviderErrorKind.AccountNeedsReauthentication or ProviderErrorKind.AccountNeedsConfiguration,
            error?.RetryAfter);
    }

    public void Record(ProviderRuntimeStatusKey key, long? revision, bool success, long? latency,
        string? reason, bool authenticationFailure = false, TimeSpan? retryAfter = null)
    {
        lock (_sync)
        {
            _observations.TryGetValue(key, out var previous);
            if (previous?.AccountRevision > revision) return;
            var now = _clock.UtcNow;
            var failures = success ? 0 : previous != null && previous.AccountRevision == revision && IsFresh(previous)
                ? Math.Min(previous.ConsecutiveFailures + 1, _options.FailureThreshold) : 1;
            var opens = !success && (authenticationFailure || retryAfter.HasValue || failures >= _options.FailureThreshold);
            var cooldown = TimeSpan.FromSeconds(_options.CircuitOpenSeconds);
            if (retryAfter > cooldown) cooldown = TimeSpan.FromSeconds(Math.Min(retryAfter.Value.TotalSeconds, 86400));
            _observations[key] = new(revision, success ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
                now, latency, SafeReason(reason), failures, opens ? now.Add(cooldown) : null);
        }
    }

    public ProviderHealthObservation? Get(ProviderRuntimeStatusKey key, long? revision = null)
    {
        lock (_sync)
            return _observations.TryGetValue(key, out var value) && IsFresh(value) &&
                (!revision.HasValue || value.AccountRevision == revision) ? value : null;
    }

    public ProviderRouteHealthSnapshot GetRouteStatus(string providerId, Guid? accountId, ProviderCapabilityKind capability)
    {
        var current = Get(Key(providerId, accountId, capability.ToString()));
        var open = current?.RetryAfter > _clock.UtcNow;
        return new(current?.Health == ProviderHealthState.Healthy ? ProviderRouteHealthState.Healthy :
            ProviderRouteHealthState.Unknown, open, current?.AccountRevision);
    }

    public bool IsCircuitOpen(ProviderHealthObservation observation) => observation.RetryAfter > _clock.UtcNow;

    public sealed record ProbeObservation(ProviderRuntimeStatusKey Key, ProviderHealthObservation Current,
        ProviderHealthObservation? Previous);

    public ProbeObservation? BeginProbe(ProviderRuntimeStatusKey key, long? revision)
    {
        lock (_sync)
        {
            _observations.TryGetValue(key, out var previous);
            if (previous?.AccountRevision > revision) return null;
            var current = new ProviderHealthObservation(revision, ProviderHealthState.Testing, _clock.UtcNow,
                null, null, previous != null && previous.AccountRevision == revision ? previous.ConsecutiveFailures : 0,
                previous != null && previous.AccountRevision == revision ? previous.RetryAfter : null);
            _observations[key] = current;
            return new(key, current, previous);
        }
    }

    public void CancelProbe(ProbeObservation? probe)
    {
        if (probe == null) return;
        lock (_sync)
        {
            if (!_observations.TryGetValue(probe.Key, out var current) || !ReferenceEquals(current, probe.Current)) return;
            if (probe.Previous == null) _observations.Remove(probe.Key);
            else _observations[probe.Key] = probe.Previous;
        }
    }

    public IReadOnlyList<SidecarStatus> GetAll()
    {
        lock (_sync) return _sidecars.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool TryGet(string id, out SidecarStatus status)
    {
        lock (_sync) return _sidecars.TryGetValue(id, out status!);
    }

    public void Set(SidecarStatus status)
    {
        lock (_sync) _sidecars[status.Id] = status;
    }

    private bool IsFresh(ProviderHealthObservation observation) =>
        observation.ObservedAt.AddSeconds(_options.SampleTtlSeconds) > _clock.UtcNow ||
        observation.RetryAfter > _clock.UtcNow;

    private static ProviderRuntimeStatusKey Key(string provider, Guid? account, string capability) =>
        account.HasValue ? ProviderRuntimeStatusKey.CreateManaged(provider, capability, account.Value) :
            ProviderRuntimeStatusKey.CreateAccountFree(provider, capability);

    private static string? SafeReason(string? reason) => reason switch
    {
        null => null,
        "apple-web-token-unavailable" or "provider-contract-changed" or "capability-unavailable" or
        "account-needs-configuration" or "account-needs-reauthentication" or "unauthorized" or
        "rate-limited" or "transient-failure" or "permanent-failure" or "probe_failed" or "timeout" or
        "unreachable" or "sidecar_not_installed" or "sidecar_unreachable" or "sidecar_timeout" or
        "sidecar_authentication_required" or "sidecar_version_mismatch" => reason,
        _ => "probe_failed"
    };
}
