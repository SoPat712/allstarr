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

    public static ProviderError AppleDownloadGatewayUpdateRequired() => new(
        ProviderErrorKind.CapabilityUnavailable,
        "apple-download-gateway-update-required",
        "Apple download gateway needs an update.");

    public static ProviderError AppleDownloadSignInRequired() => new(
        ProviderErrorKind.AccountNeedsReauthentication,
        "apple-download-sign-in-required",
        "Apple download needs sign-in.");

    public static ProviderError AppleWebTokenUnavailable() => new(
        ProviderErrorKind.CapabilityUnavailable,
        "apple-web-token-unavailable",
        "Apple's web-player token could not be fetched. Retry the Apple account test later.");

    public static ProviderError ServiceUnavailable() => new(
        ProviderErrorKind.TransientFailure,
        "service_unavailable",
        "The service's download server is unavailable.");

    public static ProviderError DownloadContainerConversionFailed() => new(
        ProviderErrorKind.IncompatibleMedia,
        "container-conversion-failed",
        "The downloaded audio could not be copied to its native container.");

    public static ProviderError DownloadMediaToolsUnavailable() => new(
        ProviderErrorKind.CapabilityUnavailable,
        "media-tools-unavailable",
        "Download processing requires ffmpeg and ffprobe on the server.");

    public static ProviderError DownloadVerificationRequired() => new(
        ProviderErrorKind.CapabilityUnavailable,
        "verification-required",
        "The download service requires verification.");

    public static ProviderError BlockedByChallenge() => new(
        ProviderErrorKind.CapabilityUnavailable,
        "blocked-by-challenge",
        "This source is behind a Cloudflare check. Add a FlareSolverr or Byparr URL in Settings to use it.");

    public static ProviderError LyricsUpstreamBusyOrTimedOut() => new(
        ProviderErrorKind.TransientFailure,
        "lyrics-upstream-busy-or-timed-out",
        "The lyrics upstream is busy or timed out. Try another lyrics source.");

    public static ProviderError StreamRangeUnavailable() => new(
        ProviderErrorKind.NotSupported,
        "stream-range-unavailable",
        "The playback source cannot serve the requested byte range. Restart playback before seeking.");

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
