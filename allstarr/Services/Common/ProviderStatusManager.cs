using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Models.Settings;
using allstarr.Services.SquidWTF;
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
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly (string Provider, string Capability)[] KnownCapabilities =
    [
        ("spotify", ProviderCapabilities.Playlist),
        ("spotify", ProviderCapabilities.Lyrics),
        ("apple-download", ProviderCapabilities.Metadata),
        ("apple-download", ProviderCapabilities.Streaming),
        ("apple-download", ProviderCapabilities.Download),
        ("deezer", ProviderCapabilities.Metadata),
        ("deezer", ProviderCapabilities.Streaming),
        ("deezer", ProviderCapabilities.Download),
        ("deezer", ProviderCapabilities.Playlist),
        ("qobuz", ProviderCapabilities.Metadata),
        ("qobuz", ProviderCapabilities.Streaming),
        ("qobuz", ProviderCapabilities.Download),
        ("qobuz", ProviderCapabilities.Playlist),
        ("squidwtf", ProviderCapabilities.Metadata),
        ("lyricsplus", ProviderCapabilities.Lyrics),
        ("lrclib", ProviderCapabilities.Lyrics),
        ("lastfm", ProviderCapabilities.Scrobbling),
        ("listenbrainz", ProviderCapabilities.Scrobbling)
    ];

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProviderStatusManager> _logger;
    private readonly SpotifyApiSettings _spotifySettings;
    private readonly AppleDownloadSettings _appleMusicSettings;
    private readonly DeezerSettings _deezerSettings;
    private readonly QobuzSettings _qobuzSettings;
    private readonly SquidWtfEndpointCatalog _squidWtfCatalog;
    private readonly DurableProviderHealthStore? _durableHealth;
    private readonly IAppleDownloadEndpointDiscovery? _appleDownloadDiscovery;
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
        IOptions<SquidWTFSettings> squidWtfSettings,
        SquidWtfEndpointCatalog squidWtfCatalog,
        DurableProviderHealthStore? durableHealth = null,
        IAppleDownloadEndpointDiscovery? appleDownloadDiscovery = null)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _spotifySettings = spotifySettings.Value;
        _appleMusicSettings = appleMusicSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _squidWtfCatalog = squidWtfCatalog;
        _durableHealth = durableHealth;
        _appleDownloadDiscovery = appleDownloadDiscovery;
    }

    public IReadOnlyList<string> GetEnabledSearchProviders()
    {
        var order = GetMetadataOrder();
        var enabled = GetEnabledSearchRaw();

        return order
            .Where(provider =>
                enabled.Contains(provider) &&
                GetStatus(provider, ProviderCapabilities.Metadata).CanAttempt)
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledPlaylistProviders()
    {
        var order = GetPlaylistOrder();
        var enabled = GetEnabledPlaylistRaw();

        return order
            .Where(provider =>
                enabled.Contains(provider) &&
                GetStatus(provider, ProviderCapabilities.Playlist).CanAttempt)
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledDownloadProviders()
    {
        return GetDownloadOrder()
            .Where(provider => GetStatus(provider, ProviderCapabilities.Download).CanAttempt)
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledStreamingProviders()
    {
        return GetStreamingOrder()
            .Where(provider => GetStatus(provider, ProviderCapabilities.Streaming).CanAttempt)
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledLyricsProviders()
    {
        return GetLyricsOrder()
            .Where(provider => GetStatus(provider, ProviderCapabilities.Lyrics).CanAttempt)
            .ToList();
    }

    /// <summary>
    /// Returns the complete current built-in status projection without probing.
    /// </summary>
    public IReadOnlyList<ProviderRuntimeStatus> GetAllStatuses(
        string accountKey = ProviderRuntimeAccounts.LegacyGlobal) =>
        KnownCapabilities
            .Select(item => GetStatus(item.Provider, item.Capability, accountKey))
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
        return KnownCapabilities
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
        var key = ProviderRuntimeStatusKey.Create(
            provider,
            capability,
            providerAccountId.ToString("N"));
        var baseline = ApplyManagedAccountConfiguration(
            BuildBaselineStatus(key),
            accountSecrets);
        return GetStatusCore(key, baseline);
    }

    /// <summary>
    /// Returns a truthful snapshot without starting a probe or inventing a timestamp.
    /// </summary>
    public ProviderRuntimeStatus GetStatus(
        string provider,
        string capability,
        string accountKey = ProviderRuntimeAccounts.LegacyGlobal)
    {
        var key = ProviderRuntimeStatusKey.Create(provider, capability, accountKey);
        return GetStatusCore(key, BuildBaselineStatus(key));
    }

    public bool CanTestCapability(string provider, string capability) =>
        HasProbe(Normalize(provider), Normalize(capability));

    private ProviderRuntimeStatus GetStatusCore(
        ProviderRuntimeStatusKey key,
        ProviderRuntimeStatus baseline)
    {
        if (!_observations.TryGetValue(key, out var observation))
        {
            if (_durableHealth != null &&
                _durableHealth.TryGetLatest(
                    key.Provider,
                    key.AccountKey,
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
    public async Task<ProviderRuntimeStatus> TestProviderCapabilityAsync(
        string provider,
        string capability,
        string accountKey = ProviderRuntimeAccounts.LegacyGlobal,
        CancellationToken cancellationToken = default) =>
        await TestProviderCapabilityCoreAsync(
            provider,
            capability,
            accountKey,
            accountSecrets: null,
            cancellationToken);

    public async Task<ProviderRuntimeStatus> TestManagedProviderCapabilityAsync(
        string provider,
        string capability,
        Guid providerAccountId,
        IReadOnlyDictionary<string, string> accountSecrets,
        CancellationToken cancellationToken = default) =>
        await TestProviderCapabilityCoreAsync(
            provider,
            capability,
            providerAccountId.ToString("N"),
            accountSecrets,
            cancellationToken);

    private async Task<ProviderRuntimeStatus> TestProviderCapabilityCoreAsync(
        string provider,
        string capability,
        string accountKey,
        IReadOnlyDictionary<string, string>? accountSecrets,
        CancellationToken cancellationToken)
    {
        var key = ProviderRuntimeStatusKey.Create(provider, capability, accountKey);
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
            "Testing provider capability {Provider}/{Capability} for account key {AccountKey}",
            key.Provider,
            key.Capability,
            key.AccountKey);

        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var isHealthy = await ProbeCapabilityAsync(
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
            var result = baseline with
            {
                Health = isHealthy ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
                TestedAt = DateTimeOffset.UtcNow,
                ReasonCode = isHealthy ? null : failureReason
            };

            _observations[key] = result;
            await PersistObservationAsync(
                key,
                isHealthy
                    ? allstarr.Core.Storage.ProviderHealthState.Healthy
                    : allstarr.Core.Storage.ProviderHealthState.Degraded,
                (long)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds),
                result.ReasonCode,
                cancellationToken);
            _logger.LogInformation(
                "Provider capability probe result: {Provider}/{Capability} => {Health}",
                key.Provider,
                key.Capability,
                result.Health);
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
        if (_durableHealth?.IsCircuitOpen(key.Provider, key.AccountKey, key.Capability) != true)
        {
            return status;
        }

        return status with
        {
            Health = ProviderHealthState.Degraded,
            ReasonCode = "circuit_open"
        };
    }

    private async Task PersistObservationAsync(
        ProviderRuntimeStatusKey key,
        allstarr.Core.Storage.ProviderHealthState state,
        long? latencyMilliseconds,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        if (_durableHealth == null)
        {
            return;
        }

        try
        {
            await _durableHealth.RecordAsync(
                key.Provider,
                key.AccountKey,
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

    /// <summary>
    /// Legacy provider-wide adapter. It is now a pure read and returns true only
    /// when the compatibility capability has actually passed a probe.
    /// </summary>
    public bool IsProviderHealthy(string provider)
    {
        var normalized = Normalize(provider);
        var capability = GetCompatibilityCapability(normalized);
        return capability != null &&
               GetStatus(normalized, capability).Health == ProviderHealthState.Healthy;
    }

    /// <summary>
    /// Legacy manual-test adapter used by the current admin endpoint.
    /// </summary>
    public async Task<bool> TestProviderConnectionAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(provider);
        var capability = GetCompatibilityCapability(normalized);
        if (capability == null)
        {
            return false;
        }

        var result = await TestProviderCapabilityAsync(
            normalized,
            capability,
            ProviderRuntimeAccounts.LegacyGlobal,
            cancellationToken);
        return result.Health == ProviderHealthState.Healthy;
    }

    public async Task<bool> TestManagedProviderConnectionAsync(
        string provider,
        Guid providerAccountId,
        IReadOnlyDictionary<string, string> accountSecrets,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(provider);
        var capability = GetCompatibilityCapability(normalized);
        if (capability == null)
        {
            return false;
        }

        var result = await TestManagedProviderCapabilityAsync(
            normalized,
            capability,
            providerAccountId,
            accountSecrets,
            cancellationToken);
        return result.Health == ProviderHealthState.Healthy;
    }

    /// <summary>
    /// Legacy cache projection. Only completed compatibility probes are included;
    /// unknown and testing observations have no fabricated test time.
    /// </summary>
    public IReadOnlyDictionary<string, (bool IsHealthy, DateTime TestedAt)> GetStatusCache()
    {
        var results = new Dictionary<string, (bool IsHealthy, DateTime TestedAt)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var provider in new[] { "spotify", "apple-download", "deezer", "qobuz", "squidwtf" })
        {
            var capability = GetCompatibilityCapability(provider)!;
            var status = GetStatus(provider, capability);
            if (status.TestedAt is not { } testedAt ||
                status.Health is ProviderHealthState.Unknown or ProviderHealthState.Testing)
            {
                continue;
            }

            results[provider] = (
                status.Health == ProviderHealthState.Healthy,
                testedAt.UtcDateTime);
        }

        return results;
    }

    private ProviderRuntimeStatus BuildBaselineStatus(ProviderRuntimeStatusKey key)
    {
        var isSupported = IsCapabilitySupported(key.Provider, key.Capability);
        var isEnabled = isSupported && !GetDisabledProviders().Contains(key.Provider);
        var (configuration, reasonCode) = GetConfigurationState(key.Provider, key.Capability);

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
            AccountKey = key.AccountKey,
            IsSupported = isSupported,
            IsEnabled = isEnabled,
            Configuration = configuration,
            Health = ProviderHealthState.Unknown,
            TestedAt = null,
            ReasonCode = reasonCode
        };
    }

    private (ProviderConfigurationState State, string? ReasonCode) GetConfigurationState(
        string provider,
        string capability)
    {
        return (provider, capability) switch
        {
            ("apple-download", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download) =>
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

            ("squidwtf", ProviderCapabilities.Metadata) =>
                _squidWtfCatalog.ApiUrls.Count > 0
                    ? (ProviderConfigurationState.NotRequired, null)
                    : (ProviderConfigurationState.NeedsConfiguration, "no_metadata_endpoint"),

            ("spotify", ProviderCapabilities.Playlist) =>
                _spotifySettings.Enabled && IsConfiguredValue(_spotifySettings.SessionCookie)
                    ? (ProviderConfigurationState.Configured, null)
                    : (ProviderConfigurationState.NeedsConfiguration, "missing_spotify_session"),
            ("spotify", ProviderCapabilities.Lyrics) =>
                _spotifySettings.Enabled &&
                IsConfiguredValue(_spotifySettings.SessionCookie) &&
                IsConfiguredValue(_spotifySettings.LyricsApiUrl)
                    ? (ProviderConfigurationState.Configured, null)
                    : (ProviderConfigurationState.NeedsConfiguration, "missing_spotify_lyrics_configuration"),

            ("lyricsplus", ProviderCapabilities.Lyrics) or
            ("lrclib", ProviderCapabilities.Lyrics) =>
                (ProviderConfigurationState.NotRequired, null),

            _ => (ProviderConfigurationState.NeedsConfiguration, "unsupported_capability")
        };
    }

    private ProviderRuntimeStatus ApplyManagedAccountConfiguration(
        ProviderRuntimeStatus baseline,
        IReadOnlyDictionary<string, string> secrets)
    {
        bool? configured = (baseline.Provider, baseline.Capability) switch
        {
            ("spotify", ProviderCapabilities.Playlist) =>
                IsConfiguredValue(SecretValue(secrets, "sessioncookie", "spdc", "cookie")),
            ("spotify", ProviderCapabilities.Lyrics) =>
                IsConfiguredValue(SecretValue(secrets, "sessioncookie", "spdc", "cookie")) &&
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
        return (provider, capability) switch
        {
            ("apple-download", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download) =>
                _appleDownloadSnapshot?.Capability(capability).State != AppleDownloadCapabilityState.Unsupported,
            ("deezer", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Playlist) => true,
            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download or ProviderCapabilities.Playlist) => true,
            ("squidwtf", ProviderCapabilities.Metadata) => true,
            ("spotify", ProviderCapabilities.Playlist or ProviderCapabilities.Lyrics) => true,
            ("lyricsplus", ProviderCapabilities.Lyrics) => true,
            ("lrclib", ProviderCapabilities.Lyrics) => true,
            ("lastfm", ProviderCapabilities.Scrobbling) => true,
            ("listenbrainz", ProviderCapabilities.Scrobbling) => true,
            _ => false
        };
    }

    private static bool HasProbe(string provider, string capability)
    {
        return (provider, capability) switch
        {
            ("apple-download", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download) => true,
            ("deezer", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist or ProviderCapabilities.Streaming or ProviderCapabilities.Download) => true,
            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist or ProviderCapabilities.Streaming or ProviderCapabilities.Download) => true,
            ("squidwtf", ProviderCapabilities.Metadata) => true,
            ("spotify", ProviderCapabilities.Playlist) => true,
            ("lyricsplus", ProviderCapabilities.Lyrics) => true,
            ("lrclib", ProviderCapabilities.Lyrics) => true,
            ("lastfm", ProviderCapabilities.Scrobbling) => true,
            ("listenbrainz", ProviderCapabilities.Scrobbling) => true,
            _ => false
        };
    }

    private async Task<bool> ProbeCapabilityAsync(
        string provider,
        string capability,
        IReadOnlyDictionary<string, string>? accountSecrets,
        CancellationToken cancellationToken)
    {
        return (provider, capability) switch
        {
            ("spotify", ProviderCapabilities.Playlist) => await TestSpotifyPlaylistAsync(
                SecretValue(accountSecrets, "sessioncookie", "spdc", "cookie") ?? _spotifySettings.SessionCookie,
                cancellationToken),
            ("apple-download", ProviderCapabilities.Metadata or ProviderCapabilities.Streaming or ProviderCapabilities.Download) => await TestAppleDownloadAsync(capability, cancellationToken),
            ("deezer", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) => await TestDeezerMetadataAsync(cancellationToken),
            ("deezer", ProviderCapabilities.Streaming or ProviderCapabilities.Download) => await TestDeezerAccountAsync(
                SecretValue(accountSecrets, "arl") ?? _deezerSettings.Arl,
                cancellationToken),
            ("qobuz", ProviderCapabilities.Metadata or ProviderCapabilities.Playlist) => await TestQobuzMetadataAsync(cancellationToken),
            ("qobuz", ProviderCapabilities.Streaming or ProviderCapabilities.Download) => await TestQobuzAccountAsync(
                SecretValue(accountSecrets, "userauthtoken", "token") ?? _qobuzSettings.UserAuthToken,
                SecretValue(accountSecrets, "userid") ?? _qobuzSettings.UserId,
                cancellationToken),
            ("squidwtf", ProviderCapabilities.Metadata) => await TestSquidWtfAsync(cancellationToken),
            ("lyricsplus", ProviderCapabilities.Lyrics) => await TestLyricsPlusAsync(cancellationToken),
            ("lrclib", ProviderCapabilities.Lyrics) => await TestLrclibAsync(cancellationToken),
            ("lastfm", ProviderCapabilities.Scrobbling) => await TestLastFmAsync(accountSecrets, cancellationToken),
            ("listenbrainz", ProviderCapabilities.Scrobbling) => await TestListenBrainzAsync(accountSecrets, cancellationToken),
            _ => false
        };
    }

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

        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.listenbrainz.org/1/validate-token");
        request.Headers.Authorization = new("Token", token);
        using var response = await SendWithProbeTimeoutAsync(client, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.True;
    }

    private async Task<bool> TestLyricsPlusAsync(CancellationToken cancellationToken)
    {
        const string url = "https://lyricsplus.prjktla.workers.dev/v2/lyrics/get?title=Never%20Gonna%20Give%20You%20Up&artist=Rick%20Astley&album=Whenever%20You%20Need%20Somebody&duration=213";
        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendWithProbeTimeoutAsync(client, request, cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode is
            System.Net.HttpStatusCode.NotFound or
            System.Net.HttpStatusCode.TooManyRequests;
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

    private async Task<bool> TestSpotifyPlaylistAsync(
        string? sessionCookie,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://open.spotify.com/get_access_token?reason=transport&productType=web_player");
        request.Headers.Add("Cookie", $"sp_dc={sessionCookie}");
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        using var response = await SendWithProbeTimeoutAsync(client, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("accessToken", out var token) &&
               !string.IsNullOrWhiteSpace(token.GetString());
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

    private async Task<bool> TestSquidWtfAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        foreach (var url in _squidWtfCatalog.ApiUrls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await SendWithProbeTimeoutAsync(client, request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try the next discovered metadata endpoint.
            }
        }

        return false;
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
        GetProviderOrder("MULTI_PROVIDER_METADATA_ORDER", "deezer,qobuz,squidwtf");

    private List<string> GetDownloadOrder() =>
        GetProviderOrder("MULTI_PROVIDER_DOWNLOAD_ORDER", "deezer,qobuz")
            .Where(provider => provider != "squidwtf")
            .ToList();

    private List<string> GetStreamingOrder() =>
        GetProviderOrder("MULTI_PROVIDER_STREAMING_ORDER", "deezer,qobuz")
            .Where(provider => provider != "squidwtf")
            .ToList();

    private List<string> GetPlaylistOrder() =>
        GetProviderOrder("MULTI_PROVIDER_PLAYLIST_ORDER", "spotify,deezer,qobuz")
            .Where(provider => provider != "squidwtf")
            .ToList();

    private List<string> GetLyricsOrder() =>
        GetProviderOrder("MULTI_PROVIDER_LYRICS_ORDER", "spotify,lyricsplus,lrclib");

    private List<string> GetProviderOrder(string key, string fallback)
    {
        var value = _configuration[key] ?? fallback;
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .ToList();
    }

    private HashSet<string> GetEnabledSearchRaw() =>
        GetProviderSet("MULTI_PROVIDER_ENABLED_SEARCH", "deezer,qobuz,squidwtf");

    private HashSet<string> GetEnabledPlaylistRaw() =>
        GetProviderSet("MULTI_PROVIDER_ENABLED_PLAYLIST", "spotify");

    private HashSet<string> GetProviderSet(string key, string fallback) =>
        GetProviderOrder(key, fallback).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> GetDisabledProviders() =>
        GetProviderSet("MULTI_PROVIDER_DISABLED_PROVIDERS", string.Empty);

    private static string? GetCompatibilityCapability(string provider) => provider switch
    {
        "spotify" => ProviderCapabilities.Playlist,
        "apple-download" => ProviderCapabilities.Download,
        "deezer" => ProviderCapabilities.Download,
        "qobuz" => ProviderCapabilities.Download,
        "squidwtf" => ProviderCapabilities.Metadata,
        "lastfm" or "listenbrainz" => ProviderCapabilities.Scrobbling,
        _ => null
    };

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
