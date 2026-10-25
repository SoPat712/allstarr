using Microsoft.EntityFrameworkCore;
using allstarr.Core.Storage;
using allstarr.Core.Providers.AppleMusicKit;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Models.Settings;
using allstarr.Core.Capabilities;
using allstarr.Core.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using allstarr.Core.Health;
using allstarr.Core.Operations;
using allstarr.Services.AppleMusic;
using allstarr.Core.Providers.AudioMuse;

namespace allstarr.Services.Common;

/// <summary>
/// Current status for built-in providers. Reads are side-effect free. Explicit
/// probes and normal activity share account, revision and capability observations.
/// The latest evidence remains visible after volatile routing samples expire.
/// </summary>
public class ProviderStatusManager
{
    private readonly ProviderDiagnosticSamples _diagnosticSamples = new();

    private readonly ConcurrentDictionary<ProviderRuntimeStatusKey,
        (long? Revision, ProviderRuntimeStatus Status)> _recordedChecks = new();

    private readonly record struct ProbeOutcome(
        bool Success,
        string? ReasonCode = null,
        bool MeasuresLatency = true, bool IsNeutral = false);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly (string Provider, string Capability, ProviderAccountRequirement AccountRequirement)[] KnownCapabilities =
    [
        ("spotify", ProviderCapabilities.Playlist, ProviderAccountRequirement.Required),
        ("spotify", ProviderCapabilities.Lyrics, ProviderAccountRequirement.Required),
        ("apple-musickit", ProviderCapabilities.Metadata, ProviderAccountRequirement.Optional),
        ("apple-musickit", ProviderCapabilities.Playlist, ProviderAccountRequirement.Optional),
        ("apple-musickit", ProviderCapabilities.Streaming, ProviderAccountRequirement.Required),
        ("apple-musickit", ProviderCapabilities.Download, ProviderAccountRequirement.Required),
        ("apple-musickit", ProviderCapabilities.Lyrics, ProviderAccountRequirement.Required),
        ("deezer", ProviderCapabilities.Metadata, ProviderAccountRequirement.None),
        ("deezer", ProviderCapabilities.Streaming, ProviderAccountRequirement.Required),
        ("deezer", ProviderCapabilities.Download, ProviderAccountRequirement.Required),
        ("deezer", ProviderCapabilities.Playlist, ProviderAccountRequirement.Required),
        ("qobuz", ProviderCapabilities.Metadata, ProviderAccountRequirement.Required),
        ("qobuz", ProviderCapabilities.Streaming, ProviderAccountRequirement.Required),
        ("qobuz", ProviderCapabilities.Download, ProviderAccountRequirement.Required),
        ("qobuz", ProviderCapabilities.Playlist, ProviderAccountRequirement.Required),
        ("lrcred", ProviderCapabilities.Lyrics, ProviderAccountRequirement.None),
        ("unison", ProviderCapabilities.Lyrics, ProviderAccountRequirement.None),
        ("paxsenix", ProviderCapabilities.Lyrics, ProviderAccountRequirement.None),
        ("genius", ProviderCapabilities.Lyrics, ProviderAccountRequirement.None),
        ("lrclib", ProviderCapabilities.Lyrics, ProviderAccountRequirement.None),
        ("lyricsplus", ProviderCapabilities.Lyrics, ProviderAccountRequirement.None),
        ("musixmatch", ProviderCapabilities.Lyrics, ProviderAccountRequirement.Optional),
        ("spicylyrics", ProviderCapabilities.Lyrics, ProviderAccountRequirement.Required),
        ("lastfm", ProviderCapabilities.Scrobbling, ProviderAccountRequirement.Required),
        ("listenbrainz", ProviderCapabilities.Scrobbling, ProviderAccountRequirement.Required),
        ("multi-scrobbler", ProviderCapabilities.Scrobbling, ProviderAccountRequirement.Required),
        ("koito", ProviderCapabilities.Scrobbling, ProviderAccountRequirement.Required),
        ("audiomuse-ai", ProviderCapabilities.Intelligence, ProviderAccountRequirement.Required)
    ];

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProviderStatusManager> _logger;
    private readonly SpotifyApiSettings _spotifySettings;
    private readonly AppleDownloadSettings _appleMusicSettings;
    private readonly DeezerSettings _deezerSettings;
    private readonly QobuzSettings _qobuzSettings;
    private readonly ExtensionManager? _extensionManager;
    private readonly ProviderRuntimeHealth _health;
    private readonly IAppleDownloadEndpointDiscovery? _appleDownloadDiscovery;
    private readonly IServiceProvider? _services;
    private readonly IPlatformClock _clock;
    private readonly Lazy<allstarr.Core.Providers.Lyrics.MusixmatchTokenProvider> _musixmatchTokens;
    private AppleDownloadEndpointSnapshot? _appleDownloadSnapshot
    {
        get => _health.AppleDownloadSnapshot;
        set => _health.AppleDownloadSnapshot = value;
    }


    public ProviderStatusManager(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<ProviderStatusManager> logger,
        IOptions<SpotifyApiSettings> spotifySettings,
        IOptions<AppleDownloadSettings> appleMusicSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings,
        ExtensionManager? extensionManager = null,
        ProviderRuntimeHealth? health = null,
        IAppleDownloadEndpointDiscovery? appleDownloadDiscovery = null,
        IServiceProvider? services = null,
        IPlatformClock? clock = null)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _musixmatchTokens = new(() => new(_httpClientFactory.CreateClient(
            allstarr.Core.Providers.Lyrics.MusixmatchLyricsCapabilityAdapter.HttpClientName)));
        _logger = logger;
        _spotifySettings = spotifySettings.Value;
        _appleMusicSettings = appleMusicSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _extensionManager = extensionManager;
        _appleDownloadDiscovery = appleDownloadDiscovery;
        _services = services;
        _clock = clock ?? new SystemPlatformClock();
        _health = health ?? new ProviderRuntimeHealth(clock: _clock);
    }

    public IReadOnlyList<string> GetEnabledSearchProviders()
    {
        var order = GetMetadataOrder();
        var enabled = GetEnabledSearchRaw();

        return order
            .Where(provider =>
                enabled.Contains(provider) &&
                CanRunWithoutAccount(provider, ProviderCapabilities.Metadata) &&
                GetAccountFreeStatus(provider, ProviderCapabilities.Metadata).CanAttempt)
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledPlaylistProviders()
    {
        var order = GetPlaylistOrder();
        var enabled = GetEnabledPlaylistRaw();

        return order
            .Where(provider =>
                enabled.Contains(provider) &&
                CanRunWithoutAccount(provider, ProviderCapabilities.Playlist) &&
                GetAccountFreeStatus(provider, ProviderCapabilities.Playlist).CanAttempt)
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledDownloadProviders()
    {
        var configured = GetDownloadOrder();
        var extensionProviders = ActiveExtensionProviders(IsDownloadCapability);
        return configured
            .Where(provider => IsKnownBuiltInProvider(provider)
                ? CanRunWithoutAccount(provider, ProviderCapabilities.Download) &&
                  GetAccountFreeStatus(provider, ProviderCapabilities.Download).CanAttempt
                : extensionProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            .Concat(extensionProviders.Except(configured, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledStreamingProviders()
    {
        var configured = GetStreamingOrder();
        var extensionProviders = ActiveExtensionProviders(IsStreamingCapability);
        return configured
            .Where(provider => IsKnownBuiltInProvider(provider)
                ? CanRunWithoutAccount(provider, ProviderCapabilities.Streaming) &&
                  GetAccountFreeStatus(provider, ProviderCapabilities.Streaming).CanAttempt
                : extensionProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            .Concat(extensionProviders.Except(configured, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Returns providers in the exact order used when selecting a playable route.
    /// A provider that can both stream and download appears only once, at its
    /// first configured position.
    /// </summary>
    public IReadOnlyList<string> GetEnabledPlaybackProviders() =>
        GetEnabledStreamingProviders()
            .Concat(GetEnabledDownloadProviders())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private List<string> ActiveExtensionProviders(Func<string, bool> canPlay)
    {
        if (_extensionManager is null)
        {
            return [];
        }

        return _extensionManager.GetActiveExtensions()
            .Where(extension => extension.Types.Any(capability => canPlay(capability)))
            .Select(extension => extension.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsKnownBuiltInProvider(string providerId) =>
        KnownCapabilities.Any(capability => string.Equals(capability.Provider, providerId, StringComparison.OrdinalIgnoreCase));

    private static bool IsStreamingCapability(string capability) =>
        capability.Equals("stream", StringComparison.OrdinalIgnoreCase) ||
        capability.Equals("streaming", StringComparison.OrdinalIgnoreCase);

    private static bool IsDownloadCapability(string capability) =>
        capability.Equals("download", StringComparison.OrdinalIgnoreCase) ||
        capability.Equals("downloads", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetEnabledLyricsProviders()
    {
        return GetLyricsOrder()
            .Where(provider => CanRunWithoutAccount(provider, ProviderCapabilities.Lyrics) &&
                               GetAccountFreeStatus(provider, ProviderCapabilities.Lyrics).CanAttempt)
            .ToList();
    }

    /// <summary>
    /// Returns only capabilities whose typed contract permits execution without
    /// a provider account. Account-bound status is available through the managed APIs.
    /// </summary>
    public IReadOnlyList<ProviderRuntimeStatus> GetAllAccountFreeStatuses() =>
        RuntimeCapabilities()
            .Where(item => item.AccountRequirement != ProviderAccountRequirement.Required)
            .Select(item => GetAccountFreeStatus(item.Provider, item.Capability))
            .ToList();

    public IReadOnlyList<ProviderRuntimeStatus> GetAppleDownloadEndpointStatuses() =>
        new[] { ProviderCapabilities.Streaming, ProviderCapabilities.Download, ProviderCapabilities.Lyrics }
            .Select(capability => ProviderRuntimeStatusKey.CreateAccountFree("apple-musickit", capability))
            .Select(key => GetStatusCore(key, BuildBaselineStatus(key)))
            .ToList();

    /// <summary>
    /// Returns the capabilities for one managed provider account. Credential
    /// configuration is evaluated from that account's decrypted, in-memory view;
    /// it never falls back to another account's deployment-global credential.
    /// </summary>
    public IReadOnlyList<ProviderRuntimeStatus> GetAllManagedStatuses(
        string provider,
        Guid providerAccountId,
        IReadOnlyDictionary<string, string> accountSecrets,
        long? accountRevision = null)
    {
        var normalizedProvider = Normalize(provider);
        return RuntimeCapabilities()
            .Where(item => item.Provider == normalizedProvider)
            .Select(item => GetManagedStatus(
                item.Provider,
                item.Capability,
                providerAccountId,
                accountSecrets, accountRevision))
            .ToList();
    }

    public ProviderRuntimeStatus GetManagedStatus(
        string provider,
        string capability,
        Guid providerAccountId,
        IReadOnlyDictionary<string, string> accountSecrets,
        long? accountRevision = null)
    {
        var key = ProviderRuntimeStatusKey.CreateManaged(
            provider,
            capability,
            providerAccountId);
        var baseline = ApplyManagedAccountConfiguration(
            BuildBaselineStatus(key),
            accountSecrets);
        return GetStatusCore(key, baseline, accountRevision);
    }

    /// <summary>
    /// Returns a truthful snapshot without starting a probe or inventing a timestamp.
    /// </summary>
    public ProviderRuntimeStatus GetAccountFreeStatus(
        string provider,
        string capability)
    {
        RequireAccountFreeCapability(provider, capability);
        var key = ProviderRuntimeStatusKey.CreateAccountFree(provider, capability);
        return GetStatusCore(key, BuildBaselineStatus(key));
    }

    public void RestoreCheckResult(ProviderRuntimeStatusKey key, long? revision, ProviderRuntimeStatus status)
    {
        if (!IsCapabilitySupported(key.Provider, key.Capability)) return;
        if (ProviderRuntimeStatus.IsNeutralReason(status.ReasonCode))
            status = status with { Health = ProviderHealthState.Unknown, CircuitOpen = false };
        _recordedChecks.AddOrUpdate(key, (revision, status), (_, previous) =>
            previous.Revision > revision || previous.Status.TestedAt > status.TestedAt ? previous : (revision, status));
    }

    public bool CanTestCapability(string provider, string capability) =>
        HasProbe(Normalize(provider), Normalize(capability));

    public bool CanTestAccountFreeCapability(string provider, string capability) =>
        CanRunWithoutAccount(provider, capability) && CanTestCapability(provider, capability);

    internal ProviderCheckResult? RecordDownloadVerification(ProviderCtsDiagnosticResult measurement, long? revision)
    {
        if (!measurement.Succeeded || !measurement.DownloadVerified || measurement.SampleBytes <= 0 ||
            !IsCapabilitySupported(measurement.ProviderId, ProviderCapabilities.Download)) return null;
        var key = measurement.ProviderAccountId is { } accountId
            ? ProviderRuntimeStatusKey.CreateManaged(measurement.ProviderId, ProviderCapabilities.Download, accountId)
            : ProviderRuntimeStatusKey.CreateAccountFree(measurement.ProviderId, ProviderCapabilities.Download);
        _health.Record(key, revision, true, (long?)measurement.ClickToStreamMilliseconds, null);
        var result = BuildBaselineStatus(key) with
        {
            Health = ProviderHealthState.Healthy,
            TestedAt = _clock.UtcNow,
            LatencyMilliseconds = (long?)measurement.ClickToStreamMilliseconds,
            ReasonCode = null,
            VerificationMeasurementId = measurement.MeasurementId,
            VerificationMethod = "streaming-download"
        };
        RestoreCheckResult(key, revision, result);
        return new(key, revision, result);
    }

    internal ProviderCheckResult? RecordStreamingMeasurement(ProviderCtsDiagnosticResult measurement, long? revision)
    {
        if (!IsCapabilitySupported(measurement.ProviderId, ProviderCapabilities.Streaming)) return null;
        var key = measurement.ProviderAccountId is { } accountId
            ? ProviderRuntimeStatusKey.CreateManaged(measurement.ProviderId, ProviderCapabilities.Streaming, accountId)
            : ProviderRuntimeStatusKey.CreateAccountFree(measurement.ProviderId, ProviderCapabilities.Streaming);
        var measured = !ProviderRuntimeStatus.IsNeutralReason(measurement.ReasonCode) &&
            measurement.Stage is not ("capability" or "account-resolution" or "concurrency");
        if (measured) _health.Record(key, revision, measurement.Succeeded, (long?)measurement.ClickToStreamMilliseconds, measurement.ReasonCode);
        var result = BuildBaselineStatus(key) with
        {
            Health = !measured ? ProviderHealthState.Unknown : measurement.Succeeded ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
            TestedAt = _clock.UtcNow,
            LatencyMilliseconds = (long?)measurement.ClickToStreamMilliseconds,
            ReasonCode = measurement.ReasonCode
        };
        RestoreCheckResult(key, revision, result);
        return new(key, revision, result, measurement.SeekRung);
    }

    private ProviderRuntimeStatus GetStatusCore(
        ProviderRuntimeStatusKey key,
        ProviderRuntimeStatus baseline,
        long? accountRevision = null)
    {
        if (key.Provider == "apple-musickit" &&
            key.Capability is ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Lyrics &&
            _appleDownloadSnapshot is { } apple && apple.State != AppleDownloadEndpointState.Available)
            return baseline with
            {
                Health = ProviderHealthState.Degraded,
                ReasonCode = apple.State == AppleDownloadEndpointState.NeedsAuthentication
                    ? ProviderError.AppleDownloadSignInRequired().Code : apple.ReasonCode == "unsupported_api_version"
                        ? ProviderError.AppleDownloadGatewayUpdateRequired().Code : apple.ReasonCode,
                TestedAt = _health.AppleDownloadObservedAt
            };
        if (key.Provider == "apple-musickit" &&
            key.Capability is ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Lyrics &&
            baseline.ReasonCode == "provider_account_required")
            return baseline with { ReasonCode = ProviderError.AppleDownloadSignInRequired().Code };
        if (key.Provider == AppleMusicClient.ProviderId &&
            key.Capability is ProviderCapabilities.Metadata or ProviderCapabilities.Playlist &&
            _services?.GetService<AppleWebTokenProvider>() is { FailureCode: not null } tokens)
            return baseline with { Health = ProviderHealthState.Degraded, ReasonCode = tokens.FailureCode, TestedAt = tokens.ObservedAt };
        if (!baseline.IsSupported) return baseline;
        // Display the newest evidence, with explicit checks winning completed ties; routing TTL cannot revive older checks.
        var observation = _health.GetForDisplay(key, accountRevision);
        if (_recordedChecks.TryGetValue(key, out var recorded) && recorded.Revision == accountRevision &&
            (observation == null || recorded.Status.TestedAt > observation.ObservedAt ||
                observation.Health != ProviderHealthState.Testing && recorded.Status.TestedAt == observation.ObservedAt))
            return baseline with
            {
                Health = recorded.Status.Health,
                TestedAt = recorded.Status.TestedAt,
                LatencyMilliseconds = recorded.Status.LatencyMilliseconds,
                VerificationMeasurementId = recorded.Status.VerificationMeasurementId,
                VerificationMethod = recorded.Status.VerificationMethod,
                ReasonCode = BaselineReasonTakesPrecedence(baseline) ? baseline.ReasonCode : recorded.Status.ReasonCode
            };
        if (observation == null) return baseline;
        var open = _health.IsCircuitOpen(observation);
        return baseline with
        {
            Health = observation.Health,
            TestedAt = observation.Health == ProviderHealthState.Testing ? null : observation.ObservedAt,
            LatencyMilliseconds = observation.LatencyMilliseconds,
            CircuitOpen = open,
            ReasonCode = BaselineReasonTakesPrecedence(baseline) ? baseline.ReasonCode :
                open ? "circuit_open" : observation.ReasonCode
        };
    }

    /// <summary>
    /// Explicitly probes one capability/account key. Status is observable as Testing
    /// while the request is in flight.
    /// </summary>
    public async Task<ProviderRuntimeStatus> TestAccountFreeProviderCapabilityAsync(
        string provider,
        string capability,
        CancellationToken cancellationToken = default)
    {
        RequireAccountFreeCapability(provider, capability);
        return await TestProviderCapabilityCoreAsync(
            provider,
            capability,
            providerAccountId: null,
            accountSecrets: null,
            cancellationToken, null);
    }

    public async Task<ProviderRuntimeStatus> TestManagedProviderCapabilityAsync(
        string provider,
        string capability,
        Guid providerAccountId,
        IReadOnlyDictionary<string, string> accountSecrets,
        CancellationToken cancellationToken = default,
        long? accountRevision = null) =>
        await TestProviderCapabilityCoreAsync(
            provider,
            capability,
            providerAccountId,
            accountSecrets,
            cancellationToken, accountRevision);

    private async Task<ProviderRuntimeStatus> TestProviderCapabilityCoreAsync(
        string provider,
        string capability,
        Guid? providerAccountId,
        IReadOnlyDictionary<string, string>? accountSecrets,
        CancellationToken cancellationToken,
        long? accountRevision)
    {
        var key = providerAccountId.HasValue
            ? ProviderRuntimeStatusKey.CreateManaged(provider, capability, providerAccountId.Value)
            : ProviderRuntimeStatusKey.CreateAccountFree(provider, capability);
        var baseline = BuildBaselineStatus(key);
        if (accountSecrets != null)
        {
            baseline = ApplyManagedAccountConfiguration(baseline, accountSecrets);
        }

        if (!baseline.IsSupported ||
            !baseline.IsEnabled ||
            baseline.Configuration == ProviderConfigurationState.NeedsConfiguration)
        {
            return baseline;
        }

        if (!HasProbe(key.Provider, key.Capability))
        {
            return baseline with { ReasonCode = "probe_not_available" };
        }

        var probeObservation = _health.BeginProbe(key, accountRevision);

        _logger.LogDebug(
            "Testing provider capability {Provider}/{Capability}",
            key.Provider,
            key.Capability);

        var startedAt = _clock.UtcNow;
        try
        {
            var probe = await ProbeCapabilityAsync(
                key.Provider,
                key.Capability,
                accountSecrets,
                cancellationToken, providerAccountId);
            if (key.Capability == ProviderCapabilities.Download && probe.Success)
                probe = new(false, "download-not-verified", probe.MeasuresLatency);
            if (probe.IsNeutral || ProviderRuntimeStatus.IsNeutralReason(probe.ReasonCode))
            {
                _health.CancelProbe(probeObservation);
                var previous = GetStatusCore(key, baseline, accountRevision);
                if (previous.VerificationMeasurementId.HasValue && previous.Health == ProviderHealthState.Healthy)
                    return previous;
                var neutral = baseline with { ReasonCode = probe.ReasonCode, TestedAt = _clock.UtcNow };
                RestoreCheckResult(key, accountRevision, neutral);
                return neutral;
            }
            if (key.Provider == "apple-musickit")
            {
                baseline = BuildBaselineStatus(key);
                if (accountSecrets != null) baseline = ApplyManagedAccountConfiguration(baseline, accountSecrets);
            }
            var failureReason = key.Provider == "apple-musickit" &&
                key.Capability is ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Lyrics &&
                _appleDownloadSnapshot != null
                ? _appleDownloadSnapshot.Capability(key.Capability).ReasonCode ?? _appleDownloadSnapshot.ReasonCode
                : "probe_failed";
            var latencyMilliseconds = probe.MeasuresLatency
                ? (long?)Math.Max(0, (_clock.UtcNow - startedAt).TotalMilliseconds)
                : null;
            var result = baseline with
            {
                Health = probe.Success ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
                TestedAt = _clock.UtcNow,
                LatencyMilliseconds = latencyMilliseconds,
                ReasonCode = probe.Success ? null : probe.ReasonCode ?? failureReason
            };

            _health.Record(key, accountRevision, probe.Success, latencyMilliseconds, result.ReasonCode);
            if (_recordedChecks.ContainsKey(key)) RestoreCheckResult(key, accountRevision, result);
            _logger.LogInformation(
                "Provider capability probe result: {Provider}/{Capability} => {Health} ({ReasonCode})",
                key.Provider,
                key.Capability,
                result.Health,
                result.ReasonCode ?? "none");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _health.CancelProbe(probeObservation);
            throw;
        }
        catch (Exception ex)
        {
            var result = baseline with
            {
                Health = ProviderHealthState.Degraded,
                TestedAt = _clock.UtcNow,
                LatencyMilliseconds = (long)Math.Max(0, (_clock.UtcNow - startedAt).TotalMilliseconds),
                ReasonCode = ex is OperationCanceledException ? "timeout" : "unreachable"
            };
            _health.Record(key, accountRevision, false, result.LatencyMilliseconds, result.ReasonCode);
            if (_recordedChecks.ContainsKey(key)) RestoreCheckResult(key, accountRevision, result);

            // Keep provider response bodies, URLs, and credentials out of status logs.
            _logger.LogWarning(
                "Provider capability probe failed: {Provider}/{Capability} ({ExceptionType})",
                key.Provider,
                key.Capability,
                ex.GetType().Name);
            return result;
        }
    }

    public async Task<bool> TestManagedProviderConnectionAsync(
        string provider,
        Guid providerAccountId,
        IReadOnlyDictionary<string, string> accountSecrets,
        CancellationToken cancellationToken = default,
        long? accountRevision = null)
    {
        var normalized = Normalize(provider);
        var capabilities = RuntimeCapabilities()
            .Where(item => item.Provider == normalized && HasProbe(item.Provider, item.Capability))
            .Select(item => item.Capability)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (capabilities.Length == 0)
        {
            return false;
        }

        var attempted = false;
        var healthy = true;
        foreach (var capability in capabilities)
        {
            var baseline = GetManagedStatus(normalized, capability, providerAccountId, accountSecrets, accountRevision);
            if (!baseline.IsSupported || !baseline.IsEnabled ||
                baseline.Configuration == ProviderConfigurationState.NeedsConfiguration)
            {
                continue;
            }

            var result = await TestManagedProviderCapabilityAsync(
                normalized,
                capability,
                providerAccountId,
                accountSecrets,
                cancellationToken, accountRevision);
            attempted |= result.HasCheckResult;
            healthy &= result.Health != ProviderHealthState.Degraded;
        }

        return attempted && healthy;
    }

    private ProviderRuntimeStatus BuildBaselineStatus(ProviderRuntimeStatusKey key)
    {
        var isSupported = IsCapabilitySupported(key.Provider, key.Capability);
        var isEnabled = isSupported && !GetDisabledProviders().Contains(key.Provider);
        var (configuration, reasonCode) = GetConfigurationState(key);

        if (!isSupported)
        {
            configuration = ProviderConfigurationState.NeedsConfiguration;
            reasonCode = "unsupported_capability";
        }
        else if (!isEnabled)
        {
            reasonCode = "disabled";
        }

        return new ProviderRuntimeStatus
        {
            Provider = key.Provider,
            Capability = key.Capability,
            IsSupported = isSupported,
            IsEnabled = isEnabled,
            Configuration = configuration,
            Health = ProviderHealthState.Unknown,
            TestedAt = null,
            LatencyMilliseconds = null,
            ReasonCode = reasonCode
        };
    }

    private (ProviderConfigurationState State, string? ReasonCode) GetConfigurationState(
        ProviderRuntimeStatusKey key)
    {
        var provider = key.Provider;
        var capability = key.Capability;
        if (TryGetExtensionCapability(provider, capability, out var extensionCapability))
        {
            return extensionCapability!.AccountRequirement == ProviderAccountRequirement.Required &&
                   !key.ProviderAccountId.HasValue
                ? (ProviderConfigurationState.NeedsConfiguration, "provider_account_required")
                : (key.ProviderAccountId.HasValue
                    ? ProviderConfigurationState.Configured
                    : ProviderConfigurationState.NotRequired, null);
        }

        return (provider, capability) switch
        {
            ("apple-musickit", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) =>
                (ProviderConfigurationState.NotRequired, null),
            ("apple-musickit", ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Lyrics) =>
                IsConfiguredValue(_appleMusicSettings.BaseUrl)
                    ? (ProviderConfigurationState.Configured, null)
                    : (ProviderConfigurationState.NeedsConfiguration, "missing_sidecar_url"),

            ("deezer", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) =>
                (ProviderConfigurationState.NotRequired, null),
            ("deezer", ProviderCapabilities.Streaming or ProviderCapabilities.Download) =>
                IsConfiguredValue(_deezerSettings.Arl)
                    ? (ProviderConfigurationState.Configured, null)
                    : (ProviderConfigurationState.NeedsConfiguration, "missing_deezer_arl"),

            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist or ProviderCapabilities.Streaming or ProviderCapabilities.Download) =>
                (ProviderConfigurationState.NeedsConfiguration, "missing_qobuz_account"),

            ("spotify", ProviderCapabilities.Playlist or ProviderCapabilities.Lyrics) =>
                IsConfiguredValue(_spotifySettings.SessionCookie)
                    ? (ProviderConfigurationState.Configured, null)
                    : (ProviderConfigurationState.NeedsConfiguration, "missing_spotify_session"),

            ("lrcred" or "unison" or "paxsenix" or "genius" or "lrclib" or "lyricsplus" or "musixmatch", ProviderCapabilities.Lyrics) =>
                (ProviderConfigurationState.NotRequired, null),

            _ => (ProviderConfigurationState.NeedsConfiguration, "unsupported_capability")
        };
    }

    private bool CanRunWithoutAccount(string provider, string capability)
    {
        var normalizedProvider = Normalize(provider);
        var normalizedCapability = Normalize(capability);
        return RuntimeCapabilities().Any(item =>
            item.Provider == normalizedProvider &&
            item.Capability == normalizedCapability &&
            item.AccountRequirement != ProviderAccountRequirement.Required);
    }

    private void RequireAccountFreeCapability(string provider, string capability)
    {
        if (!CanRunWithoutAccount(provider, capability))
        {
            throw new InvalidOperationException(
                $"Provider capability '{Normalize(provider)}/{Normalize(capability)}' requires an explicit provider account.");
        }
    }

    private ProviderRuntimeStatus ApplyManagedAccountConfiguration(
        ProviderRuntimeStatus baseline,
        IReadOnlyDictionary<string, string> secrets)
    {
        if (ProviderRegistry?.TryGet(baseline.Provider, out var descriptor) == true &&
            descriptor!.Origin == ProviderOrigin.Extension)
        {
            var required = descriptor.Settings
                .Where(setting => setting.Required && setting.DefaultJson == null)
                .Select(setting => NormalizeSettingKey(setting.Key))
                .ToArray();
            var extensionConfigured = required.All(secrets.ContainsKey);
            return extensionConfigured
                ? baseline with { Configuration = ProviderConfigurationState.Configured, ReasonCode = null }
                : baseline with
                {
                    Configuration = ProviderConfigurationState.NeedsConfiguration,
                    ReasonCode = "missing_provider_account_secret"
                };
        }

        bool? configured = (baseline.Provider, baseline.Capability) switch
        {
            ("musixmatch", ProviderCapabilities.Lyrics) => IsConfiguredValue(SecretValue(secrets, "usertoken")),
            ("spicylyrics", ProviderCapabilities.Lyrics) => IsConfiguredValue(SecretValue(secrets, "apikey")),
            ("apple-musickit", _) => IsConfiguredValue(SecretValue(secrets, "musicusertoken", "mediausertoken")) &&
                (baseline.Capability is ProviderCapabilities.Metadata or ProviderCapabilities.Playlist || IsConfiguredValue(_appleMusicSettings.BaseUrl)),
            ("spotify", ProviderCapabilities.Playlist or ProviderCapabilities.Lyrics) =>
                IsConfiguredValue(SecretValue(secrets, "sessioncookie", "spdc", "cookie")),
            ("deezer", ProviderCapabilities.Streaming or ProviderCapabilities.Download) =>
                IsConfiguredValue(SecretValue(secrets, "arl")),
            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist or ProviderCapabilities.Streaming or ProviderCapabilities.Download) =>
                IsConfiguredValue(SecretValue(secrets, "userauthtoken", "token")) &&
                IsConfiguredValue(SecretValue(secrets, "userid")),
            ("lastfm", ProviderCapabilities.Scrobbling) =>
                IsConfiguredValue(SecretValue(secrets, "apikey")) &&
                IsConfiguredValue(SecretValue(secrets, "sharedsecret")) &&
                IsConfiguredValue(SecretValue(secrets, "sessionkey")),
            ("listenbrainz" or "multi-scrobbler" or "koito", ProviderCapabilities.Scrobbling) =>
                IsConfiguredValue(SecretValue(secrets, "token", "usertoken")) && HasListeningEndpoint(baseline.Provider, secrets),
            ("audiomuse-ai", ProviderCapabilities.Intelligence) =>
                AudioMuseCredential.TryBaseUri(SecretValue(secrets, "baseurl"), out _),
            _ => null
        };

        if (!configured.HasValue)
        {
            return baseline;
        }

        return configured.Value
            ? baseline with
            {
                Configuration = ProviderConfigurationState.Configured,
                ReasonCode = null
            }
            : baseline with
            {
                Configuration = ProviderConfigurationState.NeedsConfiguration,
                ReasonCode = "missing_provider_account_secret"
            };
    }

    public void RecordTryResult(string provider, string capability, Guid? accountId, long? revision,
        bool succeeded, long elapsedMilliseconds, string? reasonCode)
    {
        if (!IsCapabilitySupported(Normalize(provider), Normalize(capability))) return;
        reasonCode = NormalizeTryReason(reasonCode);
        if (ProviderRuntimeStatus.IsNeutralReason(reasonCode)) return;
        if (!succeeded && reasonCode is "not-found" or "incompatible-media" or "not-supported" or
            "canceled" or "busy" or "temporary-storage-full" or "invalid-input" or "needs-artist") return;
        var key = accountId.HasValue ? ProviderRuntimeStatusKey.CreateManaged(provider, capability, accountId.Value) :
            ProviderRuntimeStatusKey.CreateAccountFree(provider, capability);
        _health.Record(key, revision, succeeded, elapsedMilliseconds, reasonCode);
        if (_recordedChecks.ContainsKey(key))
            RestoreCheckResult(key, revision, BuildBaselineStatus(key) with
            {
                Health = succeeded ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
                TestedAt = _clock.UtcNow,
                LatencyMilliseconds = elapsedMilliseconds,
                ReasonCode = reasonCode
            });
    }

    private static string? NormalizeTryReason(string? reason) =>
        new string((reason ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()) switch
        {
            "" => null,
            "notfound" or "noresults" => "not-found",
            "incompatiblemedia" => "incompatible-media",
            "notsupported" => "not-supported",
            "canceled" or "cancelled" => "canceled",
            "busy" => "busy",
            "temporarystoragefull" => "temporary-storage-full",
            "invalidinput" => "invalid-input",
            "needsartist" => "needs-artist",
            "transientfailure" => "transient-failure",
            "permanentfailure" => "permanent-failure",
            "timeout" => "timeout",
            "blockedbychallenge" => "blocked-by-challenge",
            "lyricsupstreambusyortimedout" => "lyrics-upstream-busy-or-timed-out",
            "unauthorized" or "providerunauthorized" => "unauthorized",
            "accountneedsreauthentication" => "account-needs-reauthentication",
            "accountneedsconfiguration" => "account-needs-configuration",
            "ratelimited" => "rate-limited",
            "providercontractchanged" => "provider-contract-changed",
            "capabilityunavailable" => "capability-unavailable",
            "applewebtokenunavailable" => "apple-web-token-unavailable",
            "unreachable" => "unreachable",
            "downloadnotverified" => "download-not-verified",
            "probenotavailable" => "probe_not_available",
            _ => "probe_failed"
        };

    private bool IsCapabilitySupported(string provider, string capability)
    {
        if (TryGetExtensionCapability(provider, capability, out var extensionCapability))
            return extensionCapability!.HasUsableImplementation;
        return (provider, capability) switch
        {
            ("apple-musickit", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) => true,
            ("apple-musickit", ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Lyrics) =>
                _appleDownloadSnapshot?.Capability(capability).State != AppleDownloadCapabilityState.Unsupported,
            ("deezer", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Playlist) => true,
            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Playlist) => true,
            ("spotify", ProviderCapabilities.Playlist or ProviderCapabilities.Lyrics) => true,
            ("lrcred" or "unison" or "paxsenix" or "genius" or "lrclib" or "lyricsplus" or "musixmatch", ProviderCapabilities.Lyrics) => true,
            ("spicylyrics", ProviderCapabilities.Lyrics) => true,
            ("lastfm", ProviderCapabilities.Scrobbling) => true,
            ("listenbrainz" or "multi-scrobbler" or "koito", ProviderCapabilities.Scrobbling) => true,
            ("audiomuse-ai", ProviderCapabilities.Intelligence) => true,
            _ => false
        };
    }

    private bool HasProbe(string provider, string capability)
    {
        if (TryGetExtensionCapability(provider, capability, out var extensionCapability))
        {
            if (!extensionCapability!.HasUsableImplementation) return false;
            var descriptor = ProviderRegistry!.GetRequired(provider);
            var kind = extensionCapability.Capability;
            if (descriptor.HealthProbe && descriptor.Capabilities.Any(item => item.Capability == ProviderCapabilityKind.Health &&
                item.Hooks.Contains("probe" + kind))) return true;
            return kind switch
            {
                ProviderCapabilityKind.Metadata => extensionCapability.Hooks.Contains("searchTracks"),
                ProviderCapabilityKind.Playlist => extensionCapability.Hooks.Contains("getUserPlaylists"),
                ProviderCapabilityKind.Streaming => extensionCapability.Hooks.Contains("getStreamLease"),
                ProviderCapabilityKind.Download => extensionCapability.Hooks.Contains("checkAvailability"),
                ProviderCapabilityKind.Lyrics => extensionCapability.Hooks.Contains("fetchLyrics"),
                _ => false
            };
        }
        return (provider, capability) switch
        {
            ("apple-musickit", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist or ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Lyrics) => true,
            ("deezer", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist or ProviderCapabilities.Streaming or ProviderCapabilities.Download) => true,
            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist or ProviderCapabilities.Streaming or ProviderCapabilities.Download) => true,
            ("spotify", ProviderCapabilities.Playlist or ProviderCapabilities.Lyrics) => true,
            ("lrcred" or "unison" or "paxsenix" or "genius" or "lrclib" or "lyricsplus" or "musixmatch", ProviderCapabilities.Lyrics) => true,
            ("spicylyrics", ProviderCapabilities.Lyrics) => true,
            ("lastfm", ProviderCapabilities.Scrobbling) => true,
            ("listenbrainz" or "multi-scrobbler" or "koito", ProviderCapabilities.Scrobbling) => true,
            ("audiomuse-ai", ProviderCapabilities.Intelligence) => true,
            _ => false
        };
    }

    private async Task<ProbeOutcome> ProbeCapabilityAsync(
        string provider,
        string capability,
        IReadOnlyDictionary<string, string>? accountSecrets,
        CancellationToken cancellationToken,
        Guid? providerAccountId = null)
    {
        if (provider == "spicylyrics" && capability == ProviderCapabilities.Lyrics)
            return await ProbeDiagnosticAsync(provider, ProviderCapabilityKind.Lyrics, providerAccountId, cancellationToken);
        if (TryGetExtensionCapability(provider, capability, out var extensionCapability))
        {
            if (!extensionCapability!.HasUsableImplementation) return new(false, "probe_not_available");
            return await ProbeDiagnosticAsync(provider, extensionCapability.Capability, providerAccountId, cancellationToken);
        }
        return (provider, capability) switch
        {

            ("spotify", ProviderCapabilities.Playlist) => await TestSpotifyPlaylistAsync(
                SecretValue(accountSecrets, "sessioncookie", "spdc", "cookie") ?? _spotifySettings.SessionCookie,
                cancellationToken),
            ("spotify", ProviderCapabilities.Lyrics) => await TestSpotifyLyricsAsync(
                SecretValue(accountSecrets, "sessioncookie", "spdc", "cookie") ?? _spotifySettings.SessionCookie, cancellationToken),
            ("apple-musickit", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) => await TestAppleMusicAsync(capability, accountSecrets, cancellationToken),
            ("apple-musickit", ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Lyrics) => await AsOutcome(TestAppleDownloadAsync(capability, cancellationToken)),
            ("deezer", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) => await TestDeezerMetadataAsync(cancellationToken),
            ("deezer", ProviderCapabilities.Streaming or ProviderCapabilities.Download) => await AsOutcome(TestDeezerAccountAsync(
                SecretValue(accountSecrets, "arl") ?? _deezerSettings.Arl,
                cancellationToken)),
            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) => await TestQobuzMetadataAsync(
                SecretValue(accountSecrets, "userauthtoken", "token"), cancellationToken),
            ("qobuz", ProviderCapabilities.Streaming or ProviderCapabilities.Download) => await AsOutcome(TestQobuzAccountAsync(
                SecretValue(accountSecrets, "userauthtoken", "token") ?? _qobuzSettings.UserAuthToken,
                SecretValue(accountSecrets, "userid") ?? _qobuzSettings.UserId,
                cancellationToken)),
            ("lrclib", ProviderCapabilities.Lyrics) => await TestLrclibAsync(cancellationToken),
            ("lrcred" or "unison" or "paxsenix" or "genius" or "lyricsplus" or "musixmatch", ProviderCapabilities.Lyrics) => await TestMetadataLyricsAsync(provider, accountSecrets, cancellationToken),
            ("lastfm", ProviderCapabilities.Scrobbling) => await AsOutcome(TestLastFmAsync(accountSecrets, cancellationToken)),
            ("listenbrainz" or "multi-scrobbler" or "koito", ProviderCapabilities.Scrobbling) => await AsOutcome(TestListenBrainzAsync(provider, accountSecrets, cancellationToken)),
            ("audiomuse-ai", ProviderCapabilities.Intelligence) => await TestAudioMuseAsync(accountSecrets, cancellationToken),
            _ => new ProbeOutcome(false, "probe_not_available")
        };
    }


    private async Task<ProbeOutcome> ProbeDiagnosticAsync(string provider, ProviderCapabilityKind kind,
        Guid? providerAccountId, CancellationToken cancellationToken)
    {
        var factory = _services?.GetService<ProviderDiagnosticExecutionFactory>();
        var operations = _services?.GetService<ProviderTryOperations>();
        if (factory == null || operations == null) return new(false, "probe_not_available");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var actor = new ProviderActorContext(ProviderActorKind.SystemJob, null, durableJobId: Guid.NewGuid());
        if (providerAccountId.HasValue)
        {
            var database = _services?.GetService<IDbContextFactory<AllstarrDbContext>>();
            if (database == null) return new(false, "administrator_required");
            await using var db = await database.CreateDbContextAsync(linked.Token);
            var administrator = await db.Users.AsNoTracking().Where(item => item.Enabled && item.IsAdmin)
                .OrderBy(item => item.Id).FirstOrDefaultAsync(linked.Token);
            if (administrator == null) return new(false, "administrator_required");
            actor = new(ProviderActorKind.Administrator, administrator.Id,
                new ProviderBackendPrincipal(administrator.BackendType, administrator.BackendInstanceId, administrator.BackendPrincipalId));
        }
        var prepared = await factory.CreateAsync(actor, provider, kind, providerAccountId,
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(30), linked.Token);
        if (prepared.Context == null) return new(false, prepared.ReasonCode);
        var outcome = await operations.ProbeAsync(prepared.Context, ProviderRegistry!.GetRequired(provider), kind);
        cancellationToken.ThrowIfCancellationRequested();
        return linked.IsCancellationRequested ? new(false, "timeout") : new(outcome.Succeeded, outcome.ReasonCode);
    }

    private async Task<ProbeOutcome> TestAudioMuseAsync(
        IReadOnlyDictionary<string, string>? accountSecrets,
        CancellationToken cancellationToken)
    {
        if (!AudioMuseCredential.TryBaseUri(SecretValue(accountSecrets, "baseurl"), out var baseUri))
            return new(false, "missing_audiomuse_url");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri!, "api/health"));
        var token = SecretValue(accountSecrets, "apitoken", "token");
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _httpClientFactory
                .CreateClient(AudioMuseIntelligenceCapabilityAdapter.HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(false, "audiomuse_auth_rejected"),
                >= HttpStatusCode.InternalServerError => new(false, "audiomuse_unavailable"),
                _ => new(response.IsSuccessStatusCode,
                    response.IsSuccessStatusCode ? null : "audiomuse_probe_failed")
            };
        }
        catch (HttpRequestException)
        {
            return new(false, "audiomuse_unreachable");
        }
    }

    private static async Task<ProbeOutcome> AsOutcome(Task<bool> probe) => new(await probe);

    private IReadOnlyList<(string Provider, string Capability, ProviderAccountRequirement AccountRequirement)> RuntimeCapabilities()
    {
        var extensions = ProviderRegistry?.Providers
            .Where(provider => provider.Origin == ProviderOrigin.Extension || provider.HealthProbe)
            .SelectMany(provider => provider.Capabilities
                .Where(capability => capability.Capability != ProviderCapabilityKind.Health)
                .Select(capability => (
                    Provider: provider.Id,
                    Capability: capability.Capability.ToString().ToLowerInvariant(),
                    AccountRequirement: capability.AccountRequirement))) ?? [];
        return KnownCapabilities
            .Concat(extensions)
            .DistinctBy(item => (item.Provider, item.Capability))
            .ToArray();
    }

    private bool TryGetExtensionCapability(
        string provider,
        string capability,
        out ProviderCapabilityDescriptor? descriptor)
    {
        descriptor = null;
        if (ProviderRegistry?.TryGet(provider, out var providerDescriptor) != true ||
            providerDescriptor!.Origin != ProviderOrigin.Extension && !providerDescriptor.HealthProbe ||
            !Enum.TryParse<ProviderCapabilityKind>(capability, true, out var kind))
            return false;
        descriptor = providerDescriptor.Capabilities.SingleOrDefault(item => item.Capability == kind);
        return descriptor != null;
    }

    private static string NormalizeSettingKey(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private IProviderRegistry? ProviderRegistry =>
        _services?.GetService<IProviderRegistry>();

    private async Task<bool> TestLastFmAsync(
        IReadOnlyDictionary<string, string>? secrets,
        CancellationToken cancellationToken)
    {
        var apiKey = SecretValue(secrets, "apikey");
        var sharedSecret = SecretValue(secrets, "sharedsecret");
        var sessionKey = SecretValue(secrets, "sessionkey");
        if (!IsConfiguredValue(apiKey) || !IsConfiguredValue(sharedSecret) || !IsConfiguredValue(sessionKey))
        {
            return false;
        }

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_key"] = apiKey!,
            ["method"] = "user.getInfo",
            ["sk"] = sessionKey!
        };
        var signatureText = string.Concat(parameters.Select(item => item.Key + item.Value)) + sharedSecret;
        parameters["api_sig"] = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(signatureText)));
        parameters["format"] = "json";
        using var client = _httpClientFactory.CreateClient();
        using var response = await SendWithProbeTimeoutAsync(
            client,
            new HttpRequestMessage(HttpMethod.Post, "https://ws.audioscrobbler.com/2.0/")
            {
                Content = new FormUrlEncodedContent(parameters)
            },
            cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static bool HasListeningEndpoint(string provider, IReadOnlyDictionary<string, string>? secrets)
    {
        try { ListenBrainzServiceEndpoint.FromSecret(secrets, provider); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private async Task<bool> TestListenBrainzAsync(
        string provider,
        IReadOnlyDictionary<string, string>? secrets,
        CancellationToken cancellationToken)
    {
        var token = SecretValue(secrets, "token", "usertoken");
        if (!IsConfiguredValue(token))
        {
            return false;
        }

        var baseUri = ListenBrainzServiceEndpoint.FromSecret(secrets, provider);
        using var client = _httpClientFactory.CreateClient("LastFm");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            ListenBrainzServiceEndpoint.Route(baseUri, "validate-token"));
        request.Headers.Authorization = new("Token", token);
        using var response = await SendWithProbeTimeoutAsync(client, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.True;
    }

    private async Task<ProbeOutcome> TestMetadataLyricsAsync(string provider,
        IReadOnlyDictionary<string, string>? secrets, CancellationToken cancellationToken)
    {
        var result = await _diagnosticSamples.CheckAsync($"{provider}:lyrics",
            ProviderDiagnosticSamples.Recordings(provider), async (sample, token) =>
            {
                var outcome = await TestMetadataLyricsSampleAsync(provider, secrets, sample, token);
                return new ProviderSampleCheck(outcome.Success, outcome.ReasonCode,
                    outcome.ReasonCode is "sample-unavailable" or "not-found");
            }, ct => DiscoverLyricsSamplesAsync(provider, ct), cancellationToken,
            timeoutReason: provider == "paxsenix" ? ProviderError.LyricsUpstreamBusyOrTimedOut().Code : "timeout",
            attemptTimeout: provider == "paxsenix" ? allstarr.Core.Providers.Lyrics.PaxsenixCapabilityAdapter.DiagnosticLookupBudget + TimeSpan.FromSeconds(1) :
                provider == "genius" ? allstarr.Core.Providers.Lyrics.MetadataLyricsCapabilityAdapter.OverallLookupBudget : null);
        return new(result.Success, result.ReasonCode, IsNeutral: result.SampleUnavailable);
    }

    private async IAsyncEnumerable<ProviderDiagnosticSample> DiscoverLyricsSamplesAsync(string provider,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var selector = _services?.GetService<ProviderCtsTrackSelector>();
        var recordings = selector == null ? [] : await selector.SelectRecordingSamplesAsync(token);
        foreach (var recording in recordings.Take(8))
            yield return new(recording.Title, string.Join(", ", recording.ArtistNames), recording.Isrc,
                (int?)(recording.DurationMilliseconds / 1000) ?? 200,
                recording.ProviderIds.TryGetValue(provider, out var id) ? id : null);
        if (provider != "lrclib") yield break;
        using var client = _httpClientFactory.CreateClient();
        foreach (var query in ProviderDiagnosticSamples.DiscoveryQueries(provider).Take(4))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}");
            using var response = await SendWithProbeTimeoutAsync(client, request, token);
            if (!response.IsSuccessStatusCode) continue;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (document.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in document.RootElement.EnumerateArray().Take(2))
                if (item.TryGetProperty("trackName", out var title) && title.ValueKind == JsonValueKind.String &&
                    item.TryGetProperty("artistName", out var artist) && artist.ValueKind == JsonValueKind.String)
                    yield return new(title.GetString()!, artist.GetString()!, null,
                        item.TryGetProperty("duration", out var duration) && duration.TryGetDouble(out var seconds) &&
                        double.IsFinite(seconds) && seconds is > 0 and < 86400 ? (int)seconds : 200);
        }
    }

    private async Task<ProbeOutcome> TestMetadataLyricsSampleAsync(string provider,
        IReadOnlyDictionary<string, string>? secrets, ProviderDiagnosticSample sample, CancellationToken cancellationToken)
    {
        var probeTimeout = provider == "paxsenix"
            ? allstarr.Core.Providers.Lyrics.PaxsenixCapabilityAdapter.DiagnosticLookupBudget + TimeSpan.FromSeconds(1)
            : provider == "genius" ? allstarr.Core.Providers.Lyrics.MetadataLyricsCapabilityAdapter.OverallLookupBudget : ProbeTimeout;
        var solver = provider == "lyricsplus" ? _services?.GetService<CloudflareChallengeSolver>() : null;
        if (solver != null && await solver.ConfiguredTimeoutAsync(cancellationToken) is { } solveTimeout)
            probeTimeout += solveTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(probeTimeout);
        var request = new ProviderLyricsRequest(Guid.NewGuid(), new(provider, ProviderResourceKind.Track, "connection-probe"),
            availabilityOnly: true, trackTitle: sample.Title, artistNames: [sample.Artist], durationSeconds: sample.DurationSeconds,
            isrc: sample.Isrc);
        ProviderOutcome<ProviderLyricsResult> result;
        if (provider == "musixmatch")
        {
            var adapter = _services?.GetService<allstarr.Core.Providers.Lyrics.MusixmatchLyricsCapabilityAdapter>();
            if (secrets == null && adapter != null)
                result = await adapter.ReadAutomaticLyricsAsync(request, timeout.Token);
            else
            {
                using var client = _httpClientFactory.CreateClient(allstarr.Core.Providers.Lyrics.MusixmatchLyricsCapabilityAdapter.HttpClientName);
                if (secrets == null)
                    result = await allstarr.Core.Providers.Lyrics.MusixmatchLyricsCapabilityAdapter.ReadAutomaticLyricsAsync(
                        client, _musixmatchTokens.Value, request, timeout.Token, _logger);
                else if (allstarr.Core.Providers.Lyrics.MusixmatchCredential.TryRead(JsonSerializer.SerializeToUtf8Bytes(secrets), out var credential))
                    result = await allstarr.Core.Providers.Lyrics.MusixmatchLyricsCapabilityAdapter.ReadLyricsFromCredentialAsync(
                        client, credential, request, timeout.Token, _logger);
                else return new(false, "account_needs_configuration");
            }
        }
        else if (provider is "lrcred" or "unison" or "paxsenix" or "genius")
        {
            using var client = _httpClientFactory.CreateClient(provider switch
            {
                "lrcred" => allstarr.Core.Providers.Lyrics.LrcRedCapabilityAdapter.HttpClientName,
                "unison" => allstarr.Core.Providers.Lyrics.UnisonCapabilityAdapter.HttpClientName,
                "genius" => allstarr.Core.Providers.Lyrics.GeniusCapabilityAdapter.HttpClientName,
                _ => allstarr.Core.Providers.Lyrics.PaxsenixCapabilityAdapter.HttpClientName
            });
            var apple = provider == "paxsenix" ? _services?.GetService<AppleMusicClient>() : null;
            var paxsenix = provider == "paxsenix"
                ? _services?.GetService<allstarr.Core.Providers.Lyrics.PaxsenixCapabilityAdapter>() : null;
            if (provider == "paxsenix" && apple == null && paxsenix == null) return new(false, "apple-web-token-unavailable");
            IProviderLyricsCapability adapter = provider switch
            {
                "lrcred" => new allstarr.Core.Providers.Lyrics.LrcRedCapabilityAdapter(client, null),
                "unison" => new allstarr.Core.Providers.Lyrics.UnisonCapabilityAdapter(client, null),
                "genius" => new allstarr.Core.Providers.Lyrics.GeniusCapabilityAdapter(client, null),
                _ => paxsenix ?? new allstarr.Core.Providers.Lyrics.PaxsenixCapabilityAdapter(client, null,
                    new AppleMusicKitMetadataCapabilityAdapter(apple!))
            };
            var context = new ProviderExecutionContext(new(ProviderActorKind.PublicRead, null), provider, null,
                new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                    true, true, false, [provider]), "connection-probe", "connection-probe", _clock.UtcNow.Add(probeTimeout), timeout.Token);
            result = await adapter.FetchLyricsAsync(context, request);
        }
        else
        {
            using var client = _httpClientFactory.CreateClient(allstarr.Core.Providers.Lyrics.LyricsPlusCapabilityAdapter.HttpClientName);
            var adapter = new allstarr.Core.Providers.Lyrics.LyricsPlusCapabilityAdapter(client, null,
                async ct => _services?.GetService<allstarr.Core.Settings.IDurableRuntimeSettings>() is { } settings
                    ? (string)(await settings.GetAsync("LyricsPlus:BaseUrl", ct)).Value
                    : _configuration["LyricsPlus:BaseUrl"] ?? string.Empty, solver: solver);
            var context = new ProviderExecutionContext(new(ProviderActorKind.PublicRead, null), provider, null,
                new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                    true, true, false, [provider]), "connection-probe", "connection-probe", _clock.UtcNow.Add(probeTimeout), timeout.Token);
            result = await adapter.FetchLyricsAsync(context, request);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (provider == "paxsenix" && result.Error?.Kind is ProviderErrorKind.TransientFailure or ProviderErrorKind.Canceled)
            return new(false, ProviderError.LyricsUpstreamBusyOrTimedOut().Code);
        return result.IsSuccess
            ? new(result.RequireValue().Availability == ProviderLyricsAvailabilityState.Available,
                result.RequireValue().Availability == ProviderLyricsAvailabilityState.Available ? null : "sample-unavailable")
            : new(false, ProviderDiagnosticSamples.FromError(result.Error!).ReasonCode);
    }

    private async Task<ProbeOutcome> TestLrclibAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        var result = await _diagnosticSamples.CheckAsync("lrclib:lyrics",
            ProviderDiagnosticSamples.Recordings("lrclib"), async (sample, token) =>
            {
                var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(sample.Artist)}&track_name={Uri.EscapeDataString(sample.Title)}&duration={sample.DurationSeconds}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await SendWithProbeTimeoutAsync(client, request, token);
                var check = HttpSampleCheck(response);
                if (!check.Success) return check;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var root = document.RootElement;
                return root.TryGetProperty("plainLyrics", out var plain) && plain.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(plain.GetString()) ||
                    root.TryGetProperty("syncedLyrics", out var synced) && synced.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(synced.GetString()) ||
                    root.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True
                    ? new(true) : ProviderSampleCheck.Missing;
            }, ct => DiscoverLyricsSamplesAsync("lrclib", ct), cancellationToken);
        return new(result.Success, result.ReasonCode, IsNeutral: result.SampleUnavailable);
    }

    private static ProviderSampleCheck HttpSampleCheck(HttpResponseMessage response) => response.StatusCode switch
    {
        System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone => ProviderSampleCheck.Missing,
        System.Net.HttpStatusCode.TooManyRequests => new(false, "rate-limited"),
        System.Net.HttpStatusCode.Unauthorized => new(false, "authentication-required"),
        System.Net.HttpStatusCode.Forbidden => new(false, "access-denied"),
        _ => response.IsSuccessStatusCode ? new(true) : new(false, "service_unavailable")
    };

    private async Task<ProbeOutcome> TestSpotifyPlaylistAsync(
        string? sessionCookie,
        CancellationToken cancellationToken)
    {
        sessionCookie = allstarr.Core.Providers.Spotify.SpotifySessionCookie.Normalize(sessionCookie);
        if (sessionCookie == null)
        {
            return new ProbeOutcome(false, "account_needs_configuration");
        }
        using var client = _httpClientFactory.CreateClient();
        var result = await allstarr.Core.Providers.Spotify.SpotifyWebTokenExchange.ExchangeAsync(client, sessionCookie, cancellationToken);
        return new ProbeOutcome(result.Success, result.ReasonCode);
    }

    private async Task<ProbeOutcome> TestSpotifyLyricsAsync(string? cookie, CancellationToken cancellationToken)
    {
        cookie = allstarr.Core.Providers.Spotify.SpotifySessionCookie.Normalize(cookie);
        if (cookie == null) return new(false, "account_needs_configuration");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        using var client = _httpClientFactory.CreateClient(allstarr.Core.Providers.Spotify.SpotifyPlaylistCapabilityAdapter.HttpClientName);
        var token = await allstarr.Core.Providers.Spotify.SpotifyWebTokenExchange.ExchangeAsync(
            client, cookie, timeout.Token, _spotifySettings);
        if (!token.Success) return new(false, token.ReasonCode);
        var result = await _diagnosticSamples.CheckAsync("spotify:lyrics", ProviderDiagnosticSamples.Recordings("spotify"),
            async (sample, ct) =>
            {
                var lyrics = await allstarr.Core.Providers.Spotify.SpotifyLyricsCapabilityAdapter.ReadLyricsFromTokenAsync(
                    client, token.AccessToken!, new ProviderLyricsRequest(Guid.NewGuid(),
                        new("spotify", ProviderResourceKind.Track, sample.TrackId ?? "connection-probe"), availabilityOnly: true,
                        trackTitle: sample.Title, artistNames: [sample.Artist], isrc: sample.Isrc), ct);
                return !lyrics.IsSuccess ? ProviderDiagnosticSamples.FromError(lyrics.Error!) :
                    lyrics.RequireValue().Availability == ProviderLyricsAvailabilityState.Available ? new(true) : ProviderSampleCheck.Missing;
            }, ct => DiscoverSpotifyLyricsSamplesAsync(client, token.AccessToken!, ct), cancellationToken);
        return new(result.Success, result.ReasonCode, IsNeutral: result.SampleUnavailable);
    }

    private async IAsyncEnumerable<ProviderDiagnosticSample> DiscoverSpotifyLyricsSamplesAsync(HttpClient client, string accessToken,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var sample in DiscoverLyricsSamplesAsync("spotify", token))
            if (sample.TrackId != null) yield return sample;
        foreach (var query in ProviderDiagnosticSamples.DiscoveryQueries("spotify").Take(4))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.spotify.com/v1/search?type=track&limit=2&q={Uri.EscapeDataString(query)}");
            request.Headers.Authorization = new("Bearer", accessToken);
            using var response = await SendWithProbeTimeoutAsync(client, request, token);
            if (!response.IsSuccessStatusCode) continue;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!document.RootElement.TryGetProperty("tracks", out var tracks) ||
                !tracks.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray().Take(2))
            {
                if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                yield return new(name.GetString()!, query, null, 200, id.GetString());
            }
        }
    }

    private async Task<ProbeOutcome> TestAppleMusicAsync(string capability,
        IReadOnlyDictionary<string, string>? accountSecrets, CancellationToken cancellationToken)
    {
        var client = _services?.GetService<AppleMusicClient>();
        if (client == null) return new(false, "probe_not_available");
        var token = SecretValue(accountSecrets, "musicusertoken", "mediausertoken");
        var configuredStorefront = SecretValue(accountSecrets, "storefront") ?? AppleMusicCredential.DefaultStorefront;
        var credential = token == null ? null : AppleMusicCredential.Read(JsonSerializer.SerializeToUtf8Bytes(new { MusicUserToken = token, Storefront = configuredStorefront }));
        if (accountSecrets != null && credential == null) return new(false, "missing_provider_account_secret");
        var storefront = credential?.Storefront ?? AppleMusicCredential.NormalizeStorefront(configuredStorefront) ??
                         AppleMusicCredential.DefaultStorefront;
        var personal = capability == ProviderCapabilities.Playlist && credential != null;
        var types = capability == ProviderCapabilities.Playlist ? "playlists" : "songs";
        try
        {
            var response = await client.SendAsync(personal ? credential : null,
                personal ? "v1/me/library/playlists?limit=1" : $"v1/catalog/{storefront}/search?term=music&types={types}&limit=1",
                cancellationToken);
            return new(response.Outcome.IsSuccess, response.Error?.Code);
        }
        catch (AppleWebTokenUnavailableException) { return new(false, "apple-web-token-unavailable"); }
    }

    private async Task<bool> TestAppleDownloadAsync(string capability, CancellationToken cancellationToken)
    {
        if (_appleDownloadDiscovery == null) return false;
        _appleDownloadSnapshot = await _appleDownloadDiscovery.DiscoverAsync(cancellationToken);
        return _appleDownloadSnapshot.State == AppleDownloadEndpointState.Available &&
               _appleDownloadSnapshot.Capability(capability).State == AppleDownloadCapabilityState.Available;
    }

    private async Task<ProbeOutcome> TestDeezerMetadataAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        var result = await _diagnosticSamples.CheckAsync("deezer:metadata", ProviderDiagnosticSamples.Recordings("deezer"),
            async (sample, token) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.deezer.com/search?q={Uri.EscapeDataString(sample.Query)}&limit=1");
                using var response = await SendWithProbeTimeoutAsync(client, request, token);
                var check = HttpSampleCheck(response);
                if (!check.Success) return check;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                return document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array &&
                    data.EnumerateArray().Any(item => item.TryGetProperty("id", out _)) ? new(true) : ProviderSampleCheck.Missing;
            }, token => DiscoverQueriesAsync("deezer", token), cancellationToken);
        return new(result.Success, result.ReasonCode, IsNeutral: result.SampleUnavailable);
    }

    private static async IAsyncEnumerable<ProviderDiagnosticSample> DiscoverQueriesAsync(string provider,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        foreach (var query in ProviderDiagnosticSamples.DiscoveryQueries(provider))
        {
            token.ThrowIfCancellationRequested();
            yield return new(query, "", null, 200);
        }
    }

    private async Task<bool> TestDeezerAccountAsync(
        string? arl,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://www.deezer.com/ajax/gw-light.php?method=deezer.getUserData&input=3&api_version=1.0&api_token=null");
        request.Headers.Add("Cookie", $"arl={arl}");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        using var response = await SendWithProbeTimeoutAsync(client, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("results", out var results) &&
            results.TryGetProperty("USER", out var user) &&
            user.TryGetProperty("USER_ID", out var userId))
        {
            return userId.ValueKind switch
            {
                JsonValueKind.Number => userId.TryGetInt64(out var numericId) && numericId > 0,
                JsonValueKind.String => long.TryParse(userId.GetString(), out var stringId) && stringId > 0,
                _ => false
            };
        }

        return false;
    }

    private async Task<ProbeOutcome> TestQobuzMetadataAsync(string? userAuthToken, CancellationToken cancellationToken)
    {
        const string appId = "798273057";
        using var client = _httpClientFactory.CreateClient();
        var result = await _diagnosticSamples.CheckAsync("qobuz:metadata", ProviderDiagnosticSamples.Recordings("qobuz"),
            async (sample, token) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://www.qobuz.com/api.json/0.2/track/search?query={Uri.EscapeDataString(sample.Query)}&limit=1&app_id={appId}");
                request.Headers.Add("X-App-Id", appId);
                request.Headers.Add("X-User-Auth-Token", userAuthToken);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");
                using var response = await SendWithProbeTimeoutAsync(client, request, token);
                var check = HttpSampleCheck(response);
                if (!check.Success) return check;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                return document.RootElement.TryGetProperty("tracks", out var tracks) &&
                    tracks.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0
                    ? new(true) : ProviderSampleCheck.Missing;
            }, token => DiscoverQueriesAsync("qobuz", token), cancellationToken);
        return new(result.Success, result.ReasonCode, IsNeutral: result.SampleUnavailable);
    }

    private async Task<bool> TestQobuzAccountAsync(
        string? userAuthToken,
        string? userId,
        CancellationToken cancellationToken)
    {
        const string appId = "798273057";
        var apiUrl = $"https://www.qobuz.com/api.json/0.2/favorite/getUserFavorites?user_id={Uri.EscapeDataString(userId!)}&app_id={appId}";

        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Add("X-App-Id", appId);
        request.Headers.Add("X-User-Auth-Token", userAuthToken);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");

        using var response = await SendWithProbeTimeoutAsync(client, request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static string? SecretValue(
        IReadOnlyDictionary<string, string>? values,
        params string[] names)
    {
        if (values == null)
        {
            return null;
        }

        foreach (var name in names)
        {
            var value = values.FirstOrDefault(item =>
                NormalizeSettingKey(item.Key) == NormalizeSettingKey(name) && !string.IsNullOrWhiteSpace(item.Value)).Value;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static async Task<HttpResponseMessage> SendWithProbeTimeoutAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        return await client.SendAsync(request, timeout.Token);
    }

    private List<string> GetMetadataOrder() =>
        GetProviderOrder(ProviderCapabilityKind.Metadata);

    private List<string> GetDownloadOrder() =>
        GetProviderOrder(ProviderCapabilityKind.Download);

    private List<string> GetStreamingOrder() =>
        GetProviderOrder(ProviderCapabilityKind.Streaming);

    private List<string> GetPlaylistOrder() =>
        GetProviderOrder(ProviderCapabilityKind.Playlist);

    private List<string> GetLyricsOrder() => GetProviderOrder(ProviderCapabilityKind.Lyrics);

    private List<string> GetProviderOrder(ProviderCapabilityKind capability)
    {
        var definition = ProviderOrderPolicyCatalog.Find(capability)!;
        var value = _configuration[definition.SettingKey] ??
                    _configuration[definition.BootstrapKey] ??
                    definition.DefaultValue;
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .ToList();
    }

    private HashSet<string> GetEnabledSearchRaw() =>
        GetProviderSet("MULTI_PROVIDER_ENABLED_SEARCH", "apple-download,deezer,qobuz");

    private HashSet<string> GetEnabledPlaylistRaw() =>
        GetProviderSet("MULTI_PROVIDER_ENABLED_PLAYLIST", "spotify");

    private HashSet<string> GetProviderSet(string key, string fallback) =>
        (_configuration[key] ?? fallback)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> GetDisabledProviders() =>
        GetProviderSet("MULTI_PROVIDER_DISABLED_PROVIDERS", string.Empty);

    private static bool BaselineReasonTakesPrecedence(ProviderRuntimeStatus baseline) =>
        !baseline.IsSupported ||
        !baseline.IsEnabled ||
        baseline.Configuration == ProviderConfigurationState.NeedsConfiguration;

    private static bool ReadTrue(JsonElement source, string property) =>
        source.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.True;

    private static bool IsConfiguredValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Trim().StartsWith("your-", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        return normalized is "applemusic" or "apple-music" or "apple_music" or "apple-download"
            ? "apple-musickit"
            : normalized;
    }
}
