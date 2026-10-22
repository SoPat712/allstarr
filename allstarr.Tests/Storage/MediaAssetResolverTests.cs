using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace allstarr.Tests;

public sealed class MediaAssetResolverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuppliedThumbnailsNeverReplaceTheFullResolutionDescriptor(bool thumbnailFirst)
    {
        static byte[] Png(int size, SKColor color)
        {
            using var bitmap = new SKBitmap(size, size);
            bitmap.Erase(color);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
        var fullBytes = Png(600, SKColors.Blue);
        var thumbnailBytes = Png(96, SKColors.Red);
        var cache = new TestMemoryApplicationCache();
        var resolver = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance);
        var identity = Identity(Guid.NewGuid()) with { Width = null, Height = null };
        var fullFetches = 0;
        Task<ResolvedMediaAsset?> Full() => resolver.ResolveAsync(identity, _ =>
        {
            fullFetches++;
            return Task.FromResult<MediaAssetSource?>(new(fullBytes, "image/png"));
        }, 1024 * 1024);
        if (!thumbnailFirst) await Full();
        var thumbnail = await resolver.ResolveAsync(identity with { Width = 96, Height = 96 },
            _ => Task.FromResult<MediaAssetSource?>(new(thumbnailBytes, "image/png", IsSizeSpecific: true)), 1024 * 1024);
        Assert.Equal(thumbnailBytes, thumbnail!.Bytes);
        Assert.Equal(fullBytes, (await Full())!.Bytes);
        Assert.Equal(fullBytes, (await Full())!.Bytes);
        Assert.Equal(1, fullFetches);
    }

    [Fact]
    public void ScopedDescriptorsAndPayloadsUsePersistentMediaTier()
    {
        Assert.Equal(
            ApplicationCacheStorageTier.Media,
            ApplicationCachePolicyRegistry.Resolve(
                CacheKeyBuilder.BuildMediaAssetDescriptorKey(Identity(Guid.CreateVersion7()))).StorageTier);
        Assert.Equal(
            ApplicationCacheStorageTier.Media,
            ApplicationCachePolicyRegistry.Resolve(
                CacheKeyBuilder.BuildMediaAssetPayloadKey(new string('a', 64))).StorageTier);
    }

    [Fact]
    public async Task ConcurrentScopedRequestsShareFetchAndContentAddressedPayload()
    {
        var cache = new TestMemoryApplicationCache();
        var metrics = new ApplicationCacheActivityMetrics();
        var resolver = new MediaAssetResolver(
            cache,
            NullLogger<MediaAssetResolver>.Instance,
            metrics);
        var identity = Identity(Guid.CreateVersion7());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;

        async Task<MediaAssetSource?> Fetch(CancellationToken _)
        {
            Interlocked.Increment(ref fetches);
            await release.Task;
            return new([1, 2, 3, 4], "image/png", "\"etag\"");
        }

        var first = resolver.ResolveAsync(identity, Fetch, 1024);
        var second = resolver.ResolveAsync(identity, Fetch, 1024);
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        var cached = await resolver.ResolveAsync(identity, _ => throw new InvalidOperationException(), 1024);

        Assert.Equal(1, fetches);
        Assert.All(results, result => Assert.False(result!.FromCache));
        Assert.True(cached!.FromCache);
        Assert.Equal("\"etag\"", cached.ETag);
        Assert.Equal(1, metrics.Snapshot().CoalescedRequests);
        Assert.Equal(4, metrics.Snapshot().UpstreamBytesAvoided);
        Assert.Single(cache.GetKeysByPattern("media:descriptor:v5:*"));
        Assert.Single(cache.GetKeysByPattern("artwork:payload:v1:*"));
    }

    [Fact]
    public async Task UserScopesKeepDescriptorsSeparateWhileIdenticalBytesDeduplicate()
    {
        var cache = new TestMemoryApplicationCache();
        var resolver = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance);
        var bytes = new byte[] { 4, 3, 2, 1 };

        await resolver.ResolveAsync(
            Identity(Guid.CreateVersion7()),
            _ => Task.FromResult<MediaAssetSource?>(new(bytes, "image/jpeg")),
            1024);
        await resolver.ResolveAsync(
            Identity(Guid.CreateVersion7()),
            _ => Task.FromResult<MediaAssetSource?>(new(bytes, "image/jpeg")),
            1024);

        Assert.Equal(2, cache.GetKeysByPattern("media:descriptor:v5:*").Count());
        Assert.Single(cache.GetKeysByPattern("artwork:payload:v1:*"));
        Assert.DoesNotContain(cache.GetKeysByPattern("*"), key =>
            key.Contains("user-avatar-id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequestedDimensionsCreateLazyVariantAndKeepOriginal()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            8, 4, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var original = encoded.ToArray();
        var cache = new TestMemoryApplicationCache();
        var resolver = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance);
        var identity = Identity(Guid.CreateVersion7()) with { Width = 2 };

        var result = await resolver.ResolveAsync(
            identity,
            _ => Task.FromResult<MediaAssetSource?>(new(original, "image/png")),
            1024 * 1024);
        var cached = await resolver.ResolveAsync(
            identity, _ => throw new InvalidOperationException(), 1024 * 1024);

        using var resized = SKBitmap.Decode(result!.Bytes);
        Assert.Equal(2, resized.Width);
        Assert.Equal(1, resized.Height);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.True(cached!.FromCache);
        Assert.Equal(2, cache.GetKeysByPattern("media:descriptor:v5:*").Count());
        Assert.Equal(2, cache.GetKeysByPattern("artwork:payload:v1:*").Count());
    }

    private static MediaAssetIdentity Identity(Guid userId) => new(
        userId,
        null,
        "jellyfin",
        "user-avatar",
        "user-avatar-id",
        "server-revision",
        Width: 96);
}
