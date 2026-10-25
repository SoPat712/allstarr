using allstarr.Services.Common;

namespace allstarr.Core.Settings;

internal static class OperatorSettingPolicy
{
    internal static readonly RuntimeSettingDefinition[] Definitions =
    [
        new("ChallengeSolver:Url", RuntimeSettingValueType.String, "ChallengeSolver:Url", AllowEmpty: true, DefaultValue: "", MaximumLength: 2048),
        new("ChallengeSolver:TimeoutSeconds", RuntimeSettingValueType.Integer, "ChallengeSolver:TimeoutSeconds", 10, 180, DefaultValue: "60"),
        new("MusicBrainz:SourceId", RuntimeSettingValueType.String, "MusicBrainz:SourceId", DefaultValue: "musicbrainz", MaximumLength: 100, RequiresRestart: true),
        new("MusicBrainz:BaseUrl", RuntimeSettingValueType.String, "MusicBrainz:BaseUrl", DefaultValue: "https://musicbrainz.org/ws/2", MaximumLength: 2048, RequiresRestart: true),
        new("MusicBrainz:RateLimitMs", RuntimeSettingValueType.Integer, "MusicBrainz:RateLimitMs", 1000, 60000, DefaultValue: "1000", RequiresRestart: true),
        new("MusicBrainz:AuthorizedUserAgentOverride", RuntimeSettingValueType.String, "MusicBrainz:AuthorizedUserAgentOverride", AllowEmpty: true, DefaultValue: "", MaximumLength: 500, RequiresRestart: true),
        new("Odesli:Enabled", RuntimeSettingValueType.Boolean, "Odesli:Enabled", DefaultValue: "false", RequiresRestart: true),
        new("Odesli:RateLimitMs", RuntimeSettingValueType.Integer, "Odesli:RateLimitMs", 1000, 60000, DefaultValue: "6000", RequiresRestart: true),
        new("Extensions:AllowRemoteInstall", RuntimeSettingValueType.Boolean, "Extensions:AllowRemoteInstall", DefaultValue: "false", RequiresRestart: true),
        new("Debug:LogAllRequests", RuntimeSettingValueType.Boolean, "Debug:LogAllRequests", DefaultValue: "false", RequiresRestart: true),
        new("Operations:EventLog:RetentionDays", RuntimeSettingValueType.Integer, "Operations:EventLog:RetentionDays", 1, 3650, DefaultValue: "30", RequiresRestart: true),
        new("Operations:EventLog:MaximumRows", RuntimeSettingValueType.Integer, "Operations:EventLog:MaximumRows", 1000, 5000000, DefaultValue: "250000", RequiresRestart: true),
        new("Operations:EventLog:CleanupIntervalMinutes", RuntimeSettingValueType.Integer, "Operations:EventLog:CleanupIntervalMinutes", 5, 10080, DefaultValue: "360", RequiresRestart: true),
        new("Operations:EventLog:CleanupBatchSize", RuntimeSettingValueType.Integer, "Operations:EventLog:CleanupBatchSize", 100, 10000, DefaultValue: "1000", RequiresRestart: true),
        new("Operations:EventLog:JobHistoryRetentionDays", RuntimeSettingValueType.Integer, "Operations:EventLog:JobHistoryRetentionDays", 1, 3650, DefaultValue: "7", RequiresRestart: true),
        new("Backend:MusicLibraryIds", RuntimeSettingValueType.String, "Backend:MusicLibraryIds", AllowEmpty: true, DefaultValue: "", MaximumLength: 20000, RequiresRestart: true),
        new("Cors:AllowedOrigins", RuntimeSettingValueType.String, "Cors:AllowedOrigins", AllowEmpty: true, DefaultValue: "", MaximumLength: 8192, RequiresRestart: true),
        new("Cors:AllowedMethods", RuntimeSettingValueType.String, "Cors:AllowedMethods", AllowEmpty: true, DefaultValue: "", MaximumLength: 1000, RequiresRestart: true),
        new("Cors:AllowedHeaders", RuntimeSettingValueType.String, "Cors:AllowedHeaders", AllowEmpty: true, DefaultValue: "", MaximumLength: 4000, RequiresRestart: true),
        new("Cors:AllowCredentials", RuntimeSettingValueType.Boolean, "Cors:AllowCredentials", DefaultValue: "false", RequiresRestart: true),
    ];

    internal static void Validate(string key, string value)
    {
        allstarr.Core.Storage.DurableBackupService.ValidatePolicyString(key, value);
        if (key is "Admin:Oidc:Authority" or "Admin:Oidc:PublicUrl" && value.Length > 0 &&
            (!Uri.TryCreate(value, UriKind.Absolute, out var oidcUri) || oidcUri.Scheme != "https" ||
            oidcUri.UserInfo.Length > 0 || oidcUri.Query.Length > 0 || oidcUri.Fragment.Length > 0))
            throw new ArgumentException("Single sign-on URLs must use HTTPS without credentials, query or fragment.");
        if (key.StartsWith("Admin:Oidc:", StringComparison.Ordinal) && value.Any(char.IsControl))
            throw new ArgumentException("Single sign-on settings cannot contain control characters.");
        if (key == "ChallengeSolver:Url" && value.Length > 0 &&
            (value.Any(char.IsControl) || !OutboundRequestGuard.TryCreateConfiguredServiceUri(value, out _, out _)))
            throw new ArgumentException("Use an HTTP or HTTPS solver URL without credentials, query or fragment.");
        if (key == "MusicBrainz:BaseUrl" &&
            !OutboundRequestGuard.TryCreateConfiguredServiceUri(value, out _, out _))
            throw new ArgumentException("Use an HTTP or HTTPS metadata service URL without credentials, query or fragment.");
        if (key == "MusicBrainz:SourceId" && (value.Length == 0 || !value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new ArgumentException("Use a metadata source identifier containing letters, numbers, dots, underscores or hyphens.");
        if (key == "MusicBrainz:AuthorizedUserAgentOverride" && value.Any(char.IsControl))
            throw new ArgumentException("The metadata user agent cannot contain control characters.");
        if (key == "Backend:MusicLibraryIds")
        {
            var ids = Parts(value);
            if (ids.Length > 64 || ids.Any(id => id.Length > 300 || id.Any(char.IsControl)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new ArgumentException("Use at most 64 distinct music library identifiers.");
        }
        if (key == "Cors:AllowedOrigins" && Parts(value).Any(origin => origin != "*" &&
            (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" || origin.EndsWith('/'))))
            throw new ArgumentException("Use comma-separated HTTP or HTTPS origins without paths, credentials or trailing slashes.");
        if (key is "Cors:AllowedMethods" or "Cors:AllowedHeaders" && Parts(value).Any(item =>
            !item.All(ch => char.IsAsciiLetterOrDigit(ch) || "!#$%&'*+-.^_`|~".Contains(ch))))
            throw new ArgumentException("Use comma-separated HTTP methods or header names without whitespace or control characters.");
    }

    internal static void ValidateCors(string origins, string credentials)
    {
        if (bool.TryParse(credentials, out var allow) && allow && Parts(origins).Contains("*"))
            throw new ArgumentException("Credentialed browser access requires explicit origins.");
    }

    private static string[] Parts(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
