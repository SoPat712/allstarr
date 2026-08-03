using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using allstarr.Models.Settings;

namespace allstarr.Services.Common;

public sealed record BackendSelectionAuthority(
    BackendType Type,
    string EffectiveValue,
    string Source,
    bool IsExplicitDeploymentValue,
    bool HasConflictingDotEnvValue,
    string? ConflictingDotEnvValue);

/// <summary>
/// Loads supported flat .env keys into ASP.NET configuration at process startup.
/// </summary>
public static class RuntimeEnvConfiguration
{
    private static readonly IReadOnlyDictionary<string, string[]> ExactKeyMappings =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["BACKEND_TYPE"] = ["Backend:Type"],
            ["ADMIN_BIND_ANY_IP"] = ["Admin:BindAnyIp"],
            ["ADMIN_TRUSTED_SUBNETS"] = ["Admin:TrustedSubnets"],
            ["ADMIN_ENABLE_ENV_EXPORT"] = ["Admin:EnableEnvExport"],

            ["CORS_ALLOWED_ORIGINS"] = ["Cors:AllowedOrigins"],
            ["CORS_ALLOWED_METHODS"] = ["Cors:AllowedMethods"],
            ["CORS_ALLOWED_HEADERS"] = ["Cors:AllowedHeaders"],
            ["CORS_ALLOW_CREDENTIALS"] = ["Cors:AllowCredentials"],
            ["EXTENSIONS_ALLOW_REMOTE_INSTALL"] = ["Extensions:AllowRemoteInstall"],

            ["SUBSONIC_URL"] = ["Subsonic:Url"],
            ["ALLSTARR_STORAGE_PROVIDER"] = ["Storage:Provider"],
            ["ALLSTARR_STORAGE_CONNECTION_STRING"] = ["Storage:ConnectionString"],
            ["ALLSTARR_STORAGE_PASSWORD_FILE"] = ["Storage:PasswordFile"],
            ["ALLSTARR_STORAGE_AUTO_MIGRATE"] = ["Storage:AutoMigrate"],
            ["ALLSTARR_STORAGE_ENFORCE_MUTATION_GUARD"] = ["Storage:EnforceMutationGuard"],
            ["ALLSTARR_BACKUP_DIRECTORY"] = ["Storage:BackupDirectory"],
            ["ALLSTARR_SECRET_KEY_RING_PATH"] = ["Secrets:KeyRingPath"],
            ["ALLSTARR_MULTI_USER_MODE"] = ["Identity:Mode"],
            ["ALLSTARR_BACKEND_INSTANCE_ID"] = ["Identity:BackendInstanceId"],
            ["ALLSTARR_PROVIDER_ACCOUNT_MANAGEMENT_MODE"] = ["ProviderAccounts:ManagementMode"],
            ["ALLSTARR_ALLOW_GLOBAL_ACCOUNTS"] = ["ProviderPolicy:AllowGlobalAccounts"],
            ["ALLSTARR_ALLOW_GLOBAL_PERSONAL_ACCOUNTS"] = ["ProviderPolicy:AllowGlobalPersonalAccounts"],
            ["ALLSTARR_SHARED_DOWNLOADER_ACCOUNT_ID"] = ["ProviderPolicy:SharedDownloaderAccountId"],
            ["JELLYFIN_URL"] = ["Jellyfin:Url"],
            ["JELLYFIN_API_KEY"] = ["Jellyfin:ApiKey"],
            ["JELLYFIN_USER_ID"] = ["Jellyfin:UserId"],
            ["JELLYFIN_CLIENT_USERNAME"] = ["Jellyfin:ClientUsername"],
            ["JELLYFIN_LIBRARY_ID"] = ["Jellyfin:LibraryId"],

            ["LIBRARY_DOWNLOAD_PATH"] = ["Library:DownloadPath"],
            ["LIBRARY_KEPT_PATH"] = ["Library:KeptPath"],

            ["SPOTIFY_IMPORT_ENABLED"] = ["SpotifyImport:Enabled"],
            ["SPOTIFY_IMPORT_SYNC_START_HOUR"] = ["SpotifyImport:SyncStartHour"],
            ["SPOTIFY_IMPORT_SYNC_START_MINUTE"] = ["SpotifyImport:SyncStartMinute"],
            ["SPOTIFY_IMPORT_SYNC_WINDOW_HOURS"] = ["SpotifyImport:SyncWindowHours"],
            ["SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS"] = ["SpotifyImport:MatchingIntervalHours"],
            ["SPOTIFY_IMPORT_PLAYLISTS"] = ["SpotifyImport:Playlists"],

            ["SPOTIFY_API_ENABLED"] = ["SpotifyApi:Enabled"],
            ["SPOTIFY_API_SESSION_COOKIE"] = ["SpotifyApi:SessionCookie"],
            ["SPOTIFY_API_SESSION_COOKIE_SET_DATE"] = ["SpotifyApi:SessionCookieSetDate"],
            ["SPOTIFY_API_CACHE_DURATION_MINUTES"] = ["SpotifyApi:CacheDurationMinutes"],
            ["SPOTIFY_API_RATE_LIMIT_DELAY_MS"] = ["SpotifyApi:RateLimitDelayMs"],
            ["SPOTIFY_API_PREFER_ISRC_MATCHING"] = ["SpotifyApi:PreferIsrcMatching"],
            ["SPOTIFY_LYRICS_API_URL"] = ["SpotifyApi:LyricsApiUrl"],

            ["SCROBBLING_ENABLED"] = ["Scrobbling:Enabled"],
            ["SCROBBLING_LOCAL_TRACKS_ENABLED"] = ["Scrobbling:LocalTracksEnabled"],
            ["SCROBBLING_SYNTHETIC_LOCAL_PLAYED_SIGNAL_ENABLED"] = ["Scrobbling:SyntheticLocalPlayedSignalEnabled"],
            ["SCROBBLING_LASTFM_ENABLED"] = ["Scrobbling:LastFm:Enabled"],
            ["SCROBBLING_LASTFM_API_KEY"] = ["Scrobbling:LastFm:ApiKey"],
            ["SCROBBLING_LASTFM_SHARED_SECRET"] = ["Scrobbling:LastFm:SharedSecret"],
            ["SCROBBLING_LASTFM_SESSION_KEY"] = ["Scrobbling:LastFm:SessionKey"],
            ["SCROBBLING_LASTFM_USERNAME"] = ["Scrobbling:LastFm:Username"],
            ["SCROBBLING_LASTFM_PASSWORD"] = ["Scrobbling:LastFm:Password"],
            ["SCROBBLING_LISTENBRAINZ_ENABLED"] = ["Scrobbling:ListenBrainz:Enabled"],
            ["SCROBBLING_LISTENBRAINZ_USER_TOKEN"] = ["Scrobbling:ListenBrainz:UserToken"],

            ["DEBUG_LOG_ALL_REQUESTS"] = ["Debug:LogAllRequests"],
            ["DEBUG_REDACT_SENSITIVE_REQUEST_VALUES"] = ["Debug:RedactSensitiveRequestValues"],

            ["AUDIO_QUALITY"] = ["Audio:Quality"],

            ["DEEZER_ARL"] = ["Deezer:Arl"],
            ["DEEZER_ARL_FALLBACK"] = ["Deezer:ArlFallback"],
            ["DEEZER_QUALITY"] = ["Deezer:Quality"],
            ["DEEZER_MIN_REQUEST_INTERVAL_MS"] = ["Deezer:MinRequestIntervalMs"],

            ["QOBUZ_USER_AUTH_TOKEN"] = ["Qobuz:UserAuthToken"],
            ["QOBUZ_USER_ID"] = ["Qobuz:UserId"],
            ["QOBUZ_QUALITY"] = ["Qobuz:Quality"],
            ["QOBUZ_MIN_REQUEST_INTERVAL_MS"] = ["Qobuz:MinRequestIntervalMs"],

            ["SQUIDWTF_QUALITY"] = ["SquidWTF:Quality"],
            ["SQUIDWTF_MIN_REQUEST_INTERVAL_MS"] = ["SquidWTF:MinRequestIntervalMs"],

            ["APPLE_DOWNLOAD_URL"] = ["AppleDownload:BaseUrl"],
            ["APPLE_DOWNLOAD_QUALITY"] = ["AppleDownload:Quality"],

            ["MUSICBRAINZ_ENABLED"] = ["MusicBrainz:Enabled"],
            ["MUSICBRAINZ_USERNAME"] = ["MusicBrainz:Username"],
            ["MUSICBRAINZ_PASSWORD"] = ["MusicBrainz:Password"],

            ["MULTI_PROVIDER_METADATA_ORDER"] = ["MULTI_PROVIDER_METADATA_ORDER"],
            ["MULTI_PROVIDER_DOWNLOAD_ORDER"] = ["MULTI_PROVIDER_DOWNLOAD_ORDER"],
            ["MULTI_PROVIDER_STREAMING_ORDER"] = ["MULTI_PROVIDER_STREAMING_ORDER"],
            ["MULTI_PROVIDER_PLAYLIST_ORDER"] = ["MULTI_PROVIDER_PLAYLIST_ORDER"],
            ["MULTI_PROVIDER_LYRICS_ORDER"] = ["MULTI_PROVIDER_LYRICS_ORDER"],
            ["MULTI_PROVIDER_ENABLED_SEARCH"] = ["MULTI_PROVIDER_ENABLED_SEARCH"],
            ["MULTI_PROVIDER_ENABLED_PLAYLIST"] = ["MULTI_PROVIDER_ENABLED_PLAYLIST"],
            ["MULTI_PROVIDER_DISABLED_PROVIDERS"] = ["MULTI_PROVIDER_DISABLED_PROVIDERS"],
            ["EXTENSION_REPOSITORIES"] = ["EXTENSION_REPOSITORIES"],

            ["CACHE_SEARCH_RESULTS_MINUTES"] = ["Cache:SearchResultsMinutes"],
            ["CACHE_PLAYLIST_IMAGES_HOURS"] = ["Cache:PlaylistImagesHours"],
            ["CACHE_LYRICS_DAYS"] = ["Cache:LyricsDays"],
            ["CACHE_GENRE_DAYS"] = ["Cache:GenreDays"],
            ["CACHE_METADATA_DAYS"] = ["Cache:MetadataDays"],
            ["CACHE_ODESLI_LOOKUP_DAYS"] = ["Cache:OdesliLookupDays"],
            ["CACHE_PROXY_IMAGES_DAYS"] = ["Cache:ProxyImagesDays"],
            ["CACHE_MEDIA_DIRECTORY"] = ["Cache:MediaDirectory"],
            ["CACHE_MEDIA_MAXIMUM_MEGABYTES"] = ["Cache:MediaMaximumMegabytes"],
            ["CACHE_MEDIA_MAXIMUM_ENTRY_MEGABYTES"] = ["Cache:MediaMaximumEntryMegabytes"],
            ["CACHE_MEDIA_CLEANUP_FILE_LIMIT"] = ["Cache:MediaCleanupFileLimit"],
            ["CACHE_TRANSCODE_MINUTES"] = ["Cache:TranscodeCacheMinutes"]
        };

    private static readonly IReadOnlyDictionary<string, string[]> SharedBackendKeyMappings =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["MUSIC_SERVICE"] = ["Subsonic:MusicService", "Jellyfin:MusicService"],
            ["EXPLICIT_FILTER"] = ["Subsonic:ExplicitFilter", "Jellyfin:ExplicitFilter"],
            ["DOWNLOAD_MODE"] = ["Subsonic:DownloadMode", "Jellyfin:DownloadMode"],
            ["STORAGE_MODE"] = ["Subsonic:StorageMode", "Jellyfin:StorageMode"],
            ["CACHE_DURATION_HOURS"] = ["Subsonic:CacheDurationHours", "Jellyfin:CacheDurationHours"],
            ["ENABLE_EXTERNAL_PLAYLISTS"] = ["Subsonic:EnableExternalPlaylists", "Jellyfin:EnableExternalPlaylists"],
            ["PLAYLISTS_DIRECTORY"] = ["Subsonic:PlaylistsDirectory", "Jellyfin:PlaylistsDirectory"]
        };

    private static readonly HashSet<string> IgnoredComposeOnlyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "DOWNLOAD_PATH",
        "KEPT_PATH",
        "CACHE_PATH"
    };

    public static string ResolveEnvFilePath(IHostEnvironment environment)
    {
        return environment.IsDevelopment()
            ? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".env"))
            : "/app/.env";
    }

    public static void AddDotEnvOverrides(
        ConfigurationManager configuration,
        IHostEnvironment environment)
    {
        AddDotEnvOverrides(configuration, ResolveEnvFilePath(environment));
    }

    public static void AddDotEnvOverrides(
        ConfigurationManager configuration,
        string envFilePath)
    {
        var deploymentOwnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deploymentOverrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            foreach (var mapping in MapEnvVarToConfiguration(
                         Convert.ToString(variable.Key) ?? string.Empty,
                         Convert.ToString(variable.Value)))
            {
                deploymentOwnedKeys.Add(mapping.Key);
                deploymentOverrides[mapping.Key] = mapping.Value;
            }
        }

        var overrides = LoadDotEnvOverrides(envFilePath, deploymentOwnedKeys);
        if (overrides.Count > 0)
        {
            configuration.AddInMemoryCollection(overrides);
        }

        // Flat deployment aliases such as BACKEND_TYPE are not translated by the
        // framework environment provider. Add their mapped values last so an explicit
        // process environment value remains authoritative over a mounted .env file.
        if (deploymentOverrides.Count > 0)
        {
            configuration.AddInMemoryCollection(deploymentOverrides);
        }
    }

    public static BackendSelectionAuthority ResolveBackendSelection(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var rawValue = configuration["Backend:Type"]?.Trim();
        if (string.IsNullOrWhiteSpace(rawValue) ||
            !Enum.TryParse<BackendType>(rawValue, ignoreCase: true, out var backendType) ||
            !Enum.IsDefined(backendType))
        {
            throw new InvalidOperationException(
                "Backend:Type must explicitly select Jellyfin or Subsonic.");
        }

        var processValue = FirstNonEmptyEnvironmentValue("Backend__Type", "BACKEND_TYPE");
        var envFilePath = ResolveEnvFilePath(environment);
        var dotEnvValue = ReadDotEnvValue(envFilePath, "Backend__Type", "BACKEND_TYPE");
        var hasProcessValue = !string.IsNullOrWhiteSpace(processValue);
        var hasDotEnvValue = !string.IsNullOrWhiteSpace(dotEnvValue);
        var source = hasProcessValue
            ? "process-environment"
            : hasDotEnvValue
                ? "deployment-env-file"
                : "application-default";
        var explicitDeploymentValue = hasProcessValue || hasDotEnvValue;

        if (environment.IsProduction() && !explicitDeploymentValue)
        {
            throw new InvalidOperationException(
                "Production requires an explicit Backend__Type or BACKEND_TYPE deployment value; " +
                "the image default cannot select the active media-server backend.");
        }

        var conflictingDotEnv = hasProcessValue &&
                                hasDotEnvValue &&
                                !processValue!.Equals(dotEnvValue, StringComparison.OrdinalIgnoreCase);
        return new BackendSelectionAuthority(
            backendType,
            backendType.ToString(),
            source,
            explicitDeploymentValue,
            conflictingDotEnv,
            conflictingDotEnv ? dotEnvValue : null);
    }

    public static Dictionary<string, string?> LoadDotEnvOverrides(
        string envFilePath,
        IReadOnlySet<string>? deploymentOwnedKeys = null)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(envFilePath))
        {
            return overrides;
        }

        foreach (var line in File.ReadLines(envFilePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var envKey = line[..separatorIndex].Trim();
            var envValue = StripQuotes(line[(separatorIndex + 1)..].Trim());

            foreach (var mapping in MapEnvVarToConfiguration(envKey, envValue))
            {
                if (deploymentOwnedKeys?.Contains(mapping.Key) == true)
                {
                    continue;
                }
                overrides[mapping.Key] = mapping.Value;
            }
        }

        return overrides;
    }

    public static IEnumerable<KeyValuePair<string, string?>> MapEnvVarToConfiguration(string envKey, string? envValue)
    {
        if (string.IsNullOrWhiteSpace(envKey) || IgnoredComposeOnlyKeys.Contains(envKey))
        {
            yield break;
        }

        if (envKey.Contains("__", StringComparison.Ordinal))
        {
            yield return new KeyValuePair<string, string?>(envKey.Replace("__", ":"), envValue);
            yield break;
        }

        if (SharedBackendKeyMappings.TryGetValue(envKey, out var sharedKeys))
        {
            foreach (var sharedKey in sharedKeys)
            {
                yield return new KeyValuePair<string, string?>(sharedKey, envValue);
            }

            yield break;
        }

        if (ExactKeyMappings.TryGetValue(envKey, out var configKeys))
        {
            foreach (var configKey in configKeys)
            {
                yield return new KeyValuePair<string, string?>(configKey, envValue);
            }
        }
    }

    private static string StripQuotes(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
        {
            return value[1..^1];
        }

        return value;
    }

    private static string? FirstNonEmptyEnvironmentValue(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? ReadDotEnvValue(string path, params string[] keys)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var accepted = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? selected = null;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith("export ", StringComparison.Ordinal))
            {
                trimmed = trimmed[7..].TrimStart();
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0 || !accepted.Contains(trimmed[..separator].Trim()))
            {
                continue;
            }

            selected = StripQuotes(trimmed[(separator + 1)..].Trim());
        }

        return string.IsNullOrWhiteSpace(selected) ? null : selected.Trim();
    }
}
