namespace allstarr.Core.Capabilities;

public enum ProviderErrorKind
{
    NotFound,
    NotSupported,
    CapabilityUnavailable,
    AccountNeedsConfiguration,
    AccountNeedsReauthentication,
    Unauthorized,
    Forbidden,
    RateLimited,
    IncompatibleMedia,
    TransientFailure,
    PermanentFailure,
    Canceled
}

/// <summary>
/// A provider failure classification whose code and message are chosen entirely by the host.
/// Provider-authored bodies or diagnostic text have no field in this outcome.
/// </summary>
public sealed record ProviderError
{
    public ProviderError(
        ProviderErrorKind kind,
        TimeSpan? retryAfter = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        }

        if (kind == ProviderErrorKind.RateLimited && retryAfter == null)
        {
            throw new ArgumentException("Rate-limited outcomes require retry timing.", nameof(retryAfter));
        }

        Kind = kind;
        Code = CodeFor(kind);
        SafeMessage = MessageFor(kind);
        RetryAfter = retryAfter;
    }

    public ProviderErrorKind Kind { get; }

    public string Code { get; }

    public string SafeMessage { get; }

    public TimeSpan? RetryAfter { get; }

    public static ProviderError CompatibilityContractChanged() => new(
        ProviderErrorKind.CapabilityUnavailable,
        "provider-contract-changed",
        "The provider API compatibility contract changed. Update Allstarr before retrying this source.");

    private ProviderError(
        ProviderErrorKind kind,
        string code,
        string safeMessage)
    {
        Kind = kind;
        Code = code;
        SafeMessage = safeMessage;
    }

    private static string CodeFor(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.NotFound => "not-found",
        ProviderErrorKind.NotSupported => "not-supported",
        ProviderErrorKind.CapabilityUnavailable => "capability-unavailable",
        ProviderErrorKind.AccountNeedsConfiguration => "account-needs-configuration",
        ProviderErrorKind.AccountNeedsReauthentication => "account-needs-reauthentication",
        ProviderErrorKind.Unauthorized => "unauthorized",
        ProviderErrorKind.Forbidden => "forbidden",
        ProviderErrorKind.RateLimited => "rate-limited",
        ProviderErrorKind.IncompatibleMedia => "incompatible-media",
        ProviderErrorKind.TransientFailure => "transient-failure",
        ProviderErrorKind.PermanentFailure => "permanent-failure",
        ProviderErrorKind.Canceled => "canceled",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string MessageFor(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.NotFound => "The requested provider resource was not found.",
        ProviderErrorKind.NotSupported => "The provider does not support this operation.",
        ProviderErrorKind.CapabilityUnavailable => "The provider capability is currently unavailable.",
        ProviderErrorKind.AccountNeedsConfiguration => "The selected provider account needs configuration.",
        ProviderErrorKind.AccountNeedsReauthentication => "Reconnect the selected provider account and replace its expired or revoked credentials.",
        ProviderErrorKind.Unauthorized => "The provider rejected the selected account credentials.",
        ProviderErrorKind.Forbidden => "Provider policy does not allow this operation.",
        ProviderErrorKind.RateLimited => "The provider rate limit was reached.",
        ProviderErrorKind.IncompatibleMedia => "The provider media is incompatible with the request policy.",
        ProviderErrorKind.TransientFailure => "The provider operation failed temporarily.",
        ProviderErrorKind.PermanentFailure => "The provider operation failed permanently.",
        ProviderErrorKind.Canceled => "The provider operation was canceled.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public sealed record ProviderOutcome<T>
{
    private ProviderOutcome(T? value, ProviderError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error == null;

    public T? Value { get; }

    public ProviderError? Error { get; }

    public static ProviderOutcome<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ProviderOutcome<T>(value, null);
    }

    public static ProviderOutcome<T> Failure(ProviderError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ProviderOutcome<T>(default, error);
    }

    public T RequireValue() => IsSuccess
        ? Value!
        : throw new InvalidOperationException(
            $"Provider outcome is not successful ({Error!.Kind}:{Error.Code}).");
}

public readonly record struct ProviderUnit
{
    public static ProviderUnit Value { get; } = new();
}
