namespace allstarr.Services.Common;

/// <summary>
/// Static configuration needed before a current provider capability can be attempted.
/// This is intentionally separate from observed health: a configured credential has
/// not necessarily been tested successfully.
/// </summary>
public enum ProviderConfigurationState
{
    NotRequired,
    NeedsConfiguration,
    Configured
}

/// <summary>
/// The most recent observation for one provider capability and account. The
/// manager can hydrate this projection from durable samples and circuit state.
/// </summary>
public enum ProviderHealthState
{
    Unknown,
    Testing,
    Healthy,
    Degraded
}

public static class ProviderCapabilities
{
    public const string Metadata = "metadata";
    public const string Streaming = "streaming";
    public const string Download = "download";
    public const string Playlist = "playlist";
    public const string Lyrics = "lyrics";
    public const string Scrobbling = "scrobbling";
}

public readonly record struct ProviderRuntimeStatusKey(
    string Provider,
    string Capability,
    Guid? ProviderAccountId)
{
    public static ProviderRuntimeStatusKey CreateAccountFree(
        string provider,
        string capability) => new(
            Normalize(provider),
            Normalize(capability),
            null);

    public static ProviderRuntimeStatusKey CreateManaged(
        string provider,
        string capability,
        Guid providerAccountId) => new(
            Normalize(provider),
            Normalize(capability),
            providerAccountId == Guid.Empty
                ? throw new ArgumentException("Provider account ID cannot be empty.", nameof(providerAccountId))
                : providerAccountId);

    private static string Normalize(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        return normalized is "applemusic" or "apple-music" or "apple_music"
            ? "apple-download"
            : normalized;
    }
}

public sealed record ProviderRuntimeStatus
{
    public required string Provider { get; init; }

    public required string Capability { get; init; }

    public required bool IsSupported { get; init; }

    public required bool IsEnabled { get; init; }

    public required ProviderConfigurationState Configuration { get; init; }

    public required ProviderHealthState Health { get; init; }

    public DateTimeOffset? TestedAt { get; init; }

    public string? ReasonCode { get; init; }

    /// <summary>
    /// Healthy is the only state that proves readiness. Configured or unknown
    /// capabilities may still be attempted by the compatibility router.
    /// </summary>
    public bool IsReady =>
        IsSupported &&
        IsEnabled &&
        Configuration != ProviderConfigurationState.NeedsConfiguration &&
        Health == ProviderHealthState.Healthy;

    /// <summary>
    /// Phase 0 compatibility decision. It preserves current best-effort routing
    /// without mislabeling an untested capability as healthy.
    /// </summary>
    public bool CanAttempt =>
        IsSupported &&
        IsEnabled &&
        Configuration != ProviderConfigurationState.NeedsConfiguration &&
        Health != ProviderHealthState.Degraded;
}
