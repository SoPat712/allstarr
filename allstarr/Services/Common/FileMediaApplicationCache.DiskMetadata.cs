using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Playlists;

namespace allstarr.Services.Common;

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

    internal Task<string[]> RecentMetadataKeysAsync(IReadOnlySet<Guid> listeners, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(ReadAllMetadata().Where(item => item.ListenerId.HasValue && listeners.Contains(item.ListenerId.Value) && item.ExpiresAt > _clock.UtcNow &&
                ApplicationCachePayloadPolicy.IsRestartSafe(item.Key))
                .OrderByDescending(item => item.LastAccessAt).GroupBy(item => item.ListenerId)
                .SelectMany(group => group.Take(32)).Select(item => item.Key).ToArray());
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            _logger.LogDebug("Metadata cache warm-up skipped a read ({ErrorType})", error.GetType().Name);
            return Task.FromResult(Array.Empty<string>());
        }
    }
}

public sealed partial class HybridApplicationCache
{
    private async Task<string?> RefillAsync(string key, bool lowPriority = false)
    {
        var retained = await media.ReadEntryAsync(key, lowPriority);
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
            if (IsCategoryEnabled(key) && !await metadata.ExistsAsync(key)) await RefillAsync(key, lowPriority: true);
            await Task.Delay(TimeSpan.FromMilliseconds(25), token);
        }
    }
}
