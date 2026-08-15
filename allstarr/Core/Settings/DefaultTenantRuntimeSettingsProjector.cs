using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Capabilities;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Settings;

public sealed class DefaultTenantRuntimeSettingsProjector : BackgroundService
{
    private readonly IDurableRuntimeSettings _settings;
    private readonly IRuntimeSettingsChangeSignal _signal;
    private readonly Guid _tenantId;
    private readonly IConfiguration _configuration;
    private readonly CacheSettings _cache;
    private readonly DeezerSettings _deezer;
    private readonly QobuzSettings _qobuz;
    private readonly AppleDownloadSettings _apple;
    private readonly SpotifyApiSettings _spotifyApi;
    private readonly SpotifyImportSettings _spotifyImport;
    private readonly MusicBrainzSettings _musicBrainz;
    private readonly ScrobblingSettings _scrobbling;
    private readonly JellyfinSettings _jellyfin;
    private readonly SubsonicSettings _subsonic;
    private readonly TrackMatchPolicy _matching;
    private readonly ILogger<DefaultTenantRuntimeSettingsProjector> _logger;
    private readonly string? _bootstrapAppleBaseUrl;
    private readonly SemaphoreSlim _refresh = new(0, 1);

    public DefaultTenantRuntimeSettingsProjector(
        IDurableRuntimeSettings settings, IRuntimeSettingsChangeSignal signal, IdentityOptions identity,
        IConfiguration configuration, IOptions<CacheSettings> cache, IOptions<DeezerSettings> deezer,
        IOptions<QobuzSettings> qobuz, IOptions<AppleDownloadSettings> apple,
        IOptions<SpotifyApiSettings> spotifyApi, IOptions<SpotifyImportSettings> spotifyImport,
        IOptions<MusicBrainzSettings> musicBrainz, IOptions<ScrobblingSettings> scrobbling,
        IOptions<JellyfinSettings> jellyfin, IOptions<SubsonicSettings> subsonic,
        TrackMatchPolicy matching,
        ILogger<DefaultTenantRuntimeSettingsProjector> logger)
    {
        (_settings, _signal, _configuration, _logger) = (settings, signal, configuration, logger);
        _bootstrapAppleBaseUrl = configuration["AppleDownload:BaseUrl"];
        _tenantId = identity.GetDefaultTenantId();
        (_cache, _deezer, _qobuz, _apple) = (cache.Value, deezer.Value, qobuz.Value, apple.Value);
        (_spotifyApi, _spotifyImport, _musicBrainz, _scrobbling) =
            (spotifyApi.Value, spotifyImport.Value, musicBrainz.Value, scrobbling.Value);
        (_jellyfin, _subsonic) = (jellyfin.Value, subsonic.Value);
        _matching = matching;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _signal.Changed += OnChanged;
        await ProjectAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _signal.Changed -= OnChanged;
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _refresh.WaitAsync(stoppingToken);
                await ProjectAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void OnChanged(long _)
    {
        try { _refresh.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task ProjectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var values = await _settings.GetManyAsync(_tenantId, RuntimeSettingCatalog.Definitions.Keys, cancellationToken);
            foreach (var setting in values.Values.Where(item =>
                         item.Origin == RuntimeSettingOrigin.Durable && item.Key != AudioQualityPolicy.SettingKey))
                Apply(setting);

            var audio = values[AudioQualityPolicy.SettingKey];
            if (audio.Origin == RuntimeSettingOrigin.Durable || _configuration[AudioQualityPolicy.SettingKey] != null)
            {
                ApplyAudioQuality((string)audio.Value);
            }
            else if (LegacyQualityIsDurable(values))
            {
                var migrated = AudioQualityPolicy.FromProviderCeilings(_apple.Quality, _deezer.Quality, _qobuz.Quality);
                await _settings.ApplyBatchAsync(_tenantId,
                    [new RuntimeSettingWrite(AudioQualityPolicy.SettingKey, migrated)],
                    "audio-quality-migration", cancellationToken: cancellationToken);
                ApplyAudioQuality(migrated);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to project durable runtime settings for the default tenant");
        }
    }

    private static bool LegacyQualityIsDurable(IReadOnlyDictionary<string, EffectiveRuntimeSetting> values) =>
        values["AppleDownload:Quality"].Origin == RuntimeSettingOrigin.Durable ||
        values["Deezer:Quality"].Origin == RuntimeSettingOrigin.Durable ||
        values["Qobuz:Quality"].Origin == RuntimeSettingOrigin.Durable;

    private void ApplyAudioQuality(string step)
    {
        var quality = AudioQualityPolicy.ProviderCeilings(step);
        (_apple.Quality, _deezer.Quality, _qobuz.Quality) = (quality.Apple, quality.Deezer, quality.Qobuz);
    }

    private void Apply(EffectiveRuntimeSetting setting)
    {
        var value = setting.Value;
        switch (setting.Key)
        {
            case "Cache:SearchResultsMinutes": _cache.SearchResultsMinutes = (int)value; break;
            case "Cache:PlaylistImagesHours": _cache.PlaylistImagesHours = (int)value; break;
            case "Cache:LyricsDays": _cache.LyricsDays = (int)value; break;
            case "Cache:GenreDays": _cache.GenreDays = (int)value; break;
            case "Cache:MetadataDays": _cache.MetadataDays = (int)value; break;
            case "Cache:OdesliLookupDays": _cache.OdesliLookupDays = (int)value; break;
            case "Cache:ProxyImagesDays": _cache.ProxyImagesDays = (int)value; break;
            case "Cache:TranscodeCacheMinutes": _cache.TranscodeCacheMinutes = (int)value; break;
            case "Deezer:Quality": _deezer.Quality = (string)value; break;
            case "Deezer:MinRequestIntervalMs": _deezer.MinRequestIntervalMs = (int)value; break;
            case "Qobuz:Quality": _qobuz.Quality = (string)value; break;
            case "Qobuz:MinRequestIntervalMs": _qobuz.MinRequestIntervalMs = (int)value; break;
            case "AppleDownload:BaseUrl":
                if (string.IsNullOrWhiteSpace(_bootstrapAppleBaseUrl)) _apple.BaseUrl = (string)value;
                break;
            case "AppleDownload:Quality": _apple.Quality = (string)value; break;
            case "MusicBrainz:Enabled": _musicBrainz.Enabled = (bool)value; break;
            case "SpotifyApi:Enabled": _spotifyApi.Enabled = (bool)value; break;
            case "SpotifyApi:CacheDurationMinutes": _spotifyApi.CacheDurationMinutes = (int)value; break;
            case "SpotifyApi:RateLimitDelayMs": _spotifyApi.RateLimitDelayMs = (int)value; break;
            case "SpotifyApi:LyricsApiUrl": _spotifyApi.LyricsApiUrl = (string)value; break;
            case "SpotifyApi:PreferIsrcMatching": _spotifyApi.PreferIsrcMatching = (bool)value; break;
            case "SpotifyImport:Enabled": _spotifyImport.Enabled = (bool)value; break;
            case "SpotifyImport:MatchingIntervalHours": _spotifyImport.MatchingIntervalHours = (int)value; break;
            case "SpotifyImport:Playlists": _spotifyImport.Playlists = SpotifyPlaylistConfigParser.Parse((string)value); break;
            case "Scrobbling:Enabled": _scrobbling.Enabled = (bool)value; break;
            case "Scrobbling:LocalTracksEnabled": _scrobbling.LocalTracksEnabled = (bool)value; break;
            case "Scrobbling:SyntheticLocalPlayedSignalEnabled": _scrobbling.SyntheticLocalPlayedSignalEnabled = (bool)value; break;
            case "Scrobbling:LastFm:Enabled": _scrobbling.LastFm.Enabled = (bool)value; break;
            case "Scrobbling:ListenBrainz:Enabled": _scrobbling.ListenBrainz.Enabled = (bool)value; break;
            case "Library:EnableExternalPlaylists": SetBoth(item => item.EnableExternalPlaylists = (bool)value, item => item.EnableExternalPlaylists = (bool)value); break;
            case "Matching:LocalPreferencePercent": _matching.LocalPreferenceBoost = (int)value / 100d; break;
            case "Matching:ExtensionPenaltyPercent": _matching.ExtensionPreferencePenalty = (int)value / 100d; break;
            case "Library:PlaylistsDirectory": SetBoth(item => item.PlaylistsDirectory = (string)value, item => item.PlaylistsDirectory = (string)value); break;
            case "Library:ExplicitFilter": SetBoth(item => item.ExplicitFilter = Enum.Parse<ExplicitFilter>((string)value), item => item.ExplicitFilter = Enum.Parse<ExplicitFilter>((string)value)); break;
            case "Library:DownloadMode": SetBoth(item => item.DownloadMode = Enum.Parse<DownloadMode>((string)value), item => item.DownloadMode = Enum.Parse<DownloadMode>((string)value)); break;
            case "Library:StorageMode": SetBoth(item => item.StorageMode = Enum.Parse<StorageMode>((string)value), item => item.StorageMode = Enum.Parse<StorageMode>((string)value)); break;
            case "Library:CacheDurationHours": SetBoth(item => item.CacheDurationHours = (int)value, item => item.CacheDurationHours = (int)value); break;
            case "Providers:MetadataOrder": SetRouting("MULTI_PROVIDER_METADATA_ORDER", setting.NormalizedValue); break;
            case "Providers:DownloadOrder": SetRouting("MULTI_PROVIDER_DOWNLOAD_ORDER", setting.NormalizedValue); break;
            case "Providers:StreamingOrder": SetRouting("MULTI_PROVIDER_STREAMING_ORDER", setting.NormalizedValue); break;
            case "Providers:PlaylistOrder": SetRouting("MULTI_PROVIDER_PLAYLIST_ORDER", setting.NormalizedValue); break;
            case "Providers:LyricsOrder": SetRouting("MULTI_PROVIDER_LYRICS_ORDER", setting.NormalizedValue); break;
            case "Providers:EnabledSearch": SetRouting("MULTI_PROVIDER_ENABLED_SEARCH", setting.NormalizedValue); break;
            case "Providers:EnabledPlaylist": SetRouting("MULTI_PROVIDER_ENABLED_PLAYLIST", setting.NormalizedValue); break;
            case "Providers:Disabled": SetRouting("MULTI_PROVIDER_DISABLED_PROVIDERS", setting.NormalizedValue); break;
        }
    }

    private void SetBoth(Action<JellyfinSettings> jellyfin, Action<SubsonicSettings> subsonic) { jellyfin(_jellyfin); subsonic(_subsonic); }
    private void SetRouting(string key, string value) => _configuration[key] = value;
}
