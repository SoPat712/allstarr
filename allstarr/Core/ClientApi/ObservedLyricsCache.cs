using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Models.Lyrics;
using allstarr.Services.Common;

namespace allstarr.Core.ClientApi;

internal sealed class ObservedLyricsCache(IApplicationCache cache, Action<string> observe) : IApplicationCache
{
    public bool IsEnabled => cache.IsEnabled;
    public HashSet<string> UnavailableSources { get; } = new(StringComparer.Ordinal);
    public async Task<string?> GetStringAsync(string key)
    {
        observe(key);
        var value = await cache.GetStringAsync(key);
        if (value != null && key.StartsWith("lrclib:automatic:v2:", StringComparison.Ordinal))
        {
            try
            {
                var lyrics = JsonSerializer.Deserialize<LyricsInfo>(value, ClientApiContract.JsonOptions);
                if (string.IsNullOrWhiteSpace(lyrics?.PlainLyrics) && string.IsNullOrWhiteSpace(lyrics?.SyncedLyrics))
                    UnavailableSources.Add("lrclib");
            }
            catch (JsonException) { }
        }
        return value;
    }
    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        observe(key);
        var value = await cache.GetAsync<T>(key);
        var result = value as ProviderLyricsResult;
        if (value is SpicyLyricsCapabilityAdapter.CachedLyrics entry && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
            result = entry.Result;
        if (result != null && (result.Availability != ProviderLyricsAvailabilityState.Available || string.IsNullOrWhiteSpace(result.Content)))
        {
            var parts = key.Split(':');
            UnavailableSources.Add(parts.Length >= 3 && parts[0] == "lyrics" ? parts[2] : result.Source);
        }
        return value;
    }
    public Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null) => cache.SetStringAsync(key, value, expiry);
    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class => cache.SetAsync(key, value, expiry);
    public Task<bool> DeleteAsync(string key) => cache.DeleteAsync(key);
    public Task<bool> ExistsAsync(string key) => cache.ExistsAsync(key);
    public Task<int> DeleteByPatternAsync(string pattern) => cache.DeleteByPatternAsync(pattern);
    public Task<int> PurgeAllAsync() => cache.PurgeAllAsync();
}
