using System.Text.Json;
using System.Text.RegularExpressions;

namespace allstarr.Core.Operations;

internal static partial class ProblemReportText
{
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        "timeout", "timed-out", "timed_out", "transient_failure", "transient-failure", "rate-limited", "rate_limited",
        "unauthorized", "forbidden", "not-found", "not_found", "provider_account_required", "account_needs_reauthentication",
        "account_needs_configuration", "circuit_open", "blocked-by-challenge", "invalid-response", "network-error",
        "incompatible-media", "download-not-verified", "probe_not_available", "sample-unavailable", "unsupported_api_version",
        "apple_download_sign_in_required", "apple_download_gateway_update_required", "source-cooldown",
        "no_indexed_candidate", "manual_override_target_not_visible", "manual_override_pinned", "manual_override_rejected_all",
        "ambiguous_top_candidates", "below_accept_threshold_review", "semantic_version_mismatch_review",
        "title_annotation_mismatch_review", "weak_artist_evidence_review", "below_suggestion_threshold",
        "accepted_route_priority_selected", "local_priority_window_selected", "provider_priority_window_selected",
        "clean_version_available", "exact_isrc", "exact_title_artist", "isrc_match", "title_match", "artist_match",
        "duration_match", "album_match", "native-local-library"
    };
    private static readonly HashSet<string> RouteParts = new(StringComparer.Ordinal)
    {
        "provider-priority", "playback-start-fast", "playback-start-balanced", "playback-start-quality",
        "household-kept", "native-local-library", "personal_account", "shared_account", "explicit_account",
        "account-free", "managed-cache", "remote", "manual-match", "manual-pin", "replay", "progressive-cache"
    };
    private static readonly HashSet<string> SeekRungs = new(StringComparer.Ordinal)
    { "byte-range", "progressive-cache", "sequential-cache", "sequential", "time-seek", "server-transcode", "restart" };

    internal static string? Clean(string? value, int limit = 500)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var bounded = value[..Math.Min(value.Length, 8192)];
        bounded = Url().Replace(bounded, "<redacted-url>");
        bounded = Bearer().Replace(bounded, "Bearer <redacted>");
        bounded = Header().Replace(bounded, "$1=<redacted>");
        bounded = Assignment().Replace(bounded, "$1=<redacted>");
        bounded = SafeOperationalText.Sanitize(bounded, Math.Min(limit, 2000)) ?? "";
        return bounded.Length == 0 ? null : bounded;
    }

    internal static string? Reason(string? value) => value != null && (Reasons.Contains(value) ||
        value is "http-400" or "http-401" or "http-403" or "http-404" or "http-408" or "http-410" or "http-429" or
            "http-500" or "http-502" or "http-503" or "http-504") ? value : null;
    internal static string? Route(string? value) => value is { Length: > 0 and <= 200 } && value.Split(':').All(part =>
        RouteParts.Contains(part) || part.StartsWith("fallback-", StringComparison.Ordinal) && Reason(part[9..]) != null) ? value : null;
    internal static string? Rung(string? value) => value != null && SeekRungs.Contains(value) ? value : null;
    internal static bool IsReference(string? value, int maximum = 500) => value is { Length: > 0 } && value.Length <= maximum &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
    internal static IReadOnlyList<string> ReasonArray(string value)
    {
        if (value.Length > 32768) return [];
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind != JsonValueKind.Array ? [] : document.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String).Select(item => Reason(item.GetString()))
                .OfType<string>().Distinct(StringComparer.Ordinal).Take(12).ToArray();
        }
        catch (JsonException) { return []; }
    }

    [GeneratedRegex(@"(?:[a-z][a-z0-9+.-]*://|//)[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();
    [GeneratedRegex(@"\bBearer\s+[^\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();
    [GeneratedRegex(@"\b(cookie|authorization)[""']?\s*[=:]\s*[^\r\n]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Header();
    [GeneratedRegex(@"\b(token|access[_-]?token|refresh[_-]?token|password|secret|cookie|authorization|api[_-]?key|arl|signature|sig)[""']?\s*[=:]\s*(?:""[^""]*""|'[^']*'|[^\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();
}
