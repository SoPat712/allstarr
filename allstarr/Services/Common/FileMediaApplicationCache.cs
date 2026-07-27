using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;

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
    int OverQuotaEntries,
    long ReclaimableBytes,
    int CleanupIntervalSeconds,
    DateTimeOffset? LastCleanupAt,
    int LastCleanupDeletedEntries,
    DateTimeOffset CapturedAt);

/// <summary>
/// Bounded disk cache for artwork and other reconstructable media payloads.
/// Cache keys are hashed before becoming paths and original keys live only in sidecars.
/// </summary>
public sealed class FileMediaApplicationCache : IApplicationCache, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FileMediaCacheOptions _options;
    private readonly IPlatformClock _clock;
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
        ILogger<FileMediaApplicationCache> logger)
        : this(
            FileMediaCacheOptions.FromConfiguration(configuration),
            clock,
            logger)
    {
    }

    public FileMediaApplicationCache(
        FileMediaCacheOptions options,
        IPlatformClock clock,
        ILogger<FileMediaApplicationCache> logger)
    {
        _options = options;
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
        if (!IsEnabled || ApplicationCachePayloadPolicy.IsDatabaseEligible(key))
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
        if (!IsEnabled || ApplicationCachePayloadPolicy.IsDatabaseEligible(key))
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
            await WriteMetadataAsync(
                paths.Metadata,
                new MediaEntryMetadata(
                    key,
                    payloadBytes,
                    expiry.HasValue ? now.Add(expiry.Value) : null,
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

    public IEnumerable<string> GetKeysByPattern(string pattern)
    {
        _gate.Wait();
        try
        {
            return ReadAllMetadata()
                .Where(item =>
                    (item.ExpiresAt is null || item.ExpiresAt > _clock.UtcNow) &&
                    FileSystemName.MatchesSimpleExpression(pattern, item.Key, ignoreCase: true))
                .Select(item => item.Key)
                .ToArray();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Disk media cache key scan failed for pattern {Pattern}", pattern);
            return Array.Empty<string>();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> DeleteByPatternAsync(string pattern)
    {
        await _gate.WaitAsync();
        try
        {
            var matches = ReadAllMetadata()
                .Where(item => FileSystemName.MatchesSimpleExpression(
                    pattern,
                    item.Key,
                    ignoreCase: true))
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

    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var deleted = CleanupOrphanedFiles();
            foreach (var metadata in ReadAllMetadata())
            {
                if (metadata.ExpiresAt is null || metadata.ExpiresAt > _clock.UtcNow)
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
                    0, false, 0, 0, 0, 0, 0, 0, 0,
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

                if (metadata is null)
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

                validEntries.Add(metadata);
            }

            var orphanedPayloads = payloadFiles.Count(path =>
                !File.Exists(Path.ChangeExtension(path, ".json")));
            reclaimableBytes += payloadFiles
                .Where(path => !File.Exists(Path.ChangeExtension(path, ".json")))
                .Sum(FileLength);

            var totalBytes = validEntries.Sum(item => Math.Max(0, item.PayloadBytes));
            var overQuotaEntries = 0;
            foreach (var entry in validEntries
                         .OrderBy(item => item.LastAccessAt)
                         .ThenBy(item => item.Key, StringComparer.Ordinal))
            {
                if (totalBytes <= _options.MaximumBytes)
                {
                    break;
                }

                overQuotaEntries++;
                totalBytes -= Math.Max(0, entry.PayloadBytes);
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
                overQuotaEntries,
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

            var expectedMetadataPath = metadata is null
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

    private async Task<int> TrimToQuotaAsync()
    {
        var entries = ReadAllMetadata()
            .OrderBy(item => item.LastAccessAt)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        var totalBytes = entries.Sum(item => Math.Max(0, item.PayloadBytes));
        var deleted = 0;
        foreach (var entry in entries)
        {
            if (totalBytes <= _options.MaximumBytes)
            {
                break;
            }

            DeleteFiles(PathsFor(entry.Key));
            totalBytes -= Math.Max(0, entry.PayloadBytes);
            deleted++;
        }

        await Task.CompletedTask;
        return deleted;
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
            .Where(item => item is not null)
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
        DateTimeOffset LastAccessAt);
}

public sealed class HybridApplicationCache(
    BoundedHotApplicationCache metadata,
    FileMediaApplicationCache media,
    Microsoft.Extensions.Options.IOptions<allstarr.Models.Settings.CacheSettings>? configuredSettings = null)
    : IApplicationCache
{
    private readonly allstarr.Models.Settings.CacheSettings _settings =
        configuredSettings?.Value ?? new allstarr.Models.Settings.CacheSettings();

    public bool IsEnabled => metadata.IsEnabled || media.IsEnabled;

    public Task<string?> GetStringAsync(string key) =>
        IsCategoryEnabled(key)
            ? Target(key).GetStringAsync(key)
            : Task.FromResult<string?>(null);

    public Task<T?> GetAsync<T>(string key) where T : class =>
        IsCategoryEnabled(key)
            ? Target(key).GetAsync<T>(key)
            : Task.FromResult<T?>(null);

    public Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null) =>
        IsCategoryEnabled(key)
            ? Target(key).SetStringAsync(key, value, EffectiveExpiry(key, expiry))
            : Task.FromResult(false);

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class =>
        IsCategoryEnabled(key)
            ? Target(key).SetAsync(key, value, EffectiveExpiry(key, expiry))
            : Task.FromResult(false);

    public Task<bool> DeleteAsync(string key) =>
        Target(key).DeleteAsync(key);

    public Task<bool> ExistsAsync(string key) =>
        IsCategoryEnabled(key)
            ? Target(key).ExistsAsync(key)
            : Task.FromResult(false);

    public IEnumerable<string> GetKeysByPattern(string pattern) =>
        metadata.GetKeysByPattern(pattern)
            .Concat(media.GetKeysByPattern(pattern))
            .Where(IsCategoryEnabled)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public async Task<int> DeleteByPatternAsync(string pattern) =>
        await metadata.DeleteByPatternAsync(pattern) +
        await media.DeleteByPatternAsync(pattern);

    public async Task<ApplicationCacheDiagnosticsSnapshot> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        var databaseUsageTask = metadata.GetDatabaseUsageAsync(cancellationToken);
        var databaseCategoryUsageTask = metadata.GetDatabaseCategoryUsageAsync(cancellationToken);
        var mediaUsageTask = media.GetUsageAsync(cancellationToken);
        var mediaCategoryUsageTask = media.GetCategoryUsageAsync(cancellationToken);
        await Task.WhenAll(
            databaseUsageTask,
            databaseCategoryUsageTask,
            mediaUsageTask,
            mediaCategoryUsageTask);

        var databaseCategoryUsage = await databaseCategoryUsageTask;
        var mediaCategoryUsage = await mediaCategoryUsageTask;
        return new ApplicationCacheDiagnosticsSnapshot(
            await databaseUsageTask,
            metadata.GetUsageSnapshot(),
            await mediaUsageTask,
            ApplicationCachePolicyRegistry.All(_settings)
                .Select(policy =>
                {
                    var usage = policy.StorageTier == ApplicationCacheStorageTier.Metadata
                        ? databaseCategoryUsage.GetValueOrDefault(policy.Category)
                        : mediaCategoryUsage.GetValueOrDefault(policy.Category);
                    return ApplicationCacheCategoryDiagnostics.From(
                        policy,
                        ApplicationCachePolicyRegistry.IsEnabled(policy.Category, _settings),
                        usage);
                })
                .ToArray(),
            DateTimeOffset.UtcNow);
    }

    public Task<int> PurgeMetadataAsync() => metadata.DeleteByPatternAsync("*");

    public Task<int> PurgeMediaAsync() => media.DeleteByPatternAsync("*");

    public Task<FileMediaCacheMaintenancePreview> PreviewMediaCleanupAsync(
        CancellationToken cancellationToken = default) =>
        media.PreviewCleanupAsync(cancellationToken);

    public Task<int> CleanupMediaAsync(CancellationToken cancellationToken = default) =>
        media.CleanupAsync(cancellationToken);

    public async Task<int> PurgeAllAsync() =>
        await PurgeMetadataAsync() + await PurgeMediaAsync();

    private IApplicationCache Target(string key) =>
        ApplicationCachePayloadPolicy.IsDatabaseEligible(key) ? metadata : media;

    private bool IsCategoryEnabled(string key) =>
        ApplicationCachePolicyRegistry.IsEnabled(key, _settings);

    private TimeSpan EffectiveExpiry(string key, TimeSpan? expiry) =>
        expiry ?? ApplicationCachePolicyRegistry.Resolve(key, _settings).FreshFor;
}

public sealed class FileMediaApplicationCacheCleanupService(
    FileMediaApplicationCache cache,
    ILogger<FileMediaApplicationCacheCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(cache.CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var deleted = await cache.CleanupAsync(stoppingToken);
            if (deleted > 0)
            {
                logger.LogDebug("Removed {Count} expired or over-quota media cache entries", deleted);
            }
        }
    }
}
