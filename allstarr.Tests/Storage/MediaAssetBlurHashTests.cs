using allstarr.Core.Operations;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace allstarr.Tests;

public sealed class MediaAssetBlurHashTests
{
    [Fact]
    public async Task ArtworkFetchStoresHashForReadOnlyScopedLookup()
    {
        var cache = new TestMemoryApplicationCache();
        var resolver = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance);
        var identity = Identity();
        Assert.Null(await resolver.GetCachedBlurHashAsync(identity));
        Assert.Empty(cache.GetKeysByPattern("*"));

        var bytes = BlackPng();
        await resolver.ResolveAsync(identity,
            _ => Task.FromResult<MediaAssetSource?>(new(bytes, "image/png")), 1024);
        var descriptorKey = CacheKeyBuilder.BuildMediaAssetDescriptorKey(identity);
        var payloadKey = CacheKeyBuilder.BuildMediaAssetPayloadKey(Convert.ToHexStringLower(SHA256.HashData(bytes)));

        Assert.Equal("L00000fQfQfQfQfQfQfQfQfQfQfQ", await resolver.GetCachedBlurHashAsync(identity));
        Assert.Equal(await resolver.GetCachedBlurHashAsync(identity), await resolver.GetCachedBlurHashAsync(descriptorKey));
        Assert.Equal(await resolver.GetCachedBlurHashAsync(identity), await resolver.GetCachedBlurHashAsync(payloadKey));
        Assert.Null(await resolver.GetCachedBlurHashAsync(identity with { UserId = Guid.CreateVersion7() }));
        Assert.Null(await resolver.GetCachedBlurHashAsync("https://example.com/cover.png"));

        var payloadBefore = await cache.GetStringAsync(payloadKey);
        var descriptorBefore = await cache.GetStringAsync(descriptorKey);
        await resolver.ResolveAsync(identity, _ => throw new InvalidOperationException(), 1024);
        Assert.Equal(payloadBefore, await cache.GetStringAsync(payloadKey));
        Assert.Equal(descriptorBefore, await cache.GetStringAsync(descriptorKey));
    }

    [Fact]
    public async Task SizedArtworkKeepsCanonicalHashWhenNoResizeIsNeeded()
    {
        var cache = new TestMemoryApplicationCache();
        var resolver = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance);
        var identity = Identity() with { Width = 96 };
        await resolver.ResolveAsync(identity,
            _ => Task.FromResult<MediaAssetSource?>(new(BlackPng(), "image/png")), 1024);

        Assert.Equal("L00000fQfQfQfQfQfQfQfQfQfQfQ", await resolver.GetCachedBlurHashAsync(identity with { Width = null }));
    }

    [Fact]
    public async Task LegacyPayloadComputesHashOnlyOnOrdinaryArtworkRead()
    {
        var cache = new TestMemoryApplicationCache();
        var resolver = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance);
        var identity = Identity();
        var bytes = BlackPng();
        var payloadKey = CacheKeyBuilder.BuildMediaAssetPayloadKey(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var descriptorKey = CacheKeyBuilder.BuildMediaAssetDescriptorKey(identity);
        await resolver.ResolveAsync(identity,
            _ => Task.FromResult<MediaAssetSource?>(new(bytes, "image/png")), 1024);
        var payload = JsonNode.Parse((await cache.GetStringAsync(payloadKey))!)!.AsObject();
        payload.Remove("BlurHash");
        payload.Remove("BlurHashComputed");
        await cache.SetStringAsync(payloadKey, payload.ToJsonString());
        var descriptor = JsonNode.Parse((await cache.GetStringAsync(descriptorKey))!)!.AsObject();
        descriptor.Remove("BlurHash");
        await cache.SetStringAsync(descriptorKey, descriptor.ToJsonString());

        Assert.Null(await resolver.GetCachedBlurHashAsync(identity));
        Assert.Equal(payload.ToJsonString(), await cache.GetStringAsync(payloadKey));
        var cached = await resolver.ResolveAsync(identity, _ => throw new InvalidOperationException(), 1024);
        Assert.True(cached!.FromCache);
        Assert.Equal("L00000fQfQfQfQfQfQfQfQfQfQfQ", await resolver.GetCachedBlurHashAsync(identity));
    }

    [Fact]
    public async Task HashPersistsAcrossFileCacheAndResolverRecreation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-artwork-{Guid.CreateVersion7():N}");
        var options = new FileMediaCacheOptions(root, MaximumBytes: 64_000, MaximumEntryBytes: 32_000);
        var identity = Identity() with { Width = 2 };
        try
        {
            using (var cache = new FileMediaApplicationCache(options, new Clock(), NullLogger<FileMediaApplicationCache>.Instance))
            {
                var resolver = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance);
                await resolver.ResolveAsync(identity,
                    _ => Task.FromResult<MediaAssetSource?>(new(BlackPng(), "image/png")), 1024);
            }

            using var restartedCache = new FileMediaApplicationCache(options, new Clock(), NullLogger<FileMediaApplicationCache>.Instance);
            var restarted = new MediaAssetResolver(restartedCache, NullLogger<MediaAssetResolver>.Instance);
            Assert.Equal("L00000fQfQfQfQfQfQfQfQfQfQfQ", await restarted.GetCachedBlurHashAsync(identity));
            Assert.Equal(await restarted.GetCachedBlurHashAsync(identity),
                await restarted.GetCachedBlurHashAsync(identity with { Width = null }));
            Assert.True((await restarted.ResolveAsync(identity, _ => throw new InvalidOperationException(), 1024))!.FromCache);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UnusableArtworkHasNoHashAndLookupDoesNotRepairMetadata()
    {
        var cache = new TestMemoryApplicationCache();
        var resolver = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance);
        var identity = Identity();
        var result = await resolver.ResolveAsync(identity,
            _ => Task.FromResult<MediaAssetSource?>(new([1, 2, 3], "image/png")), 1024);

        Assert.NotNull(result);
        Assert.Null(await resolver.GetCachedBlurHashAsync(identity));
        var key = CacheKeyBuilder.BuildMediaAssetDescriptorKey(identity);
        var descriptor = JsonNode.Parse((await cache.GetStringAsync(key))!)!.AsObject();
        descriptor.Remove("BlurHash");
        await cache.SetStringAsync(key, descriptor.ToJsonString());
        var before = await cache.GetStringAsync(key);
        Assert.Null(await resolver.GetCachedBlurHashAsync(identity));
        Assert.Equal(before, await cache.GetStringAsync(key));
    }

    private static MediaAssetIdentity Identity() => new(Guid.CreateVersion7(), null, "deezer", "song", "123");

    private static byte[] BlackPng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(4, 3, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.Black);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
