using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Middleware;
using System.Text.Json.Serialization;

namespace allstarr.Core.Protocols;

public enum ProtocolKind
{
    Unknown = 0,
    Jellyfin = 1,
    Subsonic = 2
}

public sealed record ProtocolClientDescriptor
{
    public ProtocolClientDescriptor(
        string? clientId = null,
        string? deviceId = null,
        string? deviceName = null)
    {
        ClientId = ProviderContractValidation.OptionalText(clientId, nameof(clientId), 200);
        DeviceId = ProviderContractValidation.OptionalText(deviceId, nameof(deviceId), 200);
        DeviceName = ProviderContractValidation.OptionalText(deviceName, nameof(deviceName), 200);
    }

    public string? ClientId { get; }

    public string? DeviceId { get; }

    public string? DeviceName { get; }
}

public sealed record ProtocolExecutionContext
{
    public ProtocolExecutionContext(
        ProtocolKind protocol,
        string backendInstanceId,
        string verifiedBackendPrincipalId,
        AllstarrPrincipal? principal,
        string correlationId,
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        ProtocolClientDescriptor? client = null,
        string? libraryScopeId = null)
    {
        if (!Enum.IsDefined(protocol) || protocol == ProtocolKind.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(protocol));
        }

        if (deadline == default)
        {
            throw new ArgumentException("A protocol execution deadline is required.", nameof(deadline));
        }

        BackendInstanceId = ProviderContractValidation.RequiredText(
            backendInstanceId,
            nameof(backendInstanceId),
            200);
        VerifiedBackendPrincipalId = ProviderContractValidation.RequiredText(
            verifiedBackendPrincipalId,
            nameof(verifiedBackendPrincipalId),
            500);
        CorrelationId = ProviderContractValidation.RequiredText(
            correlationId,
            nameof(correlationId),
            100);
        LibraryScopeId = ProviderContractValidation.OptionalText(
            libraryScopeId,
            nameof(libraryScopeId),
            300);

        var expectedBackendType = protocol.ToString().ToLowerInvariant();
        if (principal != null &&
            (!principal.BackendType.Equals(expectedBackendType, StringComparison.Ordinal) ||
             !principal.BackendInstanceId.Equals(BackendInstanceId, StringComparison.Ordinal) ||
             !principal.BackendPrincipalId.Equals(VerifiedBackendPrincipalId, StringComparison.Ordinal)))
        {
            throw new UnauthorizedAccessException(
                "The canonical principal does not match the verified protocol principal.");
        }

        Protocol = protocol;
        Principal = principal;
        Client = client ?? new ProtocolClientDescriptor();
        Deadline = deadline;
        CancellationToken = cancellationToken;
        Actor = principal == null
            ? null
            : new ProviderActorContext(
                principal.TenantId,
                principal.IsAdministrator
                    ? ProviderActorKind.Administrator
                    : ProviderActorKind.User,
                principal.UserId,
                new ProviderBackendPrincipal(
                    principal.BackendType,
                    principal.BackendInstanceId,
                    principal.BackendPrincipalId));
    }

    public ProtocolKind Protocol { get; }

    public string BackendInstanceId { get; }

    public string VerifiedBackendPrincipalId { get; }

    public AllstarrPrincipal? Principal { get; }

    public ProviderActorContext? Actor { get; }

    public ProtocolClientDescriptor Client { get; }

    public string? LibraryScopeId { get; }

    public string CorrelationId { get; }

    public DateTimeOffset Deadline { get; }

    [JsonIgnore]
    public CancellationToken CancellationToken { get; }

    public bool CanRunUserScopedWork => Actor != null;

    public ProviderActorContext RequireActor() => Actor ?? throw new UnauthorizedAccessException(
        "The verified backend principal is not linked to an Allstarr user.");
}

public sealed class ProtocolExecutionOptions
{
    public const string SectionName = "ProtocolExecution";

    public int OperationTimeoutSeconds { get; set; } = 30;

    public TimeSpan GetOperationTimeout()
    {
        if (OperationTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException(
                "ProtocolExecution:OperationTimeoutSeconds must be between 1 and 300.");
        }

        return TimeSpan.FromSeconds(OperationTimeoutSeconds);
    }
}

public sealed class ProtocolExecutionContextFactory
{
    public const string HttpContextItemKey = "allstarr.protocol-execution-context";

    private readonly ProtocolExecutionOptions _options;
    private readonly IPlatformClock _clock;
    private readonly IdentityOptions _identityOptions;

    public ProtocolExecutionContextFactory(
        ProtocolExecutionOptions options,
        IPlatformClock clock,
        IdentityOptions identityOptions)
    {
        _options = options;
        _clock = clock;
        _identityOptions = identityOptions;
    }

    public ProtocolExecutionContext Create(
        HttpContext httpContext,
        ProtocolKind protocol,
        string verifiedBackendPrincipalId,
        string? backendInstanceId = null,
        ProtocolClientDescriptor? client = null,
        string? libraryScopeId = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var principal = httpContext.Items.TryGetValue(
                BackendIdentityResolver.HttpContextPrincipalItemKey,
                out var value)
            ? value as AllstarrPrincipal
            : null;
        var correlationId = httpContext.Items[
                CorrelationMiddleware.HttpContextItemKey]?.ToString()
            ?? httpContext.TraceIdentifier;
        correlationId = correlationId.Length <= 100
            ? correlationId
            : correlationId[..100];
        backendInstanceId ??= principal?.BackendInstanceId ?? _identityOptions.BackendInstanceId;

        return new ProtocolExecutionContext(
            protocol,
            backendInstanceId,
            verifiedBackendPrincipalId,
            principal,
            correlationId,
            _clock.UtcNow.Add(_options.GetOperationTimeout()),
            httpContext.RequestAborted,
            client,
            libraryScopeId);
    }
}

public static class ProtocolExecutionHttpContextExtensions
{
    public static ProtocolExecutionContext? GetProtocolExecutionContext(this HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.Items.TryGetValue(
                ProtocolExecutionContextFactory.HttpContextItemKey,
                out var value)
            ? value as ProtocolExecutionContext
            : null;
    }

    public static ProtocolExecutionContext RequireProtocolExecutionContext(this HttpContext httpContext) =>
        httpContext.GetProtocolExecutionContext()
        ?? throw new InvalidOperationException(
            "No verified protocol execution context is available for this request.");
}
