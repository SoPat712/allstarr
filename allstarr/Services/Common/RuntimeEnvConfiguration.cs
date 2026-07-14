using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace allstarr.Services.Common;

/// <summary>
/// Loads supported flat .env keys into ASP.NET configuration so Docker/admin UI
/// updates stored in /app/.env take effect on the next application startup.
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

            ["SUBSONIC_URL"] = ["Subsonic:Url"],
            ["ALLSTARR_STORAGE_PROVIDER"] = ["Storage:Provider"],
            ["ALLSTARR_STORAGE_CONNECTION_STRING"] = ["Storage:ConnectionString"],
            ["ALLSTARR_STORAGE_PASSWORD_FILE"] = ["Storage:PasswordFile"],
            ["ALLSTARR_STORAGE_AUTO_MIGRATE"] = ["Storage:AutoMigrate"],
            ["ALLSTARR_STORAGE_ENFORCE_MUTATION_GUARD"] = ["Storage:EnforceMutationGuard"],
            ["ALLSTARR_STORAGE_SQLITE_BOOTSTRAP_CONFIRMATION_FILE"] = ["Storage:SqliteBootstrapConfirmationFile"],
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

            ["REDIS_ENABLED"] = ["Redis:Enabled"],
            ["REDIS_CONNECTION_STRING"] = ["Redis:ConnectionString"],

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
            ["CACHE_SPOTIFY_PLAYLIST_ITEMS_HOURS"] = ["Cache:SpotifyPlaylistItemsHours"],
            ["CACHE_SPOTIFY_MATCHED_TRACKS_DAYS"] = ["Cache:SpotifyMatchedTracksDays"],
            ["CACHE_LYRICS_DAYS"] = ["Cache:LyricsDays"],
            ["CACHE_GENRE_DAYS"] = ["Cache:GenreDays"],
            ["CACHE_METADATA_DAYS"] = ["Cache:MetadataDays"],
            ["CACHE_ODESLI_LOOKUP_DAYS"] = ["Cache:OdesliLookupDays"],
            ["CACHE_PROXY_IMAGES_DAYS"] = ["Cache:ProxyImagesDays"],
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
        var overrides = LoadDotEnvOverrides(envFilePath);
        if (overrides.Count == 0)
        {
            return;
        }

        configuration.AddInMemoryCollection(overrides);
    }

    public static Dictionary<string, string?> LoadDotEnvOverrides(string envFilePath)
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
}
