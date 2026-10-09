using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

public sealed record FileMediaCacheOptions(
    string RootPath,
    long MaximumBytes = 512L * 1024 * 1024,
    int MaximumEntryBytes = 16 * 1024 * 1024,
    int MaximumCleanupFiles = 10_000,
    int CleanupIntervalMinutes = 15)
{
    public static FileMediaCacheOptions FromConfiguration(IConfiguration configuration)
    {
        const long mebibyte = 1024L * 1024;
        var maximumBytes = configuration.GetValue<long?>("Cache:MediaMaximumBytes") ??
                           (configuration.GetValue<long?>("Cache:MediaMaximumMegabytes") ?? 512) * mebibyte;
        var maximumEntryBytes = configuration.GetValue<int?>("Cache:MediaMaximumEntryBytes") ??
                                checked((int)((configuration.GetValue<int?>("Cache:MediaMaximumEntryMegabytes") ?? 16) * mebibyte));
        var cleanupLimit = configuration.GetValue<int?>("Cache:MediaCleanupFileLimit") ?? 10_000;
        var cleanupIntervalMinutes = Math.Clamp(
            configuration.GetValue<int?>("Cache:MediaCleanupMinutes") ?? 15,
            1,
            24 * 60);

        maximumBytes = Math.Clamp(maximumBytes, 16 * mebibyte, 1024L * 1024 * mebibyte);
        maximumEntryBytes = Math.Clamp(
            maximumEntryBytes,
            64 * 1024,
            (int)Math.Min(maximumBytes, int.MaxValue));
        cleanupLimit = Math.Clamp(cleanupLimit, 100, 100_000);

        return new FileMediaCacheOptions(
            configuration["Cache:MediaDirectory"] ?? "/app/cache/media",
            maximumBytes,
            maximumEntryBytes,
            cleanupLimit,
            cleanupIntervalMinutes);
    }
}

public sealed record FileMediaCacheMaintenancePreview(
    int ScannedFiles,
    bool ScanLimitReached,
    int TemporaryFiles,
    int MalformedMetadataFiles,
    int OrphanedMetadataFiles,
    int OrphanedPayloadFiles,
    int ExpiredEntries,
    int NoExpiryEntries,
    int OverQuotaEntries,
    long ReclaimableBytes,
    int CleanupIntervalSeconds,
    DateTimeOffset? LastCleanupAt,
    int LastCleanupDeletedEntries,
    DateTimeOffset CapturedAt);

public sealed record UnreferencedMediaPayloadPreview(
    IReadOnlyList<string> Keys,
    long ReclaimableBytes,
    bool ScanLimitReached);

/// <summary>
/// Bounded disk cache for artwork and other reconstructable media payloads.
/// Cache keys are hashed before becoming paths and original keys live only in sidecars.
/// </summary>
public sealed class FileMediaApplicationCache : IApplicationCache, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FileMediaCacheOptions _options;
    private readonly IPlatformClock _clock;
    private readonly allstarr.Models.Settings.CacheSettings _settings;
    private readonly ILogger<FileMediaApplicationCache> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _hits;
    private long _misses;
    private long _writes;
    private long _evictions;
    private long _lastCleanupUnixMilliseconds;
    private int _lastCleanupDeletedEntries;

    public FileMediaApplicationCache(
        IConfiguration configuration,
        IPlatformClock clock,
        ILogger<FileMediaApplicationCache> logger,
        Microsoft.Extensions.Options.IOptions<allstarr.Models.Settings.CacheSettings>? configuredSettings = null)
        : this(
            FileMediaCacheOptions.FromConfiguration(configuration),
            clock,
            logger,
            configuredSettings)
    {
    }

    public FileMediaApplicationCache(
        FileMediaCacheOptions options,
        IPlatformClock clock,
        ILogger<FileMediaApplicationCache> logger,
        Microsoft.Extensions.Options.IOptions<allstarr.Models.Settings.CacheSettings>? configuredSettings = null)
    {
        _options = options;
        _settings = configuredSettings?.Value ?? new();
        _clock = clock;
        _logger = logger;
    }

    public bool IsEnabled =>
        _options.MaximumBytes > 0 &&
        _options.MaximumEntryBytes > 0 &&
        !string.IsNullOrWhiteSpace(_options.RootPath);

    public TimeSpan CleanupInterval => TimeSpan.FromMinutes(_options.CleanupIntervalMinutes);

    public async Task<ApplicationCacheTierUsage> GetUsageAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = ReadAllMetadata()
                .Where(item =>
                    (item.ExpiresAt is null || item.ExpiresAt > _clock.UtcNow) &&
                    File.Exists(PathsFor(item.Key).Payload))
                .ToArray();
            return new ApplicationCacheTierUsage(
                "media",
                entries.LongLength,
                entries.Sum(item => Math.Max(0, item.PayloadBytes)),
                _options.MaximumBytes,
                _options.MaximumEntryBytes,
                IsEnabled,
                Volatile.Read(ref _hits),
                Volatile.Read(ref _misses),
                Volatile.Read(ref _writes),
                Volatile.Read(ref _evictions));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<ApplicationCacheCategory, ApplicationCacheCategoryUsage>>
        GetCategoryUsageAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return ReadAllMetadata()
                .Where(item =>
                    (item.ExpiresAt is null || item.ExpiresAt > _clock.UtcNow) &&
                    File.Exists(PathsFor(item.Key).Payload))
                .GroupBy(item => ApplicationCachePolicyRegistry.Classify(item.Key))
                .ToDictionary(
                    group => group.Key,
                    group => new ApplicationCacheCategoryUsage(
                        group.LongCount(),
                        group.Sum(item => Math.Max(0, item.PayloadBytes))));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetStringAsync(string key)
    {
        if (!IsEnabled || !ApplicationCachePayloadPolicy.IsValidKey(key) ||
            !ApplicationCachePolicyRegistry.TryClassify(key, out _) ||
            ApplicationCachePayloadPolicy.IsMemoryEligible(key) ||
            !ApplicationCachePolicyRegistry.IsEnabled(key, _settings))
        {
            Interlocked.Increment(ref _misses);
            return null;
        }

        await _gate.WaitAsync();
        try
        {
            var paths = PathsFor(key);
            var metadata = await ReadMetadataAsync(paths.Metadata);
            if (metadata is null ||
                !string.Equals(metadata.Key, key, StringComparison.Ordinal) ||
                !File.Exists(paths.Payload))
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            if (metadata.ExpiresAt is not null && metadata.ExpiresAt <= _clock.UtcNow)
            {
                DeleteFiles(paths);
                Interlocked.Increment(ref _misses);
                Interlocked.Increment(ref _evictions);
                return null;
            }

            var value = await File.ReadAllTextAsync(paths.Payload, Encoding.UTF8);
            metadata = metadata with { LastAccessAt = _clock.UtcNow };
            await WriteMetadataAsync(paths.Metadata, metadata);
            Interlocked.Increment(ref _hits);
            return value;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Disk media cache GET failed for key {Key}", key);
            Interlocked.Increment(ref _misses);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        var value = await GetStringAsync(key);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null)
    {
        if (!IsEnabled || !ApplicationCachePayloadPolicy.IsValidKey(key) ||
            !ApplicationCachePolicyRegistry.TryClassify(key, out _) ||
            ApplicationCachePayloadPolicy.IsMemoryEligible(key) ||
            !ApplicationCachePolicyRegistry.IsEnabled(key, _settings))
        {
            return false;
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(value);
        if (payloadBytes > _options.MaximumEntryBytes)
        {
            _logger.LogWarning(
                "Disk media cache rejected {Bytes} byte payload for key {Key}; limit is {Limit}",
                payloadBytes,
                key,
                _options.MaximumEntryBytes);
            return false;
        }

        await _gate.WaitAsync();
        try
        {
            var paths = PathsFor(key);
            Directory.CreateDirectory(paths.Directory);
            await WritePayloadAsync(paths.Payload, value);
            var now = _clock.UtcNow;
            var effectiveExpiry = expiry ?? ApplicationCachePolicyRegistry.Resolve(key).FreshFor;
            await WriteMetadataAsync(
                paths.Metadata,
                new MediaEntryMetadata(
                    key,
                    payloadBytes,
                    now.Add(effectiveExpiry),
                    now,
                    now));
            Interlocked.Add(ref _evictions, await TrimToQuotaAsync());
            Interlocked.Increment(ref _writes);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Disk media cache SET failed for key {Key}", key);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class
    {
        try
        {
            return SetStringAsync(key, JsonSerializer.Serialize(value), expiry);
        }
        catch (JsonException)
        {
            return Task.FromResult(false);
        }
    }

    public async Task<bool> DeleteAsync(string key)
    {
        await _gate.WaitAsync();
        try
        {
            var paths = PathsFor(key);
            var existed = File.Exists(paths.Payload) || File.Exists(paths.Metadata);
            DeleteFiles(paths);
            if (existed)
            {
                Interlocked.Increment(ref _evictions);
            }

            return existed;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Disk media cache DELETE failed for key {Key}", key);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ExistsAsync(string key) =>
        await GetStringAsync(key) is not null;

    public async Task<int> DeleteByPatternAsync(string pattern)
    {
        await _gate.WaitAsync();
        try
        {
            var matcher = ApplicationCachePayloadPolicy.PatternMatcher(pattern);
            var matches = ReadAllMetadata()
                .Where(item => matcher(item.Key))
                .ToArray();
            foreach (var match in matches)
            {
                DeleteFiles(PathsFor(match.Key));
            }

            Interlocked.Add(ref _evictions, matches.Length);
            return matches.Length;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Disk media cache pattern delete failed for {Pattern}", pattern);
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> PurgeAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!Directory.Exists(_options.RootPath)) return 0;
            var deleted = Directory.EnumerateFiles(
                    _options.RootPath,
                    "*.json",
                    SearchOption.AllDirectories)
                .Count();
            Directory.Delete(_options.RootPath, recursive: true);
            Interlocked.Add(ref _evictions, deleted);
            return deleted;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Disk media cache purge failed");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> DeleteCategoryAsync(ApplicationCacheCategory category)
    {
        await _gate.WaitAsync();
        try
        {
            var matches = ReadAllMetadata()
                .Where(item => ApplicationCachePolicyRegistry.Classify(item.Key) == category)
                .ToArray();
            foreach (var match in matches)
            {
                DeleteFiles(PathsFor(match.Key));
            }

            Interlocked.Add(ref _evictions, matches.Length);
            return matches.Length;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Disk media cache category purge failed for {Category}", category);
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var deleted = CleanupOrphanedFiles();
            foreach (var metadata in ReadAllMetadata())
            {
                if (metadata.ExpiresAt is not null && metadata.ExpiresAt > _clock.UtcNow)
                {
                    continue;
                }

                DeleteFiles(PathsFor(metadata.Key));
                deleted++;
            }

            deleted += await TrimToQuotaAsync();
            Interlocked.Add(ref _evictions, deleted);
            Volatile.Write(ref _lastCleanupDeletedEntries, deleted);
            Interlocked.Exchange(
                ref _lastCleanupUnixMilliseconds,
                _clock.UtcNow.ToUnixTimeMilliseconds());
            return deleted;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Disk media cache cleanup failed");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FileMediaCacheMaintenancePreview> PreviewCleanupAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(_options.RootPath))
            {
                return new FileMediaCacheMaintenancePreview(
                    0, false, 0, 0, 0, 0, 0, 0, 0, 0,
                    Math.Max(60, (int)CleanupInterval.TotalSeconds),
                    LastCleanupAt(),
                    Volatile.Read(ref _lastCleanupDeletedEntries),
                    _clock.UtcNow);
            }

            var files = Directory
                .EnumerateFiles(_options.RootPath, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(Math.Max(1, _options.MaximumCleanupFiles) + 1)
                .ToArray();
            var scanLimitReached = files.Length > _options.MaximumCleanupFiles;
            var scanned = files.Take(_options.MaximumCleanupFiles).ToArray();
            var temporary = scanned.Where(path =>
                    path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var metadataFiles = scanned.Where(path =>
                    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var payloadFiles = scanned.Where(path =>
                    path.EndsWith(".payload", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var malformedMetadata = 0;
            var orphanedMetadata = 0;
            var expiredEntries = 0;
            var noExpiryEntries = 0;
            long reclaimableBytes = temporary.Sum(FileLength);
            var validEntries = new List<MediaEntryMetadata>();

            foreach (var metadataPath in metadataFiles)
            {
                MediaEntryMetadata? metadata;
                try
                {
                    metadata = JsonSerializer.Deserialize<MediaEntryMetadata>(
                        File.ReadAllText(metadataPath, Encoding.UTF8),
                        JsonOptions);
                }
                catch
                {
                    metadata = null;
                }

                if (metadata is null || !ApplicationCachePayloadPolicy.IsValidKey(metadata.Key))
                {
                    malformedMetadata++;
                    reclaimableBytes += FileLength(metadataPath);
                    continue;
                }

                var expected = PathsFor(metadata.Key);
                var payloadPath = Path.ChangeExtension(metadataPath, ".payload");
                var validPair = File.Exists(payloadPath) &&
                                string.Equals(
                                    Path.GetFullPath(metadataPath),
                                    Path.GetFullPath(expected.Metadata),
                                    StringComparison.Ordinal);
                if (!validPair)
                {
                    orphanedMetadata++;
                    reclaimableBytes += FileLength(metadataPath) + FileLength(payloadPath);
                    continue;
                }

                if (metadata.ExpiresAt is not null && metadata.ExpiresAt <= _clock.UtcNow)
                {
                    expiredEntries++;
                    reclaimableBytes += FileLength(metadataPath) + FileLength(payloadPath);
                    continue;
                }

                if (metadata.ExpiresAt is null)
                {
                    noExpiryEntries++;
                    reclaimableBytes += FileLength(metadataPath) + FileLength(payloadPath);
                    continue;
                }

                validEntries.Add(metadata);
            }

            var orphanedPayloads = payloadFiles.Count(path =>
                !File.Exists(Path.ChangeExtension(path, ".json")));
            reclaimableBytes += payloadFiles
                .Where(path => !File.Exists(Path.ChangeExtension(path, ".json")))
                .Sum(FileLength);

            var overQuota = QuotaOverflow(validEntries);
            foreach (var entry in overQuota)
            {
                var paths = PathsFor(entry.Key);
                reclaimableBytes += FileLength(paths.Metadata) + FileLength(paths.Payload);
            }

            return new FileMediaCacheMaintenancePreview(
                scanned.Length,
                scanLimitReached,
                temporary.Length,
                malformedMetadata,
                orphanedMetadata,
                orphanedPayloads,
                expiredEntries,
                noExpiryEntries,
                overQuota.Length,
                Math.Max(0, reclaimableBytes),
                Math.Max(60, (int)CleanupInterval.TotalSeconds),
                LastCleanupAt(),
                Volatile.Read(ref _lastCleanupDeletedEntries),
                _clock.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UnreferencedMediaPayloadPreview> PreviewUnreferencedArtworkAsync(
        IReadOnlySet<string> referencedPayloadKeys,
        int maximumEntries = 1000,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var take = Math.Clamp(maximumEntries, 1, _options.MaximumCleanupFiles);
            var entries = ReadAllMetadata()
                .Where(item =>
                    CacheKeyBuilder.IsMediaAssetPayloadKey(item.Key) &&
                    item.LastAccessAt <= _clock.UtcNow.AddMinutes(-5) &&
                    !referencedPayloadKeys.Contains(item.Key))
                .OrderBy(item => item.LastAccessAt)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Take(take + 1)
                .ToArray();
            return new(
                entries.Take(take).Select(item => item.Key).ToArray(),
                entries.Take(take).Sum(item => Math.Max(0, item.PayloadBytes)),
                entries.Length > take);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CleanupUnreferencedArtworkAsync(
        IReadOnlySet<string> referencedPayloadKeys,
        CancellationToken cancellationToken = default)
    {
        var preview = await PreviewUnreferencedArtworkAsync(
            referencedPayloadKeys,
            cancellationToken: cancellationToken);
        var deleted = 0;
        foreach (var key in preview.Keys)
        {
            if (await DeleteAsync(key))
            {
                deleted++;
            }
        }

        return deleted;
    }

    internal async Task<FileCacheEntrySnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = ReadAllMetadata().OrderBy(item => item.Key, StringComparer.Ordinal)
                .Take(_options.MaximumCleanupFiles + 1).ToArray();
            return new(entries.Take(_options.MaximumCleanupFiles).Select(item => new ApplicationCacheEntryInfo(
                item.Key, item.PayloadBytes, item.ExpiresAt, item.WrittenAt ?? item.LastAccessAt,
                ApplicationCacheStorageTier.Media, HasInvalidDescriptor(item))).ToArray(), entries.Length > _options.MaximumCleanupFiles);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning("Cache metadata scan deferred ({ErrorType})", exception.GetType().Name);
            return new(Array.Empty<ApplicationCacheEntryInfo>(), true);
        }
        finally { _gate.Release(); }
    }

    private bool HasInvalidDescriptor(MediaEntryMetadata entry)
    {
        if (!CacheKeyBuilder.IsMediaAssetDescriptorKey(entry.Key)) return false;
        try { return ReadArtworkPayloadKey(File.ReadAllText(PathsFor(entry.Key).Payload)) is null; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public async Task<ArtworkPayloadReferenceSnapshot> GetArtworkPayloadReferencesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var descriptors = ReadAllMetadata()
                .Where(item => CacheKeyBuilder.IsMediaAssetDescriptorKey(item.Key) && item.ExpiresAt > _clock.UtcNow)
                .OrderBy(item => item.Key, StringComparer.Ordinal).Take(_options.MaximumCleanupFiles + 1).ToArray();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var incomplete = descriptors.Length > _options.MaximumCleanupFiles;
            foreach (var descriptor in descriptors.Take(_options.MaximumCleanupFiles))
            {
                var value = await File.ReadAllTextAsync(PathsFor(descriptor.Key).Payload, cancellationToken);
                var key = ReadArtworkPayloadKey(value);
                if (key is null) incomplete = true;
                else keys.Add(key);
            }
            return new(keys, incomplete);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new(new HashSet<string>(StringComparer.Ordinal), true);
        }
        finally { _gate.Release(); }
    }

    private static string? ReadArtworkPayloadKey(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals("PayloadKey", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { } key && CacheKeyBuilder.IsMediaAssetPayloadKey(key))
                    return key;
        }
        catch (JsonException) { }
        return null;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private int CleanupOrphanedFiles()
    {
        if (!Directory.Exists(_options.RootPath))
        {
            return 0;
        }

        var deleted = 0;
        var files = Directory
            .EnumerateFiles(_options.RootPath, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(Math.Max(1, _options.MaximumCleanupFiles))
            .ToArray();

        foreach (var temporary in files.Where(path =>
                     path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(temporary);
            deleted++;
        }

        foreach (var metadataPath in files.Where(path =>
                     path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var payloadPath = Path.ChangeExtension(metadataPath, ".payload");
            MediaEntryMetadata? metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<MediaEntryMetadata>(
                    File.ReadAllText(metadataPath, Encoding.UTF8),
                    JsonOptions);
            }
            catch
            {
                metadata = null;
            }

            var expectedMetadataPath = metadata is null || !ApplicationCachePayloadPolicy.IsValidKey(metadata.Key)
                ? null
                : Path.GetFullPath(PathsFor(metadata.Key).Metadata);
            var validPair = metadata is not null &&
                            File.Exists(payloadPath) &&
                            string.Equals(
                                Path.GetFullPath(metadataPath),
                                expectedMetadataPath,
                                StringComparison.Ordinal);
            if (validPair)
            {
                continue;
            }

            File.Delete(metadataPath);
            File.Delete(payloadPath);
            deleted++;
        }

        foreach (var payloadPath in files.Where(path =>
                     path.EndsWith(".payload", StringComparison.OrdinalIgnoreCase)))
        {
            if (!File.Exists(payloadPath) ||
                File.Exists(Path.ChangeExtension(payloadPath, ".json")))
            {
                continue;
            }

            File.Delete(payloadPath);
            deleted++;
        }

        return deleted;
    }

    private Task<int> TrimToQuotaAsync()
    {
        var overflow = QuotaOverflow(ReadAllMetadata());
        foreach (var entry in overflow) DeleteFiles(PathsFor(entry.Key));
        return Task.FromResult(overflow.Length);
    }

    private MediaEntryMetadata[] QuotaOverflow(IEnumerable<MediaEntryMetadata> source)
    {
        var entries = source.OrderBy(item => item.LastAccessAt).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in entries.GroupBy(item => ApplicationCachePolicyRegistry.Classify(item.Key)))
        {
            var policy = ApplicationCachePolicyRegistry.Resolve(category.Key, _settings);
            var count = category.Count();
            var bytes = category.Sum(item => Math.Max(0, item.PayloadBytes));
            foreach (var entry in category)
            {
                if (count <= policy.MaximumEntries && bytes <= policy.MaximumBytes) break;
                removed.Add(entry.Key);
                count--;
                bytes -= Math.Max(0, entry.PayloadBytes);
            }
        }
        var remaining = entries.Where(item => !removed.Contains(item.Key)).ToArray();
        var totalBytes = remaining.Sum(item => Math.Max(0, item.PayloadBytes));
        var totalCount = remaining.Length;
        foreach (var entry in remaining)
        {
            if (totalBytes <= _options.MaximumBytes && totalCount <= _options.MaximumCleanupFiles) break;
            removed.Add(entry.Key);
            totalBytes -= Math.Max(0, entry.PayloadBytes);
            totalCount--;
        }
        return entries.Where(item => removed.Contains(item.Key)).ToArray();
    }

    private IEnumerable<MediaEntryMetadata> ReadAllMetadata()
    {
        if (!Directory.Exists(_options.RootPath))
        {
            return Array.Empty<MediaEntryMetadata>();
        }

        return Directory.EnumerateFiles(_options.RootPath, "*.json", SearchOption.AllDirectories)
            .Select(path =>
            {
                try
                {
                    return JsonSerializer.Deserialize<MediaEntryMetadata>(
                        File.ReadAllText(path, Encoding.UTF8),
                        JsonOptions);
                }
                catch
                {
                    return null;
                }
            })
            .Where(item => item is not null && ApplicationCachePayloadPolicy.IsValidKey(item.Key))
            .Cast<MediaEntryMetadata>()
            .ToArray();
    }

    private CachePaths PathsFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            .ToLowerInvariant();
        var directory = Path.Combine(_options.RootPath, hash[..2]);
        return new CachePaths(
            directory,
            Path.Combine(directory, $"{hash}.payload"),
            Path.Combine(directory, $"{hash}.json"));
    }

    private static async Task WritePayloadAsync(string path, string value)
    {
        var temporary = path + $".{Guid.CreateVersion7():N}.tmp";
        await File.WriteAllTextAsync(temporary, value, Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task WriteMetadataAsync(string path, MediaEntryMetadata metadata)
    {
        var temporary = path + $".{Guid.CreateVersion7():N}.tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(metadata, JsonOptions),
            Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<MediaEntryMetadata?> ReadMetadataAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<MediaEntryMetadata>(
            await File.ReadAllTextAsync(path, Encoding.UTF8),
            JsonOptions);
    }

    private static void DeleteFiles(CachePaths paths)
    {
        File.Delete(paths.Payload);
        File.Delete(paths.Metadata);
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private DateTimeOffset? LastCleanupAt()
    {
        var milliseconds = Interlocked.Read(ref _lastCleanupUnixMilliseconds);
        return milliseconds <= 0
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private sealed record CachePaths(string Directory, string Payload, string Metadata);

    private sealed record MediaEntryMetadata(
        string Key,
        long PayloadBytes,
        DateTimeOffset? ExpiresAt,
        DateTimeOffset LastAccessAt,
        DateTimeOffset? WrittenAt = null);
}

public sealed class HybridApplicationCache(
    MemoryApplicationCache metadata,
    FileMediaApplicationCache media,
    Microsoft.Extensions.Options.IOptions<allstarr.Models.Settings.CacheSettings>? configuredSettings = null,
    ApplicationCacheActivityMetrics? activityMetrics = null,
    IDbContextFactory<AllstarrDbContext>? contextFactory = null,
    IPlatformClock? clock = null,
    ILogger<HybridApplicationCache>? logger = null) : IApplicationCache
{
    private readonly allstarr.Models.Settings.CacheSettings _settings = configuredSettings?.Value ?? new();
    private readonly ApplicationCacheActivityMetrics _activity = activityMetrics ?? new();
    private DateTimeOffset Now => clock?.UtcNow ?? DateTimeOffset.UtcNow;
    public bool IsEnabled => metadata.IsEnabled || media.IsEnabled;

    public Task<string?> GetStringAsync(string key) => IsCategoryEnabled(key)
        ? Target(key).GetStringAsync(key) : Task.FromResult<string?>(null);
    public Task<T?> GetAsync<T>(string key) where T : class => IsCategoryEnabled(key)
        ? Target(key).GetAsync<T>(key) : Task.FromResult<T?>(null);
    public Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null) => IsCategoryEnabled(key)
        ? Target(key).SetStringAsync(key, value, EffectiveExpiry(key, expiry)) : Task.FromResult(false);
    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class => IsCategoryEnabled(key)
        ? Target(key).SetAsync(key, value, EffectiveExpiry(key, expiry)) : Task.FromResult(false);
    public Task<bool> DeleteAsync(string key) => ApplicationCachePayloadPolicy.IsValidKey(key)
        ? Target(key).DeleteAsync(key) : Task.FromResult(false);
    public Task<bool> ExistsAsync(string key) => IsCategoryEnabled(key)
        ? Target(key).ExistsAsync(key) : Task.FromResult(false);
    public async Task<int> DeleteByPatternAsync(string pattern) =>
        await metadata.DeleteByPatternAsync(pattern) + await media.DeleteByPatternAsync(pattern);

    public async Task<ApplicationCacheDiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var memoryCategories = metadata.GetCategoryUsage();
        var mediaUsage = await media.GetUsageAsync(cancellationToken);
        var mediaCategories = await media.GetCategoryUsageAsync(cancellationToken);
        return new(metadata.GetUsageSnapshot(), mediaUsage,
            ApplicationCachePolicyRegistry.All(_settings).Select(policy => ApplicationCacheCategoryDiagnostics.From(
                policy, ApplicationCachePolicyRegistry.IsEnabled(policy.Category, _settings),
                (policy.StorageTier == ApplicationCacheStorageTier.Metadata ? memoryCategories : mediaCategories)
                    .GetValueOrDefault(policy.Category))).ToArray(), _activity.Snapshot(), Now)
        {
            ArtworkLimits = new(mediaUsage.MaximumEntryBytes ?? 0, MediaAssetResolver.MaximumDecodedPixels)
        };
    }

    public Task<int> PurgeMetadataAsync() => metadata.PurgeAllAsync();
    public Task<int> PurgeMediaAsync() => media.PurgeAllAsync();
    public async Task<int> PurgeAllAsync() => await PurgeMetadataAsync() + await PurgeMediaAsync();
    public Task<int> PurgeCategoryAsync(ApplicationCacheCategory category) =>
        ApplicationCachePolicyRegistry.Resolve(category, _settings).StorageTier == ApplicationCacheStorageTier.Metadata
            ? metadata.DeleteCategoryAsync(category) : media.DeleteCategoryAsync(category);

    public async Task<ApplicationCacheMaintenancePreview> PreviewMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await MaintenanceCandidatesAsync(cancellationToken);
        var mediaPreview = await media.PreviewCleanupAsync(cancellationToken);
        var references = await media.GetArtworkPayloadReferencesAsync(cancellationToken);
        var unreferenced = await media.PreviewUnreferencedArtworkAsync(references.PayloadKeys,
            cancellationToken: cancellationToken);
        return new(candidates.Preview, mediaPreview,
            references.ScanLimitReached ? 0 : unreferenced.Keys.Count,
            references.ScanLimitReached ? 0 : unreferenced.ReclaimableBytes,
            references.ScanLimitReached || unreferenced.ScanLimitReached, Now);
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await MaintenanceCandidatesAsync(cancellationToken);
        var deleted = 0;
        foreach (var entry in candidates.Entries)
            if (await (entry.Tier == ApplicationCacheStorageTier.Metadata ? (IApplicationCache)metadata : media)
                .DeleteAsync(entry.Key)) deleted++;
        deleted += await media.CleanupAsync(cancellationToken);
        var references = await media.GetArtworkPayloadReferencesAsync(cancellationToken);
        if (!references.ScanLimitReached)
            deleted += await media.CleanupUnreferencedArtworkAsync(references.PayloadKeys, cancellationToken);
        return deleted;
    }

    private async Task<(CacheMetadataMaintenancePreview Preview, ApplicationCacheEntryInfo[] Entries)>
        MaintenanceCandidatesAsync(CancellationToken cancellationToken)
    {
        var files = await media.SnapshotAsync(cancellationToken);
        var entries = metadata.Snapshot().Concat(files.Entries).ToArray();
        var expired = entries.Where(item => item.Tier == ApplicationCacheStorageTier.Metadata && item.ExpiresAt <= Now).ToArray();
        var unknown = entries.Where(item => item.InvalidDescriptor || !ApplicationCachePayloadPolicy.IsValidKey(item.Key) ||
            !ApplicationCachePolicyRegistry.TryClassify(item.Key, out _)).ToArray();
        var disabled = entries.Except(unknown).Where(item => !IsCategoryEnabled(item.Key)).ToArray();
        HashSet<string> staleKeys;
        try
        {
            staleKeys = await StaleScopeKeysAsync(entries.Except(unknown).Select(item => item.Key), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger?.LogWarning("Cache account scope cleanup deferred ({ErrorType})", exception.GetType().Name);
            staleKeys = new(StringComparer.Ordinal);
        }
        var stale = entries.Where(item => staleKeys.Contains(item.Key)).ToArray();
        var superseded = entries.Except(expired).Except(unknown).Except(disabled).Except(stale)
            .Where(item => CacheKeyBuilder.IsMediaAssetDescriptorKey(item.Key) && item.ExpiresAt > Now)
            .GroupBy(item => item.Key[..item.Key.LastIndexOf(':')], StringComparer.Ordinal)
            .SelectMany(group => group.OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.Key, StringComparer.Ordinal).Skip(1)).ToArray();
        var deleted = expired.Concat(unknown).Concat(disabled).Concat(stale).Concat(superseded)
            .DistinctBy(item => item.Key).ToArray();
        return (new(entries.Length, files.ScanLimitReached, expired.Length, unknown.Length, disabled.Length,
            0, stale.Length, superseded.Length, 0, deleted.Sum(item => item.PayloadBytes), Now), deleted);
    }

    private async Task<HashSet<string>> StaleScopeKeysAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var scoped = keys.Select(key => (Key: key, Id: ScopedAccountId(key)))
            .Where(item => item.Id.HasValue).ToArray();
        var stale = new HashSet<string>(StringComparer.Ordinal);
        if (scoped.Length == 0 || contextFactory is null) return stale;
        var ids = scoped.Select(item => item.Id!.Value).Distinct().ToArray();
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await database.ProviderAccounts.AsNoTracking().Where(item => ids.Contains(item.Id))
            .Select(item => new { item.Id, item.OwnerUserId, item.ProviderId, item.Revision, item.Enabled })
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        foreach (var item in scoped)
        {
            var parts = item.Key.Split(':');
            if (!accounts.TryGetValue(item.Id!.Value, out var account) || !account.Enabled ||
                (account.OwnerUserId is { } owner && !parts[3].Equals(owner.ToString("N"), StringComparison.Ordinal)) ||
                !parts[parts.Length == 8 ? 6 : 5].Equals(account.ProviderId, StringComparison.OrdinalIgnoreCase) ||
                (parts.Length == 8 && (!long.TryParse(parts[5], out var revision) || revision != account.Revision)))
                stale.Add(item.Key);
        }
        return stale;
    }

    private static Guid? ScopedAccountId(string key)
    {
        var parts = key.Split(':');
        var account = parts.Length switch
        {
            8 when key.StartsWith("playlist:discovery:v3:", StringComparison.Ordinal) => parts[4],
            10 when CacheKeyBuilder.IsMediaAssetDescriptorKey(key) => parts[4],
            _ => null
        };
        return Guid.TryParseExact(account, "N", out var id) ? id : null;
    }

    private IApplicationCache Target(string key) => ApplicationCachePayloadPolicy.IsMemoryEligible(key) ? metadata : media;
    private bool IsCategoryEnabled(string key) => ApplicationCachePayloadPolicy.IsValidKey(key) &&
        ApplicationCachePolicyRegistry.TryClassify(key, out _) && ApplicationCachePolicyRegistry.IsEnabled(key, _settings);
    private TimeSpan EffectiveExpiry(string key, TimeSpan? expiry) => expiry ?? ApplicationCachePolicyRegistry.Resolve(key, _settings).FreshFor;
    internal TimeSpan CleanupInterval => media.CleanupInterval;
}

public sealed class ApplicationCacheMaintenanceService(
    HybridApplicationCache cache,
    ILogger<ApplicationCacheMaintenanceService> logger) : BackgroundService
{
    public Task<int> RunOnceAsync(CancellationToken cancellationToken = default) =>
        cache.CleanupAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(cache.CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var deleted = await RunOnceAsync(stoppingToken);
            if (deleted > 0)
            {
                logger.LogDebug("Removed {Count} expired, orphaned, or over-quota cache entries", deleted);
            }
        }
    }
}
