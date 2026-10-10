using allstarr.Core.Health;
using allstarr.Services.AppleMusic;
using System.Net;
using System.Text.Json;

namespace allstarr.Core.Operations;

public enum SidecarRuntimeState
{
    Unknown,
    ProbeDisabled,
    NotInstalled,
    Unreachable,
    NeedsConfiguration,
    Unauthorized,
    Incompatible,
    Degraded,
    Ready
}

public sealed class SidecarProbeTarget
{
    public string Id { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public string HealthPath { get; set; } = "/health";
    public bool Required { get; set; }
    public bool ProbeEnabled { get; set; } = true;
    public string? ExpectedApiVersion { get; set; }
    public bool RequireAuthenticated { get; set; }
}

public sealed class SidecarHealthOptions
{
    public const string SectionName = "Sidecars";

    public int ProbeIntervalSeconds { get; set; } = 30;
    public int ProbeTimeoutSeconds { get; set; } = 5;
    public List<SidecarProbeTarget> Targets { get; set; } = [];

    public bool ClampProbePolicy()
    {
        var interval = Math.Clamp(ProbeIntervalSeconds, 5, 30);
        var timeout = Math.Clamp(ProbeTimeoutSeconds, 1, 5);
        var changed = interval != ProbeIntervalSeconds || timeout != ProbeTimeoutSeconds;
        ProbeIntervalSeconds = interval;
        ProbeTimeoutSeconds = timeout;
        return changed;
    }

    public void Validate()
    {
        if (Targets.Count > 256 || Targets.Any(target =>
                string.IsNullOrWhiteSpace(target.Id) ||
                string.IsNullOrWhiteSpace(target.ProviderId)))
        {
            throw new InvalidOperationException("Sidecar probe targets are invalid or exceed safe limits.");
        }
    }
}

public sealed record SidecarStatus(
    string Id,
    string ProviderId,
    SidecarRuntimeState State,
    bool Required,
    string? ErrorCode,
    DateTimeOffset? CheckedAt);

public sealed class SidecarHealthMonitor : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SidecarHealthOptions _options;
    private readonly ProviderRuntimeHealth _catalog;
    private readonly ILogger<SidecarHealthMonitor> _logger;
    private readonly IPlatformClock _clock;
    private readonly IAppleDownloadEndpointDiscovery? _appleDiscovery;

    public SidecarHealthMonitor(
        IHttpClientFactory httpClientFactory,
        SidecarHealthOptions options,
        ProviderRuntimeHealth catalog,
        ILogger<SidecarHealthMonitor> logger,
        IPlatformClock? clock = null,
        IAppleDownloadEndpointDiscovery? appleDiscovery = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _catalog = catalog;
        _logger = logger;
        _clock = clock ?? new SystemPlatformClock();
        if (_options.ClampProbePolicy())
        {
            _logger.LogWarning(
                "Sidecar probe timing was outside the supported range; using a {Interval}s interval and {Timeout}s timeout",
                _options.ProbeIntervalSeconds,
                _options.ProbeTimeoutSeconds);
        }

        _options.Validate();
        _appleDiscovery = appleDiscovery;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeAllOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Sidecar health cycle failed ({ExceptionType}); the monitor will retry",
                    ex.GetType().Name);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.ProbeIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public async Task ProbeAllOnceAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(ProbeTargetsAsync(_options.Targets, cancellationToken), ProbeAppleAsync(cancellationToken));
    }

    private async Task ProbeAppleAsync(CancellationToken cancellationToken)
    {
        if (_appleDiscovery == null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            _catalog.AppleDownloadSnapshot = await _appleDiscovery.DiscoverAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _catalog.AppleDownloadSnapshot = new AppleDownloadEndpointSnapshot(
                AppleDownloadEndpointState.Unreachable, "endpoint_unreachable", null, false, []);
        }
    }

    private async Task ProbeTargetsAsync(
        IEnumerable<SidecarProbeTarget> targets,
        CancellationToken cancellationToken)
    {
        // At most four batches of five seconds, followed by at most thirty seconds idle.
        await Parallel.ForEachAsync(targets, new ParallelOptions
        {
            MaxDegreeOfParallelism = 64,
            CancellationToken = cancellationToken
        }, async (target, token) =>
        {
            var status = target.ProbeEnabled ? await ProbeAsync(target, token) :
                Status(target, SidecarRuntimeState.ProbeDisabled, "sidecar_probe_disabled", _clock.UtcNow);
            _catalog.Set(status);
        });
    }

    private async Task<SidecarStatus> ProbeAsync(
        SidecarProbeTarget target,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (string.IsNullOrWhiteSpace(target.BaseUrl))
        {
            return Status(target, SidecarRuntimeState.NotInstalled, "sidecar_not_installed", now);
        }

        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            return Status(target, SidecarRuntimeState.NeedsConfiguration, "invalid_sidecar_url", now);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ProbeTimeoutSeconds));
        try
        {
            var endpoint = new Uri(baseUri, target.HealthPath.TrimStart('/'));
            using var response = await _httpClientFactory.CreateClient().GetAsync(
                endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Status(target, SidecarRuntimeState.Unauthorized, "sidecar_unauthorized", now);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Status(target, SidecarRuntimeState.Degraded, "sidecar_probe_failed", now);
            }

            if (!string.IsNullOrWhiteSpace(target.ExpectedApiVersion) || target.RequireAuthenticated)
            {
                await response.Content.LoadIntoBufferAsync(64 * 1024, timeout.Token);
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(timeout.Token),
                    cancellationToken: timeout.Token);
                var root = document.RootElement;
                if (!string.IsNullOrWhiteSpace(target.ExpectedApiVersion))
                {
                    var actual = ReadString(root, "api_version", "apiVersion", "version");
                    if (!target.ExpectedApiVersion.Equals(actual, StringComparison.Ordinal))
                    {
                        return Status(target, SidecarRuntimeState.Incompatible, "sidecar_version_mismatch", now);
                    }
                }

                if (target.RequireAuthenticated && !ReadBoolean(root, "logged_in", "loggedIn", "authenticated"))
                {
                    return Status(target, SidecarRuntimeState.Unauthorized, "sidecar_authentication_required", now);
                }
            }

            return Status(target, SidecarRuntimeState.Ready, null, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "Sidecar probe {SidecarId} failed ({ExceptionType})",
                target.Id,
                ex.GetType().Name);
            return Status(
                target,
                ex is OperationCanceledException
                    ? SidecarRuntimeState.Degraded
                    : SidecarRuntimeState.Unreachable,
                ex is OperationCanceledException ? "sidecar_timeout" : "sidecar_unreachable",
                now);
        }
    }

    private static SidecarStatus Status(
        SidecarProbeTarget target,
        SidecarRuntimeState state,
        string? errorCode,
        DateTimeOffset checkedAt) => new(
        target.Id,
        target.ProviderId,
        state,
        target.Required,
        errorCode,
        checkedAt);

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static bool ReadBoolean(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return false;
    }
}
