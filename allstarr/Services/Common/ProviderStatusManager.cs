using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Models.Settings;
using allstarr.Core.Capabilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using allstarr.Core.Health;
using allstarr.Services.AppleMusic;

namespace allstarr.Services.Common;

/// <summary>
/// Current status for built-in providers. Reads are side-effect free. Explicit
/// probes are isolated by managed account and capability, then recorded in the
/// durable health store when durable storage is ready.
/// </summary>
public class ProviderStatusManager
{
    private readonly record struct ProbeOutcome(
        bool Success,
        string? ReasonCode = null,
        bool MeasuresLatency = true);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ObservationLifetime = TimeSpan.FromMinutes(20);
    private const string SpotifyLyricsTestTrackId = "3yII7UwgLF6K5zW3xad3MP";
    private static readonly (string Provider, string Capability, ProviderAccountRequirement AccountRequirement)[] KnownCapabilities =
    [
        ("spotify", ProviderCapabilities.Playlist, ProviderAccountRequirement.Required),
        ("spotify", ProviderCapabilities.Lyrics, ProviderAccountRequirement.None),
        ("apple-download", ProviderCapabilities.Metadata, ProviderAccountRequirement.None),
        ("apple-download", ProviderCapabilities.Streaming, ProviderAccountRequirement.None),
        ("apple-download", ProviderCapabilities.Download, ProviderAccountRequirement.None),
        ("apple-download", ProviderCapabilities.Lyrics, ProviderAccountRequirement.None),
        ("deezer", ProviderCapabilities.Metadata, ProviderAccountRequirement.None),
        ("deezer", ProviderCapabilities.Streaming, ProviderAccountRequirement.Required),
        ("deezer", ProviderCapabilities.Download, ProviderAccountRequirement.Required),
        ("deezer", ProviderCapabilities.Playlist, ProviderAccountRequirement.Required),
        ("qobuz", ProviderCapabilities.Metadata, ProviderAccountRequirement.Optional),
        ("qobuz", ProviderCapabilities.Streaming, ProviderAccountRequirement.Required),
        ("qobuz", ProviderCapabilities.Download, ProviderAccountRequirement.Required),
        ("qobuz", ProviderCapabilities.Playlist, ProviderAccountRequirement.Required),
        ("lrclib", ProviderCapabilities.Lyrics, ProviderAccountRequirement.None),
        ("lastfm", ProviderCapabilities.Scrobbling, ProviderAccountRequirement.Required),
        ("listenbrainz", ProviderCapabilities.Scrobbling, ProviderAccountRequirement.Required)
    ];

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProviderStatusManager> _logger;
    private readonly SpotifyApiSettings _spotifySettings;
    private readonly AppleDownloadSettings _appleMusicSettings;
    private readonly DeezerSettings _deezerSettings;
    private readonly QobuzSettings _qobuzSettings;
    private readonly ExtensionManager? _extensionManager;
    private readonly DurableProviderHealthStore? _durableHealth;
    private readonly IAppleDownloadEndpointDiscovery? _appleDownloadDiscovery;
    private readonly IServiceProvider? _services;
    private AppleDownloadEndpointSnapshot? _appleDownloadSnapshot;

    private readonly ConcurrentDictionary<ProviderRuntimeStatusKey, ProviderRuntimeStatus> _observations = new();

    public ProviderStatusManager(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<ProviderStatusManager> logger,
        IOptions<SpotifyApiSettings> spotifySettings,
        IOptions<AppleDownloadSettings> appleMusicSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings,
        ExtensionManager? extensionManager = null,
        DurableProviderHealthStore? durableHealth = null,
        IAppleDownloadEndpointDiscovery? appleDownloadDiscovery = null,
        IServiceProvider? services = null)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _spotifySettings = spotifySettings.Value;
        _appleMusicSettings = appleMusicSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _extensionManager = extensionManager;
        _durableHealth = durableHealth;
        _appleDownloadDiscovery = appleDownloadDiscovery;
        _services = services;
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

    /// <summary>
    /// Returns the capabilities for one managed provider account. Credential
    /// configuration is evaluated from that account's decrypted, in-memory view;
    /// it never falls back to another account's deployment-global credential.
    /// </summary>
    public IReadOnlyList<ProviderRuntimeStatus> GetAllManagedStatuses(
        string provider,
        Guid providerAccountId,
        IReadOnlyDictionary<string, string> accountSecrets)
    {
        var normalizedProvider = Normalize(provider);
        return RuntimeCapabilities()
            .Where(item => item.Provider == normalizedProvider)
            .Select(item => GetManagedStatus(
                item.Provider,
                item.Capability,
                providerAccountId,
                accountSecrets))
            .ToList();
    }

    public ProviderRuntimeStatus GetManagedStatus(
        string provider,
        string capability,
        Guid providerAccountId,
        IReadOnlyDictionary<string, string> accountSecrets)
    {
        var key = ProviderRuntimeStatusKey.CreateManaged(
            provider,
            capability,
            providerAccountId);
        var baseline = ApplyManagedAccountConfiguration(
            BuildBaselineStatus(key),
            accountSecrets);
        return GetStatusCore(key, baseline);
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

    public bool CanTestCapability(string provider, string capability) =>
        HasProbe(Normalize(provider), Normalize(capability));

    public bool CanTestAccountFreeCapability(string provider, string capability) =>
        CanRunWithoutAccount(provider, capability) && CanTestCapability(provider, capability);

    private ProviderRuntimeStatus GetStatusCore(
        ProviderRuntimeStatusKey key,
        ProviderRuntimeStatus baseline)
    {
        PruneExpiredObservations();
        if (!_observations.TryGetValue(key, out var observation))
        {
            if (key.ProviderAccountId.HasValue &&
                _durableHealth != null &&
                _durableHealth.TryGetLatest(
                    key.Provider,
                    key.ProviderAccountId.Value,
                    key.Capability,
                    out var durable))
            {
                observation = baseline with
                {
                    Health = durable.State switch
                    {
                        allstarr.Core.Storage.ProviderHealthState.Healthy => ProviderHealthState.Healthy,
                        allstarr.Core.Storage.ProviderHealthState.Unknown => ProviderHealthState.Unknown,
                        _ => ProviderHealthState.Degraded
                    },
                    TestedAt = durable.ObservedAt,
                    LatencyMilliseconds = durable.LatencyMilliseconds,
                    ReasonCode = durable.FailureCode
                };
            }
            else
            {
                return ApplyCircuitState(baseline, key);
            }
        }

        return ApplyCircuitState(observation with
        {
            IsSupported = baseline.IsSupported,
            IsEnabled = baseline.IsEnabled,
            Configuration = baseline.Configuration,
            ReasonCode = BaselineReasonTakesPrecedence(baseline)
                ? baseline.ReasonCode
                : observation.ReasonCode
        }, key);
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
            cancellationToken);
    }

    public async Task<ProviderRuntimeStatus> TestManagedProviderCapabilityAsync(
        string provider,
        string capability,
        Guid providerAccountId,
        IReadOnlyDictionary<string, string> accountSecrets,
        CancellationToken cancellationToken = default) =>
        await TestProviderCapabilityCoreAsync(
            provider,
            capability,
            providerAccountId,
            accountSecrets,
            cancellationToken);

    private async Task<ProviderRuntimeStatus> TestProviderCapabilityCoreAsync(
        string provider,
        string capability,
        Guid? providerAccountId,
        IReadOnlyDictionary<string, string>? accountSecrets,
        CancellationToken cancellationToken)
    {
        PruneExpiredObservations();
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

        var hadPrevious = _observations.TryGetValue(key, out var previous);
        _observations[key] = baseline with
        {
            Health = ProviderHealthState.Testing,
            TestedAt = null,
            ReasonCode = null
        };

        _logger.LogDebug(
            "Testing provider capability {Provider}/{Capability} for provider account {ProviderAccountId}",
            key.Provider,
            key.Capability,
            key.ProviderAccountId?.ToString("N") ?? "account-free");

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var probe = await ProbeCapabilityAsync(
                key.Provider,
                key.Capability,
                accountSecrets,
                cancellationToken);
            if (key.Provider == "apple-download")
            {
                baseline = BuildBaselineStatus(key);
            }
            var failureReason = key.Provider == "apple-download" && _appleDownloadSnapshot != null
                ? _appleDownloadSnapshot.Capability(key.Capability).ReasonCode ?? _appleDownloadSnapshot.ReasonCode
                : "probe_failed";
            var latencyMilliseconds = probe.MeasuresLatency
                ? (long?)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds)
                : null;
            var result = baseline with
            {
                Health = probe.Success ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
                TestedAt = DateTimeOffset.UtcNow,
                LatencyMilliseconds = latencyMilliseconds,
                ReasonCode = probe.Success ? null : probe.ReasonCode ?? failureReason
            };

            _observations[key] = result;
            await PersistObservationAsync(
                key,
                probe.Success
                    ? allstarr.Core.Storage.ProviderHealthState.Healthy
                    : allstarr.Core.Storage.ProviderHealthState.Degraded,
                latencyMilliseconds,
                result.ReasonCode,
                cancellationToken);
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
            RestorePreviousObservation(key, hadPrevious, previous);
            throw;
        }
        catch (Exception ex)
        {
            var result = baseline with
            {
                Health = ProviderHealthState.Degraded,
                TestedAt = DateTimeOffset.UtcNow,
                LatencyMilliseconds = (long)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds),
                ReasonCode = ex is OperationCanceledException ? "timeout" : "unreachable"
            };
            _observations[key] = result;
            await PersistObservationAsync(
                key,
                allstarr.Core.Storage.ProviderHealthState.Unavailable,
                null,
                result.ReasonCode,
                cancellationToken);

            // Keep provider response bodies, URLs, and credentials out of status logs.
            _logger.LogWarning(
                "Provider capability probe failed: {Provider}/{Capability} ({ExceptionType})",
                key.Provider,
                key.Capability,
                ex.GetType().Name);
            return result;
        }
    }

    private ProviderRuntimeStatus ApplyCircuitState(
        ProviderRuntimeStatus status,
        ProviderRuntimeStatusKey key)
    {
        if (!key.ProviderAccountId.HasValue ||
            _durableHealth?.IsCircuitOpen(key.ProviderAccountId.Value, key.Capability) != true)
        {
            return status;
        }

        return status with
        {
            Health = ProviderHealthState.Degraded,
            ReasonCode = "circuit_open"
        };
    }

    private void PruneExpiredObservations()
    {
        var cutoff = DateTimeOffset.UtcNow - ObservationLifetime;
        foreach (var item in _observations)
        {
            if (item.Value.TestedAt.HasValue &&
                item.Value.TestedAt.Value <= cutoff)
            {
                _observations.TryRemove(item.Key, out _);
            }
        }
    }

    private async Task PersistObservationAsync(
        ProviderRuntimeStatusKey key,
        allstarr.Core.Storage.ProviderHealthState state,
        long? latencyMilliseconds,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        if (_durableHealth == null || !key.ProviderAccountId.HasValue)
        {
            return;
        }

        try
        {
            await _durableHealth.RecordAsync(
                key.Provider,
                key.ProviderAccountId.Value,
                key.Capability,
                state,
                latencyMilliseconds,
                failureCode,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not persist provider health observation for {Provider}/{Capability} ({ExceptionType})",
                key.Provider,
                key.Capability,
                ex.GetType().Name);
        }
    }

    public async Task<bool> TestManagedProviderConnectionAsync(
        string provider,
        Guid providerAccountId,
        IReadOnlyDictionary<string, string> accountSecrets,
        CancellationToken cancellationToken = default)
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
            var baseline = GetManagedStatus(normalized, capability, providerAccountId, accountSecrets);
            if (!baseline.IsSupported || !baseline.IsEnabled ||
                baseline.Configuration == ProviderConfigurationState.NeedsConfiguration)
            {
                continue;
            }

            attempted = true;
            var result = await TestManagedProviderCapabilityAsync(
                normalized,
                capability,
                providerAccountId,
                accountSecrets,
                cancellationToken);
            healthy &= result.Health == ProviderHealthState.Healthy;
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
            ("apple-download", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Lyrics) =>
                IsConfiguredValue(_appleMusicSettings.BaseUrl)
                    ? (ProviderConfigurationState.Configured, null)
                    : (ProviderConfigurationState.NeedsConfiguration, "missing_sidecar_url"),

            ("deezer", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) =>
                (ProviderConfigurationState.NotRequired, null),
            ("deezer", ProviderCapabilities.Streaming or ProviderCapabilities.Download) =>
                IsConfiguredValue(_deezerSettings.Arl)
                    ? (ProviderConfigurationState.Configured, null)
                    : (ProviderConfigurationState.NeedsConfiguration, "missing_deezer_arl"),

            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) =>
                (ProviderConfigurationState.NotRequired, null),
            ("qobuz", ProviderCapabilities.Streaming or ProviderCapabilities.Download) =>
                IsConfiguredValue(_qobuzSettings.UserAuthToken) && IsConfiguredValue(_qobuzSettings.UserId)
                    ? (ProviderConfigurationState.Configured, null)
                    : (ProviderConfigurationState.NeedsConfiguration, "missing_qobuz_account"),

            ("spotify", ProviderCapabilities.Playlist) =>
                _spotifySettings.Enabled && IsConfiguredValue(_spotifySettings.SessionCookie)
                    ? (ProviderConfigurationState.Configured, null)
                    : (ProviderConfigurationState.NeedsConfiguration, "missing_spotify_session"),
            ("spotify", ProviderCapabilities.Lyrics) =>
                IsConfiguredValue(_spotifySettings.LyricsApiUrl)
                    ? (ProviderConfigurationState.Configured, null)
                    : (ProviderConfigurationState.NeedsConfiguration, "missing_spotify_lyrics_configuration"),

            ("lrclib", ProviderCapabilities.Lyrics) =>
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
            ("spotify", ProviderCapabilities.Playlist) =>
                IsConfiguredValue(SecretValue(secrets, "sessioncookie", "spdc", "cookie")),
            ("spotify", ProviderCapabilities.Lyrics) =>
                IsConfiguredValue(_spotifySettings.LyricsApiUrl),
            ("deezer", ProviderCapabilities.Streaming or ProviderCapabilities.Download) =>
                IsConfiguredValue(SecretValue(secrets, "arl")),
            ("qobuz", ProviderCapabilities.Streaming or ProviderCapabilities.Download) =>
                IsConfiguredValue(SecretValue(secrets, "userauthtoken", "token")) &&
                IsConfiguredValue(SecretValue(secrets, "userid")),
            ("lastfm", ProviderCapabilities.Scrobbling) =>
                IsConfiguredValue(SecretValue(secrets, "apikey")) &&
                IsConfiguredValue(SecretValue(secrets, "sharedsecret")) &&
                IsConfiguredValue(SecretValue(secrets, "sessionkey")),
            ("listenbrainz", ProviderCapabilities.Scrobbling) =>
                IsConfiguredValue(SecretValue(secrets, "token", "usertoken")),
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

    private bool IsCapabilitySupported(string provider, string capability)
    {
        if (TryGetExtensionCapability(provider, capability, out var extensionCapability))
            return extensionCapability!.HasUsableImplementation;
        return (provider, capability) switch
        {
            ("apple-download", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Lyrics) =>
                _appleDownloadSnapshot?.Capability(capability).State != AppleDownloadCapabilityState.Unsupported,
            ("deezer", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Playlist) => true,
            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Playlist) => true,
            ("spotify", ProviderCapabilities.Playlist or ProviderCapabilities.Lyrics) => true,
            ("lrclib", ProviderCapabilities.Lyrics) => true,
            ("lastfm", ProviderCapabilities.Scrobbling) => true,
            ("listenbrainz", ProviderCapabilities.Scrobbling) => true,
            _ => false
        };
    }

    private bool HasProbe(string provider, string capability)
    {
        if (TryGetExtensionCapability(provider, capability, out var extensionCapability))
            return extensionCapability!.HasUsableImplementation;
        return (provider, capability) switch
        {
            ("apple-download", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Lyrics) => true,
            ("deezer", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist or ProviderCapabilities.Streaming or ProviderCapabilities.Download) => true,
            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist or ProviderCapabilities.Streaming or ProviderCapabilities.Download) => true,
            ("spotify", ProviderCapabilities.Playlist or ProviderCapabilities.Lyrics) => true,
            ("lrclib", ProviderCapabilities.Lyrics) => true,
            ("lastfm", ProviderCapabilities.Scrobbling) => true,
            ("listenbrainz", ProviderCapabilities.Scrobbling) => true,
            _ => false
        };
    }

    private async Task<ProbeOutcome> ProbeCapabilityAsync(
        string provider,
        string capability,
        IReadOnlyDictionary<string, string>? accountSecrets,
        CancellationToken cancellationToken)
    {
        if (TryGetExtensionCapability(provider, capability, out var extensionCapability))
            return new ProbeOutcome(
                extensionCapability!.HasUsableImplementation,
                MeasuresLatency: false);
        return (provider, capability) switch
        {
            ("spotify", ProviderCapabilities.Playlist) => await TestSpotifyPlaylistAsync(
                SecretValue(accountSecrets, "sessioncookie", "spdc", "cookie") ?? _spotifySettings.SessionCookie,
                cancellationToken),
            ("spotify", ProviderCapabilities.Lyrics) => await AsOutcome(TestSpotifyLyricsAsync(cancellationToken)),
            ("apple-download", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Lyrics) => await AsOutcome(TestAppleDownloadAsync(capability, cancellationToken)),
            ("deezer", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) => await AsOutcome(TestDeezerMetadataAsync(cancellationToken)),
            ("deezer", ProviderCapabilities.Streaming or ProviderCapabilities.Download) => await AsOutcome(TestDeezerAccountAsync(
                SecretValue(accountSecrets, "arl") ?? _deezerSettings.Arl,
                cancellationToken)),
            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) => await AsOutcome(TestQobuzMetadataAsync(cancellationToken)),
            ("qobuz", ProviderCapabilities.Streaming or ProviderCapabilities.Download) => await AsOutcome(TestQobuzAccountAsync(
                SecretValue(accountSecrets, "userauthtoken", "token") ?? _qobuzSettings.UserAuthToken,
                SecretValue(accountSecrets, "userid") ?? _qobuzSettings.UserId,
                cancellationToken)),
            ("lrclib", ProviderCapabilities.Lyrics) => await AsOutcome(TestLrclibAsync(cancellationToken)),
            ("lastfm", ProviderCapabilities.Scrobbling) => await AsOutcome(TestLastFmAsync(accountSecrets, cancellationToken)),
            ("listenbrainz", ProviderCapabilities.Scrobbling) => await AsOutcome(TestListenBrainzAsync(accountSecrets, cancellationToken)),
            _ => new ProbeOutcome(false, "probe_not_available")
        };
    }

    private static async Task<ProbeOutcome> AsOutcome(Task<bool> probe) => new(await probe);

    private IReadOnlyList<(string Provider, string Capability, ProviderAccountRequirement AccountRequirement)> RuntimeCapabilities()
    {
        var extensions = ProviderRegistry?.Providers
            .Where(provider => provider.Origin == ProviderOrigin.Extension)
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
            providerDescriptor!.Origin != ProviderOrigin.Extension ||
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

    private async Task<bool> TestListenBrainzAsync(
        IReadOnlyDictionary<string, string>? secrets,
        CancellationToken cancellationToken)
    {
        var token = SecretValue(secrets, "token", "usertoken");
        if (!IsConfiguredValue(token))
        {
            return false;
        }

        var baseUri = ListenBrainzServiceEndpoint.FromSecret(secrets);
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

    private async Task<bool> TestLrclibAsync(CancellationToken cancellationToken)
    {
        const string url = "https://lrclib.net/api/get?artist_name=Rick%20Astley&track_name=Never%20Gonna%20Give%20You%20Up&album_name=Whenever%20You%20Need%20Somebody&duration=213";
        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendWithProbeTimeoutAsync(client, request, cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode is
            System.Net.HttpStatusCode.NotFound or
            System.Net.HttpStatusCode.TooManyRequests;
    }

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

    private async Task<bool> TestSpotifyLyricsAsync(CancellationToken cancellationToken)
    {
        if (!IsConfiguredValue(_spotifySettings.LyricsApiUrl))
        {
            return false;
        }

        try
        {
            var url = $"{_spotifySettings.LyricsApiUrl!.TrimEnd('/')}/?trackid={SpotifyLyricsTestTrackId}&format=id3";
            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendWithProbeTimeoutAsync(client, request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return !(document.RootElement.TryGetProperty("error", out var error) &&
                     error.ValueKind == JsonValueKind.True);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TestAppleDownloadAsync(string capability, CancellationToken cancellationToken)
    {
        if (_appleDownloadDiscovery == null) return false;
        _appleDownloadSnapshot = await _appleDownloadDiscovery.DiscoverAsync(cancellationToken);
        return _appleDownloadSnapshot.State == AppleDownloadEndpointState.Available &&
               _appleDownloadSnapshot.Capability(capability).State == AppleDownloadCapabilityState.Available;
    }

    private async Task<bool> TestDeezerMetadataAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deezer.com/track/3135556");
        using var response = await SendWithProbeTimeoutAsync(client, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("id", out _);
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

    private async Task<bool> TestQobuzMetadataAsync(CancellationToken cancellationToken)
    {
        const string appId = "798273057";
        const string apiUrl = "https://www.qobuz.com/api.json/0.2/track/search?query=test&limit=1&app_id=" + appId;

        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Add("X-App-Id", appId);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");
        using var response = await SendWithProbeTimeoutAsync(client, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("tracks", out _);
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
            if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
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
        GetProviderOrder("MULTI_PROVIDER_METADATA_ORDER", "apple-download,deezer,qobuz");

    private List<string> GetDownloadOrder() =>
        GetProviderOrder("MULTI_PROVIDER_DOWNLOAD_ORDER", "apple-download,deezer,qobuz");

    private List<string> GetStreamingOrder() =>
        GetProviderOrder("MULTI_PROVIDER_STREAMING_ORDER", "apple-download,deezer,qobuz");

    private List<string> GetPlaylistOrder() =>
        GetProviderOrder("MULTI_PROVIDER_PLAYLIST_ORDER", "spotify,deezer,qobuz");

    private List<string> GetLyricsOrder() =>
        GetProviderOrder("MULTI_PROVIDER_LYRICS_ORDER", "spotify,apple-download,lrclib")
            .Where(provider => provider != "lyricsplus")
            .ToList();

    private List<string> GetProviderOrder(string key, string fallback)
    {
        var value = _configuration[key] ?? fallback;
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(provider => provider != "squidwtf")
            .ToList();
    }

    private HashSet<string> GetEnabledSearchRaw() =>
        GetProviderSet("MULTI_PROVIDER_ENABLED_SEARCH", "apple-download,deezer,qobuz");

    private HashSet<string> GetEnabledPlaylistRaw() =>
        GetProviderSet("MULTI_PROVIDER_ENABLED_PLAYLIST", "spotify");

    private HashSet<string> GetProviderSet(string key, string fallback) =>
        GetProviderOrder(key, fallback).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> GetDisabledProviders() =>
        GetProviderSet("MULTI_PROVIDER_DISABLED_PROVIDERS", string.Empty);

    private static bool BaselineReasonTakesPrecedence(ProviderRuntimeStatus baseline) =>
        !baseline.IsSupported ||
        !baseline.IsEnabled ||
        baseline.Configuration == ProviderConfigurationState.NeedsConfiguration;

    private void RestorePreviousObservation(
        ProviderRuntimeStatusKey key,
        bool hadPrevious,
        ProviderRuntimeStatus? previous)
    {
        if (hadPrevious && previous != null)
        {
            _observations[key] = previous;
        }
        else
        {
            _observations.TryRemove(key, out _);
        }
    }

    private static bool ReadTrue(JsonElement source, string property) =>
        source.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.True;

    private static bool IsConfiguredValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Trim().StartsWith("your-", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        return normalized is "applemusic" or "apple-music" or "apple_music"
            ? "apple-download"
            : normalized;
    }
}
