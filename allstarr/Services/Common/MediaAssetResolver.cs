using System.Collections.Concurrent;
using System.Security.Cryptography;
using SkiaSharp;

namespace allstarr.Services.Common;

public sealed record MediaAssetIdentity(
    Guid? TenantId,
    Guid? UserId,
    Guid? ProviderAccountId,
    string ProviderId,
    string ResourceKind,
    string ResourceId,
    string? Revision = null,
    int? Width = null,
    int? Height = null);

public sealed record MediaAssetSource(
    byte[] Bytes,
    string ContentType,
    string? ETag = null,
    DateTimeOffset? LastModified = null);

public sealed record ResolvedMediaAsset(
    byte[] Bytes,
    string ContentType,
    string Sha256,
    string? ETag,
    DateTimeOffset? LastModified,
    bool FromCache);

public interface IMediaAssetResolver
{
    Task<ResolvedMediaAsset?> ResolveAsync(
        MediaAssetIdentity identity,
        Func<CancellationToken, Task<MediaAssetSource?>> fetch,
        int maximumBytes,
        CancellationToken cancellationToken = default);
}

public sealed class MediaAssetResolver(
    IApplicationCache cache,
    ILogger<MediaAssetResolver> logger,
    ApplicationCacheActivityMetrics? activityMetrics = null) : IMediaAssetResolver
{
    private readonly ApplicationCacheActivityMetrics _activity =
        activityMetrics ?? new ApplicationCacheActivityMetrics();
    private readonly ConcurrentDictionary<string, Lazy<Task<ResolvedMediaAsset?>>> _inflight =
        new(StringComparer.Ordinal);

    public async Task<ResolvedMediaAsset?> ResolveAsync(
        MediaAssetIdentity identity,
        Func<CancellationToken, Task<MediaAssetSource?>> fetch,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        Validate(identity, maximumBytes);
        ArgumentNullException.ThrowIfNull(fetch);
        var descriptorKey = CacheKeyBuilder.BuildMediaAssetDescriptorKey(identity);
        if (await ReadAsync(descriptorKey, maximumBytes) is { } cached)
        {
            _activity.RecordUpstreamBytesAvoided(cached.Bytes.Length);
            return cached;
        }

        var created = new Lazy<Task<ResolvedMediaAsset?>>(
            () => FetchAsync(identity, descriptorKey, fetch, maximumBytes, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var pending = _inflight.GetOrAdd(descriptorKey, created);
        if (!ReferenceEquals(pending, created))
        {
            _activity.RecordCoalesced();
        }
        try
        {
            return await pending.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<ResolvedMediaAsset?>>>(
                descriptorKey, pending));
        }
    }

    private async Task<ResolvedMediaAsset?> ReadAsync(string descriptorKey, int maximumBytes)
    {
        var descriptor = await cache.GetAsync<MediaAssetDescriptor>(descriptorKey);
        if (descriptor == null || descriptor.PayloadBytes is <= 0 || descriptor.PayloadBytes > maximumBytes)
            return null;
        var payload = await cache.GetAsync<MediaAssetPayload>(descriptor.PayloadKey);
        if (payload?.Bytes is not { Length: > 0 } bytes ||
            bytes.Length != descriptor.PayloadBytes ||
            !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(descriptor.Sha256, StringComparison.Ordinal))
            return null;
        return new(bytes, descriptor.ContentType, descriptor.Sha256,
            descriptor.ETag, descriptor.LastModified, true);
    }

    private async Task<ResolvedMediaAsset?> FetchAsync(
        MediaAssetIdentity identity,
        string descriptorKey,
        Func<CancellationToken, Task<MediaAssetSource?>> fetch,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = await fetch(cancellationToken);
            if (source?.Bytes is not { Length: > 0 } bytes ||
                bytes.Length > maximumBytes ||
                !source.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;
            var policy = ApplicationCachePolicyRegistry.Resolve(ApplicationCacheCategory.Artwork);
            if (!await StorePayloadAsync(bytes, policy))
                return Asset(source, false);

            var selected = CreateVariant(source, identity, maximumBytes);
            if (!bytes.AsSpan().SequenceEqual(selected.Bytes))
            {
                var originalSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
                await cache.SetAsync(
                    CacheKeyBuilder.BuildMediaAssetDescriptorKey(
                        identity with { Width = null, Height = null }),
                    new MediaAssetDescriptor(
                        CacheKeyBuilder.BuildMediaAssetPayloadKey(originalSha256),
                        originalSha256,
                        source.ContentType,
                        bytes.Length,
                        source.ETag,
                        source.LastModified),
                    policy.FreshFor);
            }
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(selected.Bytes));
            var payloadKey = CacheKeyBuilder.BuildMediaAssetPayloadKey(sha256);
            if (!bytes.AsSpan().SequenceEqual(selected.Bytes) &&
                !await StorePayloadAsync(selected.Bytes, policy))
                return Asset(selected, false);
            await cache.SetAsync(
                descriptorKey,
                new MediaAssetDescriptor(
                    payloadKey, sha256, selected.ContentType, selected.Bytes.Length,
                    source.ETag, source.LastModified),
                policy.FreshFor);
            return Asset(selected, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Scoped media asset fetch failed for descriptor {DescriptorKey}", descriptorKey);
            return null;
        }
    }

    private async Task<bool> StorePayloadAsync(
        byte[] bytes,
        ApplicationCacheCategoryPolicy policy)
    {
        var key = CacheKeyBuilder.BuildMediaAssetPayloadKey(
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return await cache.SetAsync(key, new MediaAssetPayload(bytes), policy.FreshFor);
    }

    private MediaAssetSource CreateVariant(
        MediaAssetSource source,
        MediaAssetIdentity identity,
        int maximumBytes)
    {
        if (identity.Width is null && identity.Height is null) return source;
        try
        {
            using var data = SKData.CreateCopy(source.Bytes);
            using var codec = SKCodec.Create(data);
            var info = codec?.Info;
            if (info is not { Width: > 0, Height: > 0 } ||
                (long)info.Value.Width * info.Value.Height > 16_000_000)
                return source;
            var scale = Math.Min(
                identity.Width.HasValue ? identity.Width.Value / (double)info.Value.Width : 1,
                identity.Height.HasValue ? identity.Height.Value / (double)info.Value.Height : 1);
            if (scale >= 1) return source;
            var width = Math.Max(1, (int)Math.Round(info.Value.Width * scale));
            var height = Math.Max(1, (int)Math.Round(info.Value.Height * scale));
            using var original = SKBitmap.Decode(codec);
            using var resized = new SKBitmap(new SKImageInfo(
                width, height, original.ColorType, original.AlphaType));
            if (!original.ScalePixels(
                    resized,
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
                return source;
            using var image = SKImage.FromBitmap(resized);
            var format = original.AlphaType == SKAlphaType.Opaque
                ? SKEncodedImageFormat.Jpeg
                : SKEncodedImageFormat.Png;
            using var encoded = image.Encode(format, 85);
            var bytes = encoded?.ToArray();
            return bytes is not { Length: > 0 } || bytes.Length > maximumBytes
                ? source
                : new(bytes, format == SKEncodedImageFormat.Png ? "image/png" : "image/jpeg",
                    source.ETag, source.LastModified);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception,
                "Unable to create {Width}x{Height} media variant",
                identity.Width, identity.Height);
            return source;
        }
    }

    private static ResolvedMediaAsset Asset(MediaAssetSource source, bool fromCache) =>
        new(
            source.Bytes,
            source.ContentType,
            Convert.ToHexStringLower(SHA256.HashData(source.Bytes)),
            source.ETag,
            source.LastModified,
            fromCache);

    private static void Validate(MediaAssetIdentity identity, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (maximumBytes is <= 0 or > 16 * 1024 * 1024 ||
            string.IsNullOrWhiteSpace(identity.ProviderId) ||
            string.IsNullOrWhiteSpace(identity.ResourceKind) ||
            string.IsNullOrWhiteSpace(identity.ResourceId) ||
            identity.Width is <= 0 ||
            identity.Height is <= 0)
            throw new ArgumentException("A complete bounded media asset identity is required.", nameof(identity));
    }

    private sealed record MediaAssetDescriptor(
        string PayloadKey,
        string Sha256,
        string ContentType,
        int PayloadBytes,
        string? ETag,
        DateTimeOffset? LastModified);

    private sealed record MediaAssetPayload(byte[] Bytes);
}
