using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace allstarr.Tests;

public sealed class MediaAssetResolverTests
{
    [Fact]
    public void ScopedDescriptorsUseMetadataTierAndPayloadsUseMediaTier()
    {
        Assert.Equal(
            ApplicationCacheStorageTier.Metadata,
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
        var resolver = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance);
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
        Assert.Single(cache.GetKeysByPattern("media:descriptor:v1:*"));
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

        Assert.Equal(2, cache.GetKeysByPattern("media:descriptor:v1:*").Count());
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
        Assert.Equal(2, cache.GetKeysByPattern("artwork:payload:v1:*").Count());
    }

    private static MediaAssetIdentity Identity(Guid userId) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        userId,
        null,
        "jellyfin",
        "user-avatar",
        "user-avatar-id",
        "server-revision",
        Width: 96);
}
