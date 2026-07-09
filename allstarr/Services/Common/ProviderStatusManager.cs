using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Services.SquidWTF;

namespace allstarr.Services.Common;

public class ProviderStatusManager
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProviderStatusManager> _logger;
    private readonly SpotifyApiSettings _spotifySettings;
    private readonly AppleMusicSettings _appleMusicSettings;
    private readonly DeezerSettings _deezerSettings;
    private readonly QobuzSettings _qobuzSettings;
    private readonly SquidWTFSettings _squidWtfSettings;
    private readonly SquidWtfEndpointCatalog _squidWtfCatalog;

    private readonly ConcurrentDictionary<string, (bool IsHealthy, DateTime TestedAt)> _statusCache = new();

    public ProviderStatusManager(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<ProviderStatusManager> logger,
        IOptions<SpotifyApiSettings> spotifySettings,
        IOptions<AppleMusicSettings> appleMusicSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings,
        IOptions<SquidWTFSettings> squidWtfSettings,
        SquidWtfEndpointCatalog squidWtfCatalog)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _spotifySettings = spotifySettings.Value;
        _appleMusicSettings = appleMusicSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _squidWtfSettings = squidWtfSettings.Value;
        _squidWtfCatalog = squidWtfCatalog;
    }

    public IReadOnlyList<string> GetEnabledSearchProviders()
    {
        var order = GetMetadataOrder();
        var enabled = GetEnabledSearchRaw();

        return order
            .Where(p => enabled.Contains(p) && IsProviderHealthy(p))
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledPlaylistProviders()
    {
        var order = GetPlaylistOrder();
        var enabled = GetEnabledPlaylistRaw();

        return order
            .Where(p => enabled.Contains(p) && IsProviderHealthy(p))
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledDownloadProviders()
    {
        var order = GetDownloadOrder();
        return order
            .Where(p => IsProviderHealthy(p))
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledStreamingProviders()
    {
        var order = GetStreamingOrder();
        return order
            .Where(p => IsProviderHealthy(p))
            .ToList();
    }

    public IReadOnlyList<string> GetEnabledLyricsProviders()
    {
        return GetLyricsOrder();
    }

    public bool IsProviderHealthy(string provider)
    {
        var prov = provider.ToLowerInvariant();
        if (!_statusCache.TryGetValue(prov, out var cache))
        {
            // If never tested, default to healthy but trigger async check in background
            _statusCache[prov] = (true, DateTime.UtcNow);
            _ = Task.Run(() => TestProviderConnectionAsync(prov));
            return true;
        }

        // Cache lifetime 5 minutes
        if (DateTime.UtcNow - cache.TestedAt > TimeSpan.FromMinutes(5))
        {
            _ = Task.Run(() => TestProviderConnectionAsync(prov));
        }

        return cache.IsHealthy;
    }

    public async Task<bool> TestProviderConnectionAsync(string provider, CancellationToken cancellationToken = default)
    {
        var prov = provider.ToLowerInvariant();
        bool isHealthy = false;

        _logger.LogDebug("Testing connectivity for provider: {Provider}", prov);

        try
        {
            isHealthy = prov switch
            {
                "spotify" => await TestSpotifyAsync(cancellationToken),
                "applemusic" => await TestAppleMusicAsync(cancellationToken),
                "deezer" => await TestDeezerAsync(cancellationToken),
                "qobuz" => await TestQobuzAsync(cancellationToken),
                "squidwtf" => await TestSquidWtfAsync(cancellationToken),
                _ => false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed for provider {Provider}", prov);
            isHealthy = false;
        }

        _statusCache[prov] = (isHealthy, DateTime.UtcNow);
        _logger.LogInformation("Provider health check result: {Provider} => healthy={Healthy}", prov, isHealthy);
        return isHealthy;
    }

    public IReadOnlyDictionary<string, (bool IsHealthy, DateTime TestedAt)> GetStatusCache()
    {
        return _statusCache.ToDictionary(k => k.Key, v => v.Value);
    }

    private List<string> GetMetadataOrder()
    {
        var val = _configuration["MULTI_PROVIDER_METADATA_ORDER"] ?? "spotify,applemusic,deezer,qobuz,squidwtf";
        return val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToList();
    }

    private List<string> GetDownloadOrder()
    {
        return GetProviderOrder("MULTI_PROVIDER_DOWNLOAD_ORDER", "applemusic,deezer,qobuz,squidwtf");
    }

    private List<string> GetStreamingOrder()
    {
        return GetProviderOrder("MULTI_PROVIDER_STREAMING_ORDER", "applemusic,deezer,qobuz,squidwtf");
    }

    private List<string> GetPlaylistOrder()
    {
        return GetProviderOrder("MULTI_PROVIDER_PLAYLIST_ORDER", "spotify,applemusic,deezer,qobuz,squidwtf");
    }

    private List<string> GetLyricsOrder()
    {
        return GetProviderOrder("MULTI_PROVIDER_LYRICS_ORDER", "spotify,lyricsplus,lrclib");
    }

    private List<string> GetProviderOrder(string key, string fallback)
    {
        var val = _configuration[key] ?? fallback;
        return val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToList();
    }

    private HashSet<string> GetEnabledSearchRaw()
    {
        var val = _configuration["MULTI_PROVIDER_ENABLED_SEARCH"] ?? "spotify,applemusic,deezer,qobuz,squidwtf";
        return val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();
    }

    private HashSet<string> GetEnabledPlaylistRaw()
    {
        var val = _configuration["MULTI_PROVIDER_ENABLED_PLAYLIST"] ?? "spotify";
        return val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();
    }

    private async Task<bool> TestSpotifyAsync(CancellationToken cancellationToken)
    {
        var cookie = _spotifySettings.SessionCookie;
        if (string.IsNullOrWhiteSpace(cookie)) return false;

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://open.spotify.com/");
        request.Headers.Add("Cookie", $"sp_dc={cookie}");
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        var response = await client.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> TestAppleMusicAsync(CancellationToken cancellationToken)
    {
        var baseUrl = _appleMusicSettings.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

        var response = await client.GetAsync("api/health", cancellationToken);
        if (!response.IsSuccessStatusCode) return false;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("staged", out var staged) &&
            doc.RootElement.TryGetProperty("logged_in", out var loggedIn))
        {
            return staged.GetBoolean() && loggedIn.GetBoolean();
        }

        return false;
    }

    private async Task<bool> TestDeezerAsync(CancellationToken cancellationToken)
    {
        var arl = _deezerSettings.Arl;
        if (string.IsNullOrWhiteSpace(arl)) return false;

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://www.deezer.com/ajax/gw-light.php?method=deezer.getUserData&input=3&api_version=1.0&api_token=null");
        request.Headers.Add("Cookie", $"arl={arl}");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return false;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("results", out var results) &&
            results.TryGetProperty("USER", out var user) &&
            user.TryGetProperty("USER_ID", out var userId))
        {
            var idVal = userId.ValueKind == JsonValueKind.Number ? userId.GetInt64() : 0;
            return idVal > 0;
        }

        return false;
    }

    private async Task<bool> TestQobuzAsync(CancellationToken cancellationToken)
    {
        var token = _qobuzSettings.UserAuthToken;
        var userId = _qobuzSettings.UserId;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userId)) return false;

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        var appId = "798273057";
        var apiUrl = $"https://www.qobuz.com/api.json/0.2/favorite/getUserFavorites?user_id={userId}&app_id={appId}";

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Add("X-App-Id", appId);
        request.Headers.Add("X-User-Auth-Token", token);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        var response = await client.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> TestSquidWtfAsync(CancellationToken cancellationToken)
    {
        var apiUrls = _squidWtfCatalog.ApiUrls;
        if (apiUrls == null || apiUrls.Count == 0) return false;

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        foreach (var url in apiUrls)
        {
            try
            {
                var response = await client.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode) return true;
            }
            catch
            {
                // Continue to next URL
            }
        }

        return false;
    }
}
