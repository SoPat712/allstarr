using Microsoft.Extensions.Configuration;

namespace allstarr.Services.Admin;

/// <summary>
/// The optional URL path prefix used to publish the local admin surface.
/// </summary>
public sealed class AdminBasePath
{
    public const string ConfigurationKey = "Admin:BasePath";

    public AdminBasePath(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Value = Normalize(configuration[ConfigurationKey]);
    }

    public string Value { get; }

    /// <summary>
    /// Normalizes an admin prefix to either the empty string or a slash-prefixed
    /// path made up of safe ASCII segments.
    /// </summary>
    public static string Normalize(string? configured)
    {
        if (string.IsNullOrEmpty(configured))
            return string.Empty;

        if (configured != configured.Trim())
            throw Invalid(configured);

        // A single slash is the same as the unprefixed admin surface. It is
        // accepted so an explicitly configured root does not fail startup.
        if (configured == "/")
            return string.Empty;

        if (!configured.StartsWith("/", StringComparison.Ordinal) ||
            configured.StartsWith("//", StringComparison.Ordinal))
            throw Invalid(configured);

        var value = configured.EndsWith("/", StringComparison.Ordinal)
            ? configured[..^1]
            : configured;
        if (value.Length == 0 || value.Contains("//", StringComparison.Ordinal))
            throw Invalid(configured);

        var segments = value[1..].Split('/');
        if (segments.Any(segment => segment.Length == 0 || !IsSafeSegment(segment)))
            throw Invalid(configured);

        return value;
    }

    private static bool IsSafeSegment(string segment) =>
        segment.All(character =>
            character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9' or
            '-' or '_');

    private static ArgumentException Invalid(string value) =>
        new(
            $"Configuration '{ConfigurationKey}' must be empty or a slash-prefixed path containing only ASCII alphanumeric, hyphen, and underscore segments; received '{value}'.",
            ConfigurationKey);
}
