using System.Text;
using allstarr.Core.Configuration;
using allstarr.Core.Settings;

namespace allstarr.Tests;

public sealed class LegacyEnvParserTests
{
    [Fact]
    public void Parse_ClassifiesEveryKeyAndKeepsSecretsOutOfPreviewMetadata()
    {
        var document = Parse("""
            DEEZER_ARL=deezer-secret
            DEEZER_QUALITY=FLAC
            QOBUZ_USER_AUTH_TOKEN=qobuz-secret
            QOBUZ_USER_ID=42
            SPOTIFY_API_SESSION_COOKIE=spotify-secret
            SPOTIFY_API_SESSION_COOKIE_SET_DATE=2026-01-01T00:00:00Z
            JELLYFIN_URL=http://jellyfin:8096
            JELLYFIN_API_KEY=backend-secret
            SCROBBLING_LASTFM_SESSION_KEY=personal-secret
            MUSIC_SERVICE=SquidWTF
            UNKNOWN_TOKEN=unknown-secret
            """);

        Assert.Equal(11, document.Entries.Count);
        AssertEntry(document, "DEEZER_ARL", LegacyEnvDisposition.ProviderAccount, "create_disabled_if_missing", true);
        AssertEntry(document, "DEEZER_QUALITY", LegacyEnvDisposition.DurableSetting, "import_if_absent", false);
        AssertEntry(document, "JELLYFIN_URL", LegacyEnvDisposition.DeploymentChecklist, "retain_in_deployment", false);
        AssertEntry(document, "JELLYFIN_API_KEY", LegacyEnvDisposition.DeploymentChecklist, "retain_in_deployment", true);
        AssertEntry(document, "SCROBBLING_LASTFM_SESSION_KEY", LegacyEnvDisposition.PerUserManual, "per_user_manual", true);
        AssertEntry(document, "MUSIC_SERVICE", LegacyEnvDisposition.IgnoredDeprecated, "deprecated_manual_review", false);
        AssertEntry(document, "UNKNOWN_TOKEN", LegacyEnvDisposition.Unknown, "manual_review", true);
        Assert.DoesNotContain("deezer-secret", document.SourceSha256, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ImportsOptionalProviderEndpointsAsDurableSettings()
    {
        var document = Parse("""
            SPOTIFY_LYRICS_API_URL=http://spotify-lyrics:8080
            APPLE_DOWNLOAD_URL=http://apple-gateway:8000
            """);

        AssertEntry(document, "SPOTIFY_LYRICS_API_URL", LegacyEnvDisposition.DurableSetting,
            "import_if_absent", false);
        AssertEntry(document, "APPLE_DOWNLOAD_URL", LegacyEnvDisposition.DurableSetting,
            "import_if_absent", false);
        Assert.Equal("SpotifyApi:LyricsApiUrl",
            document.Entries.Single(item => item.Key == "SPOTIFY_LYRICS_API_URL").DurableKey);
        Assert.Equal("AppleDownload:BaseUrl",
            document.Entries.Single(item => item.Key == "APPLE_DOWNLOAD_URL").DurableKey);
    }

    [Fact]
    public void EveryDurableAliasTargetsARegisteredRuntimeSetting()
    {
        Assert.All(LegacyEnvParser.DurableAliasTargets, key =>
            Assert.True(RuntimeSettingCatalog.Definitions.ContainsKey(key),
                $"Legacy migration targets unsupported runtime setting '{key}'."));
    }

    [Fact]
    public void Parse_RejectsIncompleteProviderBundlesAndIgnoresEmptyValues()
    {
        var document = Parse("""
            DEEZER_ARL=
            DEEZER_ARL_FALLBACK=fallback-only
            QOBUZ_USER_AUTH_TOKEN=token-only
            SPOTIFY_API_SESSION_COOKIE=
            SPOTIFY_API_SESSION_COOKIE_SET_DATE=2026-01-01
            """);

        AssertEntry(document, "DEEZER_ARL", LegacyEnvDisposition.ProviderAccount, "ignore_empty", true);
        AssertEntry(document, "DEEZER_ARL_FALLBACK", LegacyEnvDisposition.ProviderAccount, "conflict_incomplete", true);
        AssertEntry(document, "QOBUZ_USER_AUTH_TOKEN", LegacyEnvDisposition.ProviderAccount, "conflict_incomplete", true);
        AssertEntry(document, "SPOTIFY_API_SESSION_COOKIE", LegacyEnvDisposition.ProviderAccount, "ignore_empty", true);
        AssertEntry(document, "SPOTIFY_API_SESSION_COOKIE_SET_DATE", LegacyEnvDisposition.ProviderAccount, "conflict_incomplete", true);
    }

    [Fact]
    public void Parse_PreservesPlaylistHandoffWithoutDiscardingIdentifiers()
    {
        var document = Parse("""
            SPOTIFY_IMPORT_PLAYLISTS=[["Discover Weekly","spotify-source-id","jellyfin-target-id","last","0 6 * * 1","legacy-user"]]
            """);

        var playlist = Assert.Single(document.Playlists);
        Assert.Equal("Discover Weekly", playlist.Name);
        Assert.Equal("spotify-source-id", playlist.SourcePlaylistId);
        Assert.Equal("jellyfin-target-id", playlist.JellyfinTargetPlaylistId);
        Assert.Equal("last", playlist.LocalTracksPosition);
        Assert.Equal("0 6 * * 1", playlist.SyncSchedule);
        Assert.True(playlist.HasLegacyOwner);
        Assert.Equal("requires_target_selection", playlist.Action);
    }

    [Fact]
    public void Parse_AcceptsTwoFieldAndCompactFirstLastPlaylistHandoffs()
    {
        var document = Parse("""
            SPOTIFY_IMPORT_PLAYLISTS=[["Two fields","source-1"],["Compact","source-2","last"],["Compact schedule","source-3","first","0 7 * * *","legacy-owner"]]
            """);

        Assert.Collection(document.Playlists,
            item =>
            {
                Assert.Equal("source-1", item.SourcePlaylistId);
                Assert.Equal(string.Empty, item.JellyfinTargetPlaylistId);
                Assert.Equal("first", item.LocalTracksPosition);
                Assert.False(item.HasLegacyOwner);
            },
            item =>
            {
                Assert.Equal("source-2", item.SourcePlaylistId);
                Assert.Equal(string.Empty, item.JellyfinTargetPlaylistId);
                Assert.Equal("last", item.LocalTracksPosition);
            },
            item =>
            {
                Assert.Equal("first", item.LocalTracksPosition);
                Assert.Equal("0 7 * * *", item.SyncSchedule);
                Assert.True(item.HasLegacyOwner);
            });
    }

    [Fact]
    public void Parse_UsesEveryDocumentedClassificationBucket()
    {
        var document = Parse("""
            POSTGRES_DB=allstarr
            ADMIN__ENABLE_ENV_EXPORT=false
            CORS__ALLOWED_ORIGINS=https://example.test
            VALKEY_MAX_MEMORY=512mb
            SPOTIFY_API_SESSION_COOKIES={"legacy-user":"secret"}
            EXTENSION_REPOSITORIES=https://example.test/registry.json
            SPOTIFY_IMPORT_PLAYLIST_IDS=one,two
            REDIS_DATA_PATH=./redis-data
            UNKNOWN_SETTING=value
            """);

        foreach (var key in new[] { "POSTGRES_DB", "ADMIN__ENABLE_ENV_EXPORT", "CORS__ALLOWED_ORIGINS", "VALKEY_MAX_MEMORY" })
            AssertEntry(document, key, LegacyEnvDisposition.DeploymentChecklist, "retain_in_deployment", false);
        AssertEntry(document, "SPOTIFY_API_SESSION_COOKIES", LegacyEnvDisposition.PerUserManual, "per_user_manual", true);
        foreach (var key in new[] { "EXTENSION_REPOSITORIES", "SPOTIFY_IMPORT_PLAYLIST_IDS", "REDIS_DATA_PATH" })
            AssertEntry(document, key, LegacyEnvDisposition.IgnoredDeprecated, "deprecated_manual_review", false);
        AssertEntry(document, "UNKNOWN_SETTING", LegacyEnvDisposition.Unknown, "manual_review", false);
    }

    [Fact]
    public void Parse_FlagsInvalidSpotifyCookieDate()
    {
        var document = Parse("""
            SPOTIFY_API_SESSION_COOKIE=shared-cookie
            SPOTIFY_API_SESSION_COOKIE_SET_DATE=not-a-date
            """);

        AssertEntry(document, "SPOTIFY_API_SESSION_COOKIE_SET_DATE", LegacyEnvDisposition.ProviderAccount,
            "conflict_invalid_value", true);
    }

    [Theory]
    [InlineData("NOT AN ASSIGNMENT", "KEY=VALUE")]
    [InlineData("SPOTIFY_IMPORT_PLAYLISTS=not-json", "invalid JSON")]
    [InlineData("SPOTIFY_IMPORT_PLAYLISTS=[[\"name\",\"\",\"target\"]]", "are required")]
    public void Parse_RejectsAmbiguousOrInvalidInput(string source, string expected)
    {
        var error = Assert.Throws<LegacyEnvParseException>(() => Parse(source));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_DuplicateAssignmentsUseTheLastActiveValueAndRecordSourceLines()
    {
        var document = Parse("""
            CACHE_LYRICS_DAYS=7
            SPOTIFY_IMPORT_PLAYLISTS=[["Old","old-source"]]
            # SPOTIFY_IMPORT_PLAYLISTS=[["Commented","ignored-source"]]
            CACHE_LYRICS_DAYS=30
            SPOTIFY_IMPORT_PLAYLISTS=[["Current","current-source"]]
            """);

        Assert.Equal(2, document.Entries.Count);
        var cache = Assert.Single(document.Entries, entry => entry.Key == "CACHE_LYRICS_DAYS");
        Assert.Equal("30", cache.Value);
        Assert.Equal(4, cache.LineNumber);
        Assert.Equal([1], cache.OverriddenLineNumbers);
        var playlistEntry = Assert.Single(document.Entries, entry => entry.Key == "SPOTIFY_IMPORT_PLAYLISTS");
        Assert.Equal(5, playlistEntry.LineNumber);
        Assert.Equal([2], playlistEntry.OverriddenLineNumbers);
        var playlist = Assert.Single(document.Playlists);
        Assert.Equal("Current", playlist.Name);
        Assert.Equal("current-source", playlist.SourcePlaylistId);
    }

    [Fact]
    public void Parse_AcceptsAnExactOneMegabyteDecodedSource()
    {
        var prefix = Encoding.UTF8.GetBytes("CACHE_LYRICS_DAYS=30\n");
        var source = new byte[LegacyEnvParser.MaxBytes];
        prefix.CopyTo(source, 0);
        source[prefix.Length] = (byte)'#';
        Array.Fill(source, (byte)'x', prefix.Length + 1, source.Length - prefix.Length - 1);

        var document = LegacyEnvParser.Parse(source);

        Assert.Single(document.Entries);
        Assert.Equal(64, document.SourceSha256.Length);
    }

    private static LegacyEnvDocument Parse(string source) =>
        LegacyEnvParser.Parse(Encoding.UTF8.GetBytes(source));

    private static void AssertEntry(
        LegacyEnvDocument document,
        string key,
        LegacyEnvDisposition disposition,
        string action,
        bool sensitive)
    {
        var entry = Assert.Single(document.Entries, item => item.Key == key);
        Assert.Equal(disposition, entry.Disposition);
        Assert.Equal(action, entry.Action);
        Assert.Equal(sensitive, entry.Sensitive);
    }
}
