using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Playlists;

namespace allstarr.Core.Caching;

public sealed partial class FileMediaApplicationCache
{
    private string MetadataRoot => _options.MetadataRootPath ?? _options.RootPath;
    private IEnumerable<string> CacheRoots => new[] { _options.RootPath, MetadataRoot }.Distinct(StringComparer.Ordinal);
    private IEnumerable<string> CacheFiles(string pattern) => CacheRoots.Where(Directory.Exists)
        .SelectMany(root => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)).Distinct(StringComparer.Ordinal);
    private static string PayloadHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool CanPersistMetadata(string value)
    {
        try
        {
            var json = value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('[')
                ? value : JsonSerializer.Serialize(value);
            PersistenceGuard.ValidateSafeJson(json, nameof(value));
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("Body", out var body) && body.ValueKind == JsonValueKind.String)
                PersistenceGuard.ValidateSafeJson(body.GetString()!, nameof(value));
            return !new[] { "token=", "api_key=", "apikey=", "signature=", "sig=", "X-Emby-Token=", "X-MediaBrowser-Token=" }
                .Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return false; }
    }

    internal async Task<string[]> RecentMetadataKeysAsync(IReadOnlySet<Guid> listeners, CancellationToken token)
    {
        var recent = new List<MediaEntryMetadata>();
        try
        {
            foreach (var path in CacheFiles("*.json").Take(_options.MaximumCleanupFiles))
            {
                token.ThrowIfCancellationRequested();
                if (!await _gate.WaitAsync(0, token)) break;
                try
                {
                    var item = await ReadMetadataAsync(path);
                    if (item != null && ApplicationCachePayloadPolicy.IsValidKey(item.Key)) recent.Add(item);
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                finally { _gate.Release(); }
                await Task.Delay(1, token);
            }
            return recent.Where(item => item.ListenerId.HasValue && listeners.Contains(item.ListenerId.Value) && item.ExpiresAt > _clock.UtcNow &&
                ApplicationCachePayloadPolicy.IsRestartSafe(item.Key))
                .OrderByDescending(item => item.LastAccessAt).GroupBy(item => item.ListenerId)
                .SelectMany(group => group.Take(32)).Select(item => item.Key).ToArray();
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            _logger.LogDebug("Metadata cache warm-up skipped a read ({ErrorType})", error.GetType().Name);
            return [];
        }
    }
}

public sealed partial class HybridApplicationCache
{
    private async Task<string?> RefillAsync(string key)
    {
        var retained = await media.ReadEntryAsync(key);
        if (retained is not { Remaining: var remaining } || remaining <= TimeSpan.Zero) return null;
        await metadata.SetStringAsync(key, retained.Value.Value, remaining);
        return retained.Value.Value;
    }

    internal async Task WarmRecentAsync(IReadOnlySet<Guid> listeners, CancellationToken token)
    {
        if (listeners.Count == 0) return;
        foreach (var key in await media.RecentMetadataKeysAsync(listeners, token))
        {
            token.ThrowIfCancellationRequested();
            if (IsCategoryEnabled(key) && !await metadata.ExistsAsync(key)) await RefillAsync(key);
            await Task.Delay(TimeSpan.FromMilliseconds(25), token);
        }
    }
}
