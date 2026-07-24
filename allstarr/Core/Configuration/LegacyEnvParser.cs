using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace allstarr.Core.Configuration;

public enum LegacyEnvDisposition
{
    DurableSetting,
    ProviderAccount,
    DeploymentChecklist,
    PerUserManual,
    PlaylistHandoff,
    IgnoredDeprecated,
    Unknown
}

public sealed record LegacyEnvEntry(
    string Key,
    string Value,
    int LineNumber,
    LegacyEnvDisposition Disposition,
    string Action,
    string Reason,
    bool Sensitive,
    string? DurableKey = null,
    string? ProviderId = null,
    IReadOnlyList<int>? OverriddenLineNumbers = null);

public sealed record LegacyPlaylistHandoff(
    string Name,
    string SourcePlaylistId,
    string JellyfinTargetPlaylistId,
    string LocalTracksPosition,
    string SyncSchedule,
    bool HasLegacyOwner,
    string Action = "requires_target_selection");

public sealed record LegacyEnvDocument(
    string SourceSha256,
    IReadOnlyList<LegacyEnvEntry> Entries,
    IReadOnlyList<LegacyPlaylistHandoff> Playlists);

public sealed class LegacyEnvParseException(string message) : Exception(message);

public static class LegacyEnvParser
{
    public const string ParserVersion = "legacy-env-v2";
    public const int MaxBytes = 1024 * 1024;
    private const int MaxEntries = 1000;
    private const int MaxValueCharacters = 64 * 1024;

    private static readonly IReadOnlyDictionary<string, string> DurableAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EXPLICIT_FILTER"] = "Library:ExplicitFilter",
            ["DOWNLOAD_MODE"] = "Library:DownloadMode",
            ["STORAGE_MODE"] = "Library:StorageMode",
            ["CACHE_DURATION_HOURS"] = "Library:CacheDurationHours",
            ["ENABLE_EXTERNAL_PLAYLISTS"] = "Library:EnableExternalPlaylists",
            ["PLAYLISTS_DIRECTORY"] = "Library:PlaylistsDirectory",
            ["SPOTIFY_IMPORT_ENABLED"] = "SpotifyImport:Enabled",
            ["SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS"] = "SpotifyImport:MatchingIntervalHours",
            ["SPOTIFY_API_ENABLED"] = "SpotifyApi:Enabled",
            ["SPOTIFY_API_CACHE_DURATION_MINUTES"] = "SpotifyApi:CacheDurationMinutes",
            ["SPOTIFY_API_RATE_LIMIT_DELAY_MS"] = "SpotifyApi:RateLimitDelayMs",
            ["SPOTIFY_API_PREFER_ISRC_MATCHING"] = "SpotifyApi:PreferIsrcMatching",
            ["SPOTIFY_LYRICS_API_URL"] = "SpotifyApi:LyricsApiUrl",
            ["SCROBBLING_ENABLED"] = "Scrobbling:Enabled",
            ["SCROBBLING_LOCAL_TRACKS_ENABLED"] = "Scrobbling:LocalTracksEnabled",
            ["SCROBBLING_SYNTHETIC_LOCAL_PLAYED_SIGNAL_ENABLED"] = "Scrobbling:SyntheticLocalPlayedSignalEnabled",
            ["SCROBBLING_LASTFM_ENABLED"] = "Scrobbling:LastFm:Enabled",
            ["SCROBBLING_LISTENBRAINZ_ENABLED"] = "Scrobbling:ListenBrainz:Enabled",
            ["DEEZER_QUALITY"] = "Deezer:Quality",
            ["DEEZER_MIN_REQUEST_INTERVAL_MS"] = "Deezer:MinRequestIntervalMs",
            ["QOBUZ_QUALITY"] = "Qobuz:Quality",
            ["QOBUZ_MIN_REQUEST_INTERVAL_MS"] = "Qobuz:MinRequestIntervalMs",
            ["SQUIDWTF_QUALITY"] = "SquidWTF:Quality",
            ["SQUIDWTF_MIN_REQUEST_INTERVAL_MS"] = "SquidWTF:MinRequestIntervalMs",
            ["APPLE_DOWNLOAD_URL"] = "AppleDownload:BaseUrl",
            ["APPLE_DOWNLOAD_QUALITY"] = "AppleDownload:Quality",
            ["APPLE_MUSIC_AIO_URL"] = "AppleDownload:BaseUrl",
            ["APPLE_MUSIC_QUALITY"] = "AppleDownload:Quality",
            ["MUSICBRAINZ_ENABLED"] = "MusicBrainz:Enabled",
            ["MULTI_PROVIDER_METADATA_ORDER"] = "Providers:MetadataOrder",
            ["MULTI_PROVIDER_DOWNLOAD_ORDER"] = "Providers:DownloadOrder",
            ["MULTI_PROVIDER_STREAMING_ORDER"] = "Providers:StreamingOrder",
            ["MULTI_PROVIDER_PLAYLIST_ORDER"] = "Providers:PlaylistOrder",
            ["MULTI_PROVIDER_LYRICS_ORDER"] = "Providers:LyricsOrder",
            ["MULTI_PROVIDER_ENABLED_SEARCH"] = "Providers:EnabledSearch",
            ["MULTI_PROVIDER_ENABLED_PLAYLIST"] = "Providers:EnabledPlaylist",
            ["MULTI_PROVIDER_DISABLED_PROVIDERS"] = "Providers:Disabled",
            ["CACHE_SEARCH_RESULTS_MINUTES"] = "Cache:SearchResultsMinutes",
            ["CACHE_PLAYLIST_IMAGES_HOURS"] = "Cache:PlaylistImagesHours",
            ["CACHE_SPOTIFY_PLAYLIST_ITEMS_HOURS"] = "Cache:SpotifyPlaylistItemsHours",
            ["CACHE_SPOTIFY_MATCHED_TRACKS_DAYS"] = "Cache:SpotifyMatchedTracksDays",
            ["CACHE_LYRICS_DAYS"] = "Cache:LyricsDays",
            ["CACHE_GENRE_DAYS"] = "Cache:GenreDays",
            ["CACHE_METADATA_DAYS"] = "Cache:MetadataDays",
            ["CACHE_ODESLI_LOOKUP_DAYS"] = "Cache:OdesliLookupDays",
            ["CACHE_PROXY_IMAGES_DAYS"] = "Cache:ProxyImagesDays",
            ["CACHE_MEDIA_DIRECTORY"] = "Cache:MediaDirectory",
            ["CACHE_MEDIA_MAXIMUM_MEGABYTES"] = "Cache:MediaMaximumMegabytes",
            ["CACHE_MEDIA_MAXIMUM_ENTRY_MEGABYTES"] = "Cache:MediaMaximumEntryMegabytes",
            ["CACHE_MEDIA_CLEANUP_FILE_LIMIT"] = "Cache:MediaCleanupFileLimit",
            ["CACHE_TRANSCODE_MINUTES"] = "Cache:TranscodeCacheMinutes"
        };

    private static readonly IReadOnlyDictionary<string, string> ProviderSecrets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPOTIFY_API_SESSION_COOKIE"] = "spotify",
            ["SPOTIFY_API_SESSION_COOKIE_SET_DATE"] = "spotify",
            ["DEEZER_ARL"] = "deezer",
            ["DEEZER_ARL_FALLBACK"] = "deezer",
            ["QOBUZ_USER_AUTH_TOKEN"] = "qobuz",
            ["QOBUZ_USER_ID"] = "qobuz"
        };

    private static readonly HashSet<string> PersonalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SPOTIFY_API_SESSION_COOKIES",
        "SPOTIFY_API_SESSION_COOKIE_SET_DATES",
        "SCROBBLING_LASTFM_API_KEY",
        "SCROBBLING_LASTFM_SHARED_SECRET",
        "SCROBBLING_LASTFM_SESSION_KEY",
        "SCROBBLING_LASTFM_USERNAME",
        "SCROBBLING_LASTFM_PASSWORD",
        "SCROBBLING_LISTENBRAINZ_USER_TOKEN"
    };

    private static readonly HashSet<string> SensitiveDeploymentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "JELLYFIN_API_KEY",
        "ALLSTARR_STORAGE_CONNECTION_STRING",
        "REDIS_CONNECTION_STRING",
        "MUSICBRAINZ_PASSWORD"
    };

    private static readonly HashSet<string> DeploymentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD_FILE", "ALLSTARR_KEYRING_FILE",
        "STORAGE_AUTO_MIGRATE", "ALLSTARR_IMAGE", "PROXY_BIND_ADDRESS", "PROXY_PORT",
        "ADMIN_BIND_ADDRESS", "ADMIN_PORT", "VALKEY_MAX_MEMORY", "ADMIN__ENABLE_ENV_EXPORT",
        "CORS__ALLOWED_ORIGINS", "CORS__ALLOWED_METHODS", "CORS__ALLOWED_HEADERS", "CORS__ALLOW_CREDENTIALS",
        "BACKEND_TYPE", "ADMIN_BIND_ANY_IP", "ADMIN_TRUSTED_SUBNETS", "ADMIN_ENABLE_ENV_EXPORT",
        "CORS_ALLOWED_ORIGINS", "CORS_ALLOWED_METHODS", "CORS_ALLOWED_HEADERS", "CORS_ALLOW_CREDENTIALS",
        "SUBSONIC_URL", "JELLYFIN_URL", "JELLYFIN_API_KEY", "JELLYFIN_USER_ID", "JELLYFIN_CLIENT_USERNAME",
        "JELLYFIN_LIBRARY_ID", "ALLSTARR_STORAGE_PROVIDER", "ALLSTARR_STORAGE_CONNECTION_STRING",
        "ALLSTARR_STORAGE_PASSWORD_FILE", "ALLSTARR_STORAGE_AUTO_MIGRATE",
        "ALLSTARR_STORAGE_ENFORCE_MUTATION_GUARD", "ALLSTARR_STORAGE_SQLITE_BOOTSTRAP_CONFIRMATION_FILE",
        "ALLSTARR_BACKUP_DIRECTORY", "ALLSTARR_SECRET_KEY_RING_PATH", "ALLSTARR_MULTI_USER_MODE",
        "ALLSTARR_BACKEND_INSTANCE_ID", "ALLSTARR_PROVIDER_ACCOUNT_MANAGEMENT_MODE",
        "ALLSTARR_ALLOW_GLOBAL_ACCOUNTS", "ALLSTARR_ALLOW_GLOBAL_PERSONAL_ACCOUNTS",
        "ALLSTARR_SHARED_DOWNLOADER_ACCOUNT_ID", "LIBRARY_DOWNLOAD_PATH", "LIBRARY_KEPT_PATH",
        "DOWNLOAD_PATH", "KEPT_PATH", "CACHE_PATH", "REDIS_ENABLED", "REDIS_CONNECTION_STRING",
        "DEBUG_LOG_ALL_REQUESTS", "DEBUG_REDACT_SENSITIVE_REQUEST_VALUES", "MUSICBRAINZ_USERNAME",
        "MUSICBRAINZ_PASSWORD"
    };

    private static readonly HashSet<string> IgnoredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "MUSIC_SERVICE", "EXTENSION_REPOSITORIES", "REDIS_DATA_PATH", "REDIS_HOST", "REDIS_PORT",
        "REDIS_USERNAME", "REDIS_PASSWORD", "REDIS_DATABASE", "REDIS_DB", "REDIS_SSL",
        "SPOTIFY_IMPORT_SYNC_START_HOUR", "SPOTIFY_IMPORT_SYNC_START_MINUTE",
        "SPOTIFY_IMPORT_SYNC_WINDOW_HOURS", "SPOTIFY_IMPORT_PLAYLIST_IDS",
        "SPOTIFY_IMPORT_PLAYLIST_NAMES", "SPOTIFY_IMPORT_PLAYLIST_LOCAL_TRACKS_POSITIONS",
        "REDIS2VALKEY_ENABLED", "REDIS2VALKEY_MIGRATION", "REDIS_TO_VALKEY_MIGRATION"
    };

    public static bool TryGetDurableAlias(string legacyKey, out string durableKey) =>
        DurableAliases.TryGetValue(legacyKey, out durableKey!);

    public static IReadOnlyCollection<string> DurableAliasTargets { get; } =
        DurableAliases.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static LegacyEnvDocument Parse(ReadOnlyMemory<byte> source)
    {
        if (source.IsEmpty || source.Length > MaxBytes)
        {
            throw new LegacyEnvParseException($"The .env file must contain 1 to {MaxBytes} bytes.");
        }

        var bytes = source.ToArray();
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var entries = new List<LegacyEnvEntry>();
            var entryIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var playlists = new List<LegacyPlaylistHandoff>();
            var assignmentCount = 0;
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var raw = lines[index];
                var trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                if (trimmed.StartsWith("export ", StringComparison.Ordinal))
                {
                    trimmed = trimmed[7..].TrimStart();
                }

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                {
                    throw new LegacyEnvParseException($"Line {index + 1} is not a KEY=VALUE assignment.");
                }

                var key = trimmed[..separator].Trim();
                if (!IsValidKey(key))
                {
                    throw new LegacyEnvParseException($"Line {index + 1} contains an invalid key.");
                }

                var value = ParseValue(trimmed[(separator + 1)..], index + 1);
                if (value.Length > MaxValueCharacters)
                {
                    throw new LegacyEnvParseException($"The value on line {index + 1} is too large.");
                }

                assignmentCount++;
                if (assignmentCount > MaxEntries)
                {
                    throw new LegacyEnvParseException($"The .env file contains more than {MaxEntries} settings.");
                }

                var entry = Classify(key, value, index + 1);
                if (entryIndexes.TryGetValue(key, out var priorIndex))
                {
                    var prior = entries[priorIndex];
                    entry = entry with
                    {
                        OverriddenLineNumbers = prior.OverriddenLineNumbers is { Count: > 0 } priorLines
                            ? [.. priorLines, prior.LineNumber]
                            : [prior.LineNumber]
                    };
                    entries[priorIndex] = entry;
                }
                else
                {
                    entryIndexes.Add(key, entries.Count);
                    entries.Add(entry);
                }
            }

            if (entries.Count == 0)
            {
                throw new LegacyEnvParseException("The .env file does not contain any settings.");
            }

            ApplyProviderBundleCompleteness(entries);
            var playlistEntry = entries.SingleOrDefault(entry =>
                entry.Key.Equals("SPOTIFY_IMPORT_PLAYLISTS", StringComparison.OrdinalIgnoreCase));
            if (playlistEntry is { Value.Length: > 0 })
            {
                playlists.AddRange(ParsePlaylists(playlistEntry.Value));
            }
            return new LegacyEnvDocument(hash, entries, playlists);
        }
        catch (DecoderFallbackException)
        {
            throw new LegacyEnvParseException("The .env file must be valid UTF-8.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static LegacyEnvEntry Classify(string key, string value, int line)
    {
        if (key.Equals("SPOTIFY_IMPORT_PLAYLISTS", StringComparison.OrdinalIgnoreCase))
        {
            return new(key, value, line, LegacyEnvDisposition.DurableSetting,
                "import_if_absent",
                "Restore the legacy injected playlists and also preserve them for conversion into durable playlist links.",
                true, "SpotifyImport:Playlists");
        }

        if (IgnoredKeys.Contains(key) || key.StartsWith("SPOTIFY_IMPORT_PLAYLIST_", StringComparison.OrdinalIgnoreCase))
        {
            var reason = key.Equals("MUSIC_SERVICE", StringComparison.OrdinalIgnoreCase)
                ? "The single-provider MUSIC_SERVICE switch is deprecated; choose capability-specific provider routing after migration."
                : "This legacy value has no automatic target and is retained only for manual review.";
            return new(key, value, line, LegacyEnvDisposition.IgnoredDeprecated, "deprecated_manual_review",
                reason, LooksSensitive(key));
        }

        if (ProviderSecrets.TryGetValue(key, out var provider))
        {
            if (value.Length == 0)
            {
                return new(key, value, line, LegacyEnvDisposition.ProviderAccount, "ignore_empty",
                    "Empty legacy provider fields do not create an account.", true, ProviderId: provider);
            }

            return new(key, value, line, LegacyEnvDisposition.ProviderAccount, "create_disabled_if_missing",
                "Import into a disabled administrator-owned global provider account for explicit review.", true,
                ProviderId: provider);
        }

        if (PersonalKeys.Contains(key))
        {
            return new(key, value, line, LegacyEnvDisposition.PerUserManual, "per_user_manual",
                "Personal credentials require an explicitly selected Allstarr user and are never imported globally.", true);
        }

        if (DeploymentKeys.Contains(key))
        {
            return new(key, value, line, LegacyEnvDisposition.DeploymentChecklist, "retain_in_deployment",
                "This bootstrap or deployment value remains outside durable runtime settings.",
                SensitiveDeploymentKeys.Contains(key));
        }

        if (DurableAliases.TryGetValue(key, out var durableKey))
        {
            return new(key, value, line, LegacyEnvDisposition.DurableSetting, "import_if_absent",
                "Import into tenant-scoped durable runtime settings.", false, durableKey);
        }

        return new(key, value, line, LegacyEnvDisposition.Unknown, "manual_review",
            "No safe durable migration mapping exists for this key.", LooksSensitive(key));
    }

    private static IReadOnlyList<LegacyPlaylistHandoff> ParsePlaylists(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new LegacyEnvParseException("SPOTIFY_IMPORT_PLAYLISTS must be a JSON array.");
            }

            var result = new List<LegacyPlaylistHandoff>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array)
                {
                    throw new LegacyEnvParseException("Each legacy Spotify playlist must be a JSON array.");
                }

                var fields = item.EnumerateArray().ToArray();
                if (fields.Length < 2 || fields.Length > 6 || fields.Any(field => field.ValueKind != JsonValueKind.String))
                {
                    throw new LegacyEnvParseException("Each legacy Spotify playlist must contain 2 to 6 string fields.");
                }

                var name = fields[0].GetString()?.Trim();
                var sourceId = fields[1].GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sourceId))
                {
                    throw new LegacyEnvParseException("Legacy Spotify playlist name and source ID are required.");
                }

                var third = fields.Length > 2 ? fields[2].GetString()?.Trim() ?? string.Empty : string.Empty;
                var compact = IsLocalTracksPosition(third);
                var targetId = compact ? string.Empty : third;
                var positionIndex = compact ? 2 : 3;
                var scheduleIndex = compact ? 3 : 4;
                var ownerIndex = compact ? 4 : 5;
                var position = fields.Length > positionIndex &&
                               IsLocalTracksPosition(fields[positionIndex].GetString())
                    ? fields[positionIndex].GetString()!.Trim().ToLowerInvariant()
                    : "first";

                result.Add(new LegacyPlaylistHandoff(
                    name.Length <= 200 ? name : name[..200],
                    sourceId!,
                    targetId,
                    position,
                    fields.Length > scheduleIndex && !string.IsNullOrWhiteSpace(fields[scheduleIndex].GetString())
                        ? fields[scheduleIndex].GetString()!.Trim()
                        : "0 8 * * *",
                    fields.Length > ownerIndex && !string.IsNullOrWhiteSpace(fields[ownerIndex].GetString())));
                if (result.Count > 500)
                {
                    throw new LegacyEnvParseException("The legacy playlist list contains more than 500 entries.");
                }
            }

            return result;
        }
        catch (JsonException)
        {
            throw new LegacyEnvParseException("SPOTIFY_IMPORT_PLAYLISTS contains invalid JSON.");
        }
    }

    private static void ApplyProviderBundleCompleteness(List<LegacyEnvEntry> entries)
    {
        var byKey = entries.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        var deezerReady = HasValue(byKey, "DEEZER_ARL");
        var qobuzToken = HasValue(byKey, "QOBUZ_USER_AUTH_TOKEN");
        var qobuzUser = HasValue(byKey, "QOBUZ_USER_ID");
        var spotifyReady = HasValue(byKey, "SPOTIFY_API_SESSION_COOKIE");
        var spotifyDateValid = !HasValue(byKey, "SPOTIFY_API_SESSION_COOKIE_SET_DATE") ||
                               DateTimeOffset.TryParse(
                                   byKey["SPOTIFY_API_SESSION_COOKIE_SET_DATE"].Value,
                                   CultureInfo.InvariantCulture,
                                   DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                                   out _);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.ProviderId == "deezer" && entry.Value.Length > 0 && !deezerReady)
            {
                entries[index] = entry with
                {
                    Action = "conflict_incomplete",
                    Reason = "A Deezer import requires the primary DEEZER_ARL value; fallback alone is not imported."
                };
            }
            else if (entry.ProviderId == "qobuz" && entry.Value.Length > 0 && (!qobuzToken || !qobuzUser))
            {
                entries[index] = entry with
                {
                    Action = "conflict_incomplete",
                    Reason = "A Qobuz import requires both QOBUZ_USER_AUTH_TOKEN and QOBUZ_USER_ID."
                };
            }
            else if (entry.ProviderId == "spotify" && entry.Value.Length > 0 && !spotifyReady)
            {
                entries[index] = entry with
                {
                    Action = "conflict_incomplete",
                    Reason = "Spotify cookie metadata is imported only with SPOTIFY_API_SESSION_COOKIE."
                };
            }
            else if (entry.Key.Equals("SPOTIFY_API_SESSION_COOKIE_SET_DATE", StringComparison.OrdinalIgnoreCase) &&
                     entry.Value.Length > 0 && !spotifyDateValid)
            {
                entries[index] = entry with
                {
                    Action = "conflict_invalid_value",
                    Reason = "SPOTIFY_API_SESSION_COOKIE_SET_DATE must be a valid date and time."
                };
            }
        }
    }

    private static bool HasValue(IReadOnlyDictionary<string, LegacyEnvEntry> entries, string key) =>
        entries.TryGetValue(key, out var entry) && entry.Value.Length > 0;

    private static bool IsLocalTracksPosition(string? value) =>
        value?.Trim().Equals("first", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Trim().Equals("last", StringComparison.OrdinalIgnoreCase) == true;

    private static string ParseValue(string raw, int line)
    {
        var value = raw.Trim();
        if (value.Length < 2 || (value[0] != '\'' && value[0] != '"'))
        {
            return value;
        }

        var quote = value[0];
        if (value[^1] != quote)
        {
            throw new LegacyEnvParseException($"Line {line} contains an unterminated quoted value.");
        }

        value = value[1..^1];
        return quote == '"'
            ? value.Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\r", "\r", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal)
            : value;
    }

    private static bool IsValidKey(string key) =>
        key.Length is > 0 and <= 200 &&
        (char.IsLetter(key[0]) || key[0] == '_') &&
        key.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static bool LooksSensitive(string key) =>
        key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("COOKIE", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("CONNECTION_STRING", StringComparison.OrdinalIgnoreCase);
}
