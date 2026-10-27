using allstarr.Core.Providers.Lyrics;
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

    void ObservePlayback(ProviderExecutionContext context, ProviderExternalResourceId track,
        ProviderAudioQuality quality, ProviderError? error, TimeSpan elapsed, ProviderMediaFormat? media = null)
    { }

    PlaybackStartObservation? GetPlaybackStart(ProviderExecutionContext context, ProviderAudioQuality quality) => null;

    void ObservePlaybackStart(ProviderExecutionContext context, ProviderAudioQuality quality, TimeSpan elapsed,
        ProviderMediaFormat? media)
    { }

    bool IsPlaybackCoolingDown(ProviderExecutionContext context, ProviderExternalResourceId track,
        ProviderAudioQuality quality) => false;
}

public sealed record PlaybackStartObservation(TimeSpan MedianStart, ProviderMediaFormat? Media);

public sealed record ProviderHealthObservation(
    long? AccountRevision,
    ProviderHealthState Health,
    DateTimeOffset ObservedAt,
    long? LatencyMilliseconds,
    string? ReasonCode,
    int ConsecutiveFailures,
    DateTimeOffset? RetryAfter)
{
    public DateTimeOffset? PausedAt { get; init; }
    internal DateTimeOffset? FailureWindowStartedAt { get; init; }
    internal LyricsConfigurationId? LyricsConfiguration { get; init; }
    internal ProviderHealthState? HealthBeforeProbe { get; init; }
}

/// <summary>Volatile observations only. Reading status never contacts a provider or writes storage.</summary>
public sealed class ProviderRuntimeHealth : IProviderOutcomeObserver, ILyricsOutcomeObserver
{
    private const int MaximumObservations = 4096;
    private readonly object _sync = new();
    private readonly Dictionary<ProviderRuntimeStatusKey, ProviderHealthObservation> _observations = [];
    private readonly Dictionary<ProviderRuntimeStatusKey, List<ProviderCheckResult>> _recent = [];
    private readonly Dictionary<PlaybackItemKey, DateTimeOffset> _playbackFailures = [];
    private readonly Dictionary<PlaybackSourceKey, List<PlaybackStartSample>> _playbackStarts = [];
    private readonly Dictionary<string, SidecarStatus> _sidecars = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProviderHealthOptions _options;
    private readonly IPlatformClock _clock;

    private AppleDownloadEndpointSnapshot? _appleDownloadSnapshot;
    private DateTimeOffset? _appleDownloadObservedAt;
    public AppleDownloadEndpointSnapshot? AppleDownloadSnapshot
    {
        get { lock (_sync) return _appleDownloadSnapshot; }
        set
        {
            lock (_sync)
            {
                _appleDownloadSnapshot = value;
                _appleDownloadObservedAt = _clock.UtcNow;
            }
        }
    }

    public DateTimeOffset? AppleDownloadObservedAt { get { lock (_sync) return _appleDownloadObservedAt; } }

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
        if (error?.Code == LocalCapacityException.Code) return;
        if (error?.Kind is ProviderErrorKind.Canceled or ProviderErrorKind.NotFound or
            ProviderErrorKind.NotSupported or ProviderErrorKind.IncompatibleMedia or ProviderErrorKind.Forbidden)
            return;
        RecordCore(Key(context.ProviderId, context.Account?.AccountId, capability.ToString()),
            context.Account?.Revision, error == null, (long)Math.Max(0, elapsed.TotalMilliseconds),
            error?.Code, error?.Kind is ProviderErrorKind.Unauthorized or
                ProviderErrorKind.AccountNeedsReauthentication or ProviderErrorKind.AccountNeedsConfiguration,
            error?.RetryAfter, passive: true);
    }

    void ILyricsOutcomeObserver.ObserveLyrics(ProviderExecutionContext context, ProviderError? error,
        TimeSpan elapsed, LyricsConfigurationId configuration)
    {
        if (error?.Code == LocalCapacityException.Code || error?.Kind is ProviderErrorKind.Canceled or
            ProviderErrorKind.NotFound or ProviderErrorKind.NotSupported or ProviderErrorKind.IncompatibleMedia or ProviderErrorKind.Forbidden) return;
        RecordCore(Key(context.ProviderId, context.Account?.AccountId, ProviderCapabilityKind.Lyrics.ToString()),
            context.Account?.Revision, error == null, (long)Math.Max(0, elapsed.TotalMilliseconds), error?.Code,
            error?.Kind is ProviderErrorKind.Unauthorized or ProviderErrorKind.AccountNeedsReauthentication or ProviderErrorKind.AccountNeedsConfiguration,
            error?.RetryAfter, passive: true, configuration);
    }

    internal ProviderHealthObservation? GetLyrics(ProviderRuntimeStatusKey key, long? revision, LyricsConfigurationId configuration)
    {
        var current = Get(key, revision);
        return current?.LyricsConfiguration == configuration ? current : null;
    }

    public void ObservePlayback(ProviderExecutionContext context, ProviderExternalResourceId track,
        ProviderAudioQuality quality, ProviderError? error, TimeSpan elapsed, ProviderMediaFormat? media = null)
    {
        if (error?.Code == LocalCapacityException.Code) return;
        if (context.IsReadOnly || error?.Kind is ProviderErrorKind.Canceled or ProviderErrorKind.Forbidden or
            ProviderErrorKind.Unauthorized or ProviderErrorKind.AccountNeedsConfiguration or ProviderErrorKind.AccountNeedsReauthentication)
            return;
        lock (_sync)
        {
            var key = PlaybackKey(context, track, quality);
            if (error == null)
            {
                _playbackFailures.Remove(key);
                ObservePlaybackStart(context, quality, elapsed, media);
                return;
            }
            var cooldown = TimeSpan.FromSeconds(Math.Clamp(error.RetryAfter?.TotalSeconds ?? 30, 5, 300));
            if (_playbackFailures.Count >= MaximumObservations && !_playbackFailures.ContainsKey(key))
                _playbackFailures.Remove(_playbackFailures.MinBy(item => item.Value).Key);
            _playbackFailures[key] = _clock.UtcNow.Add(cooldown);
            if (error.Kind == ProviderErrorKind.IncompatibleMedia)
                RecordCore(Key(context.ProviderId, context.Account?.AccountId, ProviderCapabilityKind.Streaming.ToString()),
                    context.Account?.Revision, false, (long)elapsed.TotalMilliseconds, error.Code, false, null, passive: true);
        }
    }

    public void ObservePlaybackStart(ProviderExecutionContext context, ProviderAudioQuality quality, TimeSpan elapsed,
        ProviderMediaFormat? media)
    {
        lock (_sync)
        {
            var key = new PlaybackSourceKey(context.ProviderId, context.Account?.AccountId, context.Account?.Revision, quality);
            if (!_playbackStarts.TryGetValue(key, out var samples))
            {
                if (_playbackStarts.Count >= MaximumObservations)
                    _playbackStarts.Remove(_playbackStarts.MinBy(item => item.Value[^1].At).Key);
                _playbackStarts[key] = samples = [];
            }
            samples.RemoveAll(item => item.At.AddSeconds(_options.SampleTtlSeconds) <= _clock.UtcNow);
            if (samples.Count >= 15) samples.RemoveAt(0);
            samples.Add(new(_clock.UtcNow, elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed, media));
        }
    }

    public PlaybackStartObservation? GetPlaybackStart(ProviderExecutionContext context, ProviderAudioQuality quality)
    {
        lock (_sync)
        {
            var key = new PlaybackSourceKey(context.ProviderId, context.Account?.AccountId, context.Account?.Revision, quality);
            if (!_playbackStarts.TryGetValue(key, out var samples)) return null;
            samples.RemoveAll(item => item.At.AddSeconds(_options.SampleTtlSeconds) <= _clock.UtcNow);
            if (samples.Count == 0) { _playbackStarts.Remove(key); return null; }
            var ordered = samples.OrderBy(item => item.Elapsed).ToArray();
            var median = ordered.Length % 2 == 1 ? ordered[ordered.Length / 2].Elapsed :
                TimeSpan.FromTicks((ordered[ordered.Length / 2 - 1].Elapsed.Ticks + ordered[ordered.Length / 2].Elapsed.Ticks) / 2);
            return new(median, samples.LastOrDefault(item => item.Media != null)?.Media);
        }
    }

    private readonly record struct PlaybackSourceKey(string Provider, Guid? Account, long? Revision, ProviderAudioQuality Quality);
    private sealed record PlaybackStartSample(DateTimeOffset At, TimeSpan Elapsed, ProviderMediaFormat? Media);

    public bool IsPlaybackCoolingDown(ProviderExecutionContext context, ProviderExternalResourceId track,
        ProviderAudioQuality quality)
    {
        lock (_sync)
        {
            var key = PlaybackKey(context, track, quality);
            if (!_playbackFailures.TryGetValue(key, out var until)) return false;
            if (until > _clock.UtcNow) return true;
            _playbackFailures.Remove(key);
            return false;
        }
    }

    private readonly record struct PlaybackItemKey(string Provider, string? Catalog, string Track, Guid? Account,
        long? Revision, ProviderAudioQuality Quality);

    private static PlaybackItemKey PlaybackKey(ProviderExecutionContext context, ProviderExternalResourceId track,
        ProviderAudioQuality quality) => new(track.ProviderId, track.Catalog, track.Value, context.Account?.AccountId,
            context.Account?.Revision, quality);

    public void Record(ProviderRuntimeStatusKey key, long? revision, bool success, long? latency,
        string? reason, bool authenticationFailure = false, TimeSpan? retryAfter = null, bool recordAttempt = false) =>
        RecordCore(key, revision, success, latency, reason, authenticationFailure, retryAfter, passive: false, recordAttempt: recordAttempt);

    private void RecordCore(ProviderRuntimeStatusKey key, long? revision, bool success, long? latency,
        string? reason, bool authenticationFailure, TimeSpan? retryAfter, bool passive, LyricsConfigurationId? configuration = null, bool recordAttempt = false)
    {
        if (reason == LocalCapacityException.Code) return;
        lock (_sync)
        {
            _observations.TryGetValue(key, out var previous);
            if (previous?.AccountRevision > revision) return;
            if (configuration.HasValue && previous?.AccountRevision == revision &&
                previous?.LyricsConfiguration is { } previousConfiguration && previousConfiguration.Revision > configuration.Value.Revision) return;
            var now = _clock.UtcNow;
            var comparable = previous != null && previous.AccountRevision == revision &&
                (!configuration.HasValue || previous.LyricsConfiguration == configuration);
            var withinWindow = comparable && previous!.FailureWindowStartedAt?.AddSeconds(_options.SampleTtlSeconds) > now;
            var failures = success ? 0 : withinWindow
                ? Math.Min(previous!.ConsecutiveFailures + 1, _options.FailureThreshold) : 1;
            var opens = !success && (authenticationFailure || retryAfter.HasValue || failures >= _options.FailureThreshold);
            var cooldown = TimeSpan.FromSeconds(_options.CircuitOpenSeconds);
            if (retryAfter > cooldown) cooldown = TimeSpan.FromSeconds(Math.Min(retryAfter.Value.TotalSeconds, 86400));
            var health = success ? ProviderHealthState.Healthy : !passive || opens ? ProviderHealthState.Degraded :
                comparable ? previous!.HealthBeforeProbe ?? previous.Health : ProviderHealthState.Unknown;
            MakeRoom(key);
            _observations[key] = new(revision, health,
                now, latency, SafeReason(reason), failures, opens ? now.Add(cooldown) : null)
            {
                LyricsConfiguration = configuration,
                FailureWindowStartedAt = success ? null : withinWindow ? previous!.FailureWindowStartedAt : now,
                PausedAt = opens ? comparable && IsCircuitOpen(previous!) ? previous!.PausedAt ?? now : now : null
            };
            if (!passive && !recordAttempt) return;
            if (!_recent.TryGetValue(key, out var recent)) _recent[key] = recent = [];
            if (recent.Count >= 20) recent.RemoveAt(0);
            recent.Add(new(key, revision, new()
            {
                Provider = key.Provider,
                Capability = key.Capability,
                IsSupported = true,
                IsEnabled = true,
                Configuration = ProviderConfigurationState.Configured,
                Health = success ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
                TestedAt = now,
                LatencyMilliseconds = latency,
                ReasonCode = SafeReason(reason)
            }));
        }
    }

    public IReadOnlyList<ProviderCheckResult> Recent(ProviderRuntimeStatusKey key, long? revision)
    {
        lock (_sync)
            return _recent.TryGetValue(key, out var recent)
                ? recent.Where(item => item.Revision == revision && item.Status.TestedAt >= _clock.UtcNow.AddDays(-30))
                    .Reverse().ToArray() : [];
    }

    public bool Resume(ProviderRuntimeStatusKey key, long? revision)
    {
        lock (_sync)
        {
            if (!_observations.TryGetValue(key, out var current) || current.AccountRevision != revision) return false;
            _observations[key] = current with
            {
                ConsecutiveFailures = 0,
                RetryAfter = null,
                PausedAt = null,
                FailureWindowStartedAt = null
            };
            foreach (var item in _playbackFailures.Keys.Where(item => key.Capability == ProviderCapabilities.Streaming &&
                Key(item.Provider, item.Account, key.Capability) == key && item.Revision == revision).ToArray())
                _playbackFailures.Remove(item);
            return true;
        }
    }

    public ProviderHealthObservation? Get(ProviderRuntimeStatusKey key, long? revision = null)
    {
        lock (_sync)
            return _observations.TryGetValue(key, out var value) && IsFresh(value) &&
                (!revision.HasValue || value.AccountRevision == revision) ? value : null;
    }

    public ProviderHealthObservation? GetForDisplay(ProviderRuntimeStatusKey key, long? revision = null)
    {
        lock (_sync)
            return _observations.TryGetValue(key, out var value) &&
                (!revision.HasValue || value.AccountRevision == revision) ? value : null;
    }

    public ProviderRouteHealthSnapshot GetRouteStatus(string providerId, Guid? accountId, ProviderCapabilityKind capability)
    {
        if (ProviderRuntimeStatusKey.CreateAccountFree(providerId, capability.ToString()).Provider == "apple-musickit" &&
            capability is ProviderCapabilityKind.Streaming or ProviderCapabilityKind.Download or ProviderCapabilityKind.Lyrics &&
            AppleDownloadSnapshot is { } apple)
        {
            if (apple.State == AppleDownloadEndpointState.NeedsAuthentication)
                return new(ProviderRouteHealthState.Unavailable, false,
                    ReasonCode: ProviderError.AppleDownloadSignInRequired().Code);
            if (apple.ReasonCode == "unsupported_api_version")
                return new(ProviderRouteHealthState.Unavailable, false,
                    ReasonCode: ProviderError.AppleDownloadGatewayUpdateRequired().Code);
        }
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
                previous != null && previous.AccountRevision == revision ? previous.RetryAfter : null)
            {
                PausedAt = previous != null && previous.AccountRevision == revision ? previous.PausedAt : null,
                FailureWindowStartedAt = previous != null && previous.AccountRevision == revision ? previous.FailureWindowStartedAt : null,
                HealthBeforeProbe = previous != null && previous.AccountRevision == revision
                    ? previous.HealthBeforeProbe ?? previous.Health : ProviderHealthState.Unknown
            };
            MakeRoom(key);
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

    private void MakeRoom(ProviderRuntimeStatusKey key)
    {
        if (_observations.Count < MaximumObservations || _observations.ContainsKey(key)) return;
        var oldest = _observations.MinBy(item => item.Value.ObservedAt).Key;
        _observations.Remove(oldest);
        _recent.Remove(oldest);
    }

    private static ProviderRuntimeStatusKey Key(string provider, Guid? account, string capability) =>
        account.HasValue ? ProviderRuntimeStatusKey.CreateManaged(provider, capability, account.Value) :
            ProviderRuntimeStatusKey.CreateAccountFree(provider, capability);

    private static string? SafeReason(string? reason) => reason switch
    {
        null => null,
        "apple-web-token-unavailable" or "provider-contract-changed" or "capability-unavailable" or
        "account-needs-configuration" or "account-needs-reauthentication" or "unauthorized" or
        "rate-limited" or "transient-failure" or "permanent-failure" or "incompatible-media" or "probe_failed" or "timeout" or
        "sample-unavailable" or "service_unavailable" or "download-not-verified" or "probe_not_available" or
        "container-conversion-failed" or "media-tools-unavailable" or "verification-required" or
        "authentication-required" or "access-denied" or "not-found" or "metadata-not-found" or "metadata-invalid" or "playlist-not-found" or
        "playlist-empty" or "playlist-invalid" or "playlist-sample-unavailable" or "probe-limit-exceeded" or
        "blocked-by-challenge" or "lyrics-upstream-busy-or-timed-out" or "unreachable" or "sidecar_not_installed" or "sidecar_unreachable" or "sidecar_timeout" or
        "sidecar_authentication_required" or "sidecar_version_mismatch" => reason,
        _ => "probe_failed"
    };
}
