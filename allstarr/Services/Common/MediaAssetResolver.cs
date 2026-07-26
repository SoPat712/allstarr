using System.Collections.Concurrent;
using System.Security.Cryptography;

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
    ILogger<MediaAssetResolver> logger) : IMediaAssetResolver
{
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
            return cached;

        var pending = _inflight.GetOrAdd(
            descriptorKey,
            _ => new Lazy<Task<ResolvedMediaAsset?>>(
                () => FetchAsync(descriptorKey, fetch, maximumBytes, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
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
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var payloadKey = CacheKeyBuilder.BuildMediaAssetPayloadKey(sha256);
            var policy = ApplicationCachePolicyRegistry.Resolve(ApplicationCacheCategory.Artwork);
            if (!await cache.SetAsync(payloadKey, new MediaAssetPayload(bytes), policy.FreshFor))
                return new(bytes, source.ContentType, sha256, source.ETag, source.LastModified, false);
            await cache.SetAsync(
                descriptorKey,
                new MediaAssetDescriptor(
                    payloadKey, sha256, source.ContentType, bytes.Length, source.ETag, source.LastModified),
                policy.FreshFor);
            return new(bytes, source.ContentType, sha256, source.ETag, source.LastModified, false);
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
