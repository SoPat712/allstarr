using System.Collections.Concurrent;
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

    public int ProbeIntervalSeconds { get; set; } = 900;
    public int ProbeJitterSeconds { get; set; } = 60;
    public int ProbeTimeoutSeconds { get; set; } = 5;
    public int MaxProbesPerCycle { get; set; } = 16;
    public List<SidecarProbeTarget> Targets { get; set; } = [];

    public void Validate()
    {
        if (ProbeIntervalSeconds is < 30 or > 86400 ||
            ProbeJitterSeconds is < 0 or > 900 ||
            ProbeTimeoutSeconds is < 1 or > 60 ||
            MaxProbesPerCycle is < 1 or > 64)
        {
            throw new InvalidOperationException("Sidecar probe policy is outside the supported bounds.");
        }

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

public sealed class SidecarStatusCatalog
{
    private readonly ConcurrentDictionary<string, SidecarStatus> _statuses =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly OperationalRuntimeState? _runtimeState;

    public SidecarStatusCatalog(
        SidecarHealthOptions options,
        OperationalRuntimeState? runtimeState = null)
    {
        _runtimeState = runtimeState;
        foreach (var target in options.Targets)
        {
            var state = string.IsNullOrWhiteSpace(target.BaseUrl)
                ? SidecarRuntimeState.NotInstalled
                : SidecarRuntimeState.Unknown;
            Set(new SidecarStatus(
                target.Id,
                target.ProviderId,
                state,
                target.Required,
                state == SidecarRuntimeState.NotInstalled ? "sidecar_not_installed" : null,
                null));
        }
    }

    public IReadOnlyList<SidecarStatus> GetAll() => _statuses.Values
        .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public bool TryGet(string id, out SidecarStatus status) =>
        _statuses.TryGetValue(id, out status!);

    public void Set(SidecarStatus status)
    {
        if (_statuses.TryGetValue(status.Id, out var prior) && prior.State != status.State)
        {
            if (prior.State == SidecarRuntimeState.Ready && status.State != SidecarRuntimeState.Ready)
            {
                _runtimeState?.RecordSidecarTransition(recovered: false);
            }
            else if (prior.State != SidecarRuntimeState.Ready && status.State == SidecarRuntimeState.Ready)
            {
                _runtimeState?.RecordSidecarTransition(recovered: true);
            }
        }

        _statuses[status.Id] = status;
    }
}

public sealed class SidecarHealthMonitor : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SidecarHealthOptions _options;
    private readonly SidecarStatusCatalog _catalog;
    private readonly ILogger<SidecarHealthMonitor> _logger;
    private readonly IPlatformClock _clock;
    private int _nextTargetIndex;

    public SidecarHealthMonitor(
        IHttpClientFactory httpClientFactory,
        SidecarHealthOptions options,
        SidecarStatusCatalog catalog,
        ILogger<SidecarHealthMonitor> logger,
        IPlatformClock? clock = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _catalog = catalog;
        _logger = logger;
        _clock = clock ?? new SystemPlatformClock();
        _options.Validate();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeScheduledCycleAsync(stoppingToken);
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

            var jitter = _options.ProbeJitterSeconds == 0
                ? 0
                : Random.Shared.Next(0, _options.ProbeJitterSeconds + 1);
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.ProbeIntervalSeconds + jitter),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ProbeScheduledCycleAsync(CancellationToken cancellationToken)
    {
        if (_options.Targets.Count == 0)
        {
            return;
        }

        var count = Math.Min(_options.MaxProbesPerCycle, _options.Targets.Count);
        var targets = new List<SidecarProbeTarget>(count);
        for (var offset = 0; offset < count; offset++)
        {
            targets.Add(_options.Targets[(_nextTargetIndex + offset) % _options.Targets.Count]);
        }

        _nextTargetIndex = (_nextTargetIndex + count) % _options.Targets.Count;
        await ProbeTargetsAsync(targets, cancellationToken);
    }

    public async Task ProbeAllOnceAsync(CancellationToken cancellationToken = default)
    {
        await ProbeTargetsAsync(_options.Targets, cancellationToken);
    }

    private async Task ProbeTargetsAsync(
        IEnumerable<SidecarProbeTarget> targets,
        CancellationToken cancellationToken)
    {
        foreach (var target in targets)
        {
            if (!target.ProbeEnabled)
            {
                _catalog.Set(Status(
                    target,
                    SidecarRuntimeState.ProbeDisabled,
                    "sidecar_probe_disabled",
                    _clock.UtcNow));
                continue;
            }

            using var activity = PlatformDiagnostics.ActivitySource.StartActivity("sidecar.probe");
            activity?.SetTag("sidecar.id", target.Id);
            activity?.SetTag("provider.id", target.ProviderId);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var status = await ProbeAsync(target, cancellationToken);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            _catalog.Set(status);
            activity?.SetTag("sidecar.state", status.State.ToString().ToLowerInvariant());
            activity?.SetTag("error.code", status.ErrorCode);
            PlatformDiagnostics.SidecarProbes.Add(
                1,
                new("provider.id", target.ProviderId),
                new("sidecar.state", status.State.ToString().ToLowerInvariant()));
            PlatformDiagnostics.SidecarProbeLatency.Record(
                elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("provider.id", target.ProviderId));
        }
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
            using var response = await _httpClientFactory.CreateClient().GetAsync(endpoint, timeout.Token);
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
