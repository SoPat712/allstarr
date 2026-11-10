using System.Diagnostics;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class MixCoverDiskCacheTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mix-cover-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(DateTimeOffset.UtcNow);

    private FileMediaApplicationCache Cache(long maximumBytes = 1024 * 1024) => new(
        new FileMediaCacheOptions(_root, MaximumBytes: maximumBytes, MaximumEntryBytes: 1024 * 1024),
        _clock, NullLogger<FileMediaApplicationCache>.Instance);

    [Fact]
    public async Task NoArtworkDiskHitKeepsShortPrivateCachingWithoutAValidator()
    {
        using var cache = Cache();
        var renders = 0;
        MixCoverImageService Service() => new(_ => { renders++; return [9]; }, cache: cache);
        var input = new MixCoverInput("mix", 1, "daily", Count: 17);
        await Service().CreateResponseAsync(input, new DefaultHttpContext());
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = $"\"{MixCoverImageService.GetTag(input)}\"";
        Assert.IsType<FileContentResult>(await Service().CreateResponseAsync(input, context, MixCoverImageService.Version(input)));
        Assert.Equal(1, renders);
        Assert.Equal("private, max-age=60", context.Response.Headers.CacheControl);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        _clock.UtcNow += TimeSpan.FromSeconds(61);
        await Service().CreateResponseAsync(input, new DefaultHttpContext());
        Assert.Equal(2, renders);
    }

    [Fact]
    public async Task ServiceArtworkIsFetchedOnceAcrossServiceInstances()
    {
        using var cache = Cache();
        var fetches = 0;
        MixCoverImageService Service() => new(_ => [9], cache: cache, serviceArt: (_, _, _) =>
        {
            fetches++;
            return Task.FromResult<ResolvedMediaAsset?>(new(Picture(), "image/png", "hash", null, null, false));
        });
        var input = new MixCoverInput("mix", 1, "external", ServiceArtwork: "saved-art");
        var owner = Guid.NewGuid();
        var context = new DefaultHttpContext();
        var first = Assert.IsType<FileContentResult>(await Service().CreateResponseAsync(input, context, owner: owner));
        var second = Assert.IsType<FileContentResult>(await Service().CreateResponseAsync(input, new DefaultHttpContext(), owner: owner));
        Assert.Equal(first.FileContents, second.FileContents);
        Assert.Equal(1, fetches);
        await Service().CreateResponseAsync(input with { Generation = 2 }, new DefaultHttpContext(), owner: owner);
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task CompleteCoverSurvivesRestartAndVersionChangeRendersAgain()
    {
        var input = new MixCoverInput("mix", 1, "daily", Count: 4, Art: ["library:album"]);
        var bytes = Picture();
        var loads = 0;
        async Task<byte[]?> Load(string reference, CancellationToken token)
        {
            Interlocked.Increment(ref loads);
            await Task.Delay(150, token);
            return bytes;
        }
        using var cache = Cache();
        var service = new MixCoverImageService(MixCoverRenderer.Render, Load, cache: cache);
        var timer = Stopwatch.StartNew();
        var first = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, new DefaultHttpContext()));
        var cold = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(MixCoverRenderer.RenderCollage([bytes]), first.FileContents);
        using var restartedCache = Cache();
        var restarted = new MixCoverImageService(_ => throw new InvalidOperationException(), Load, cache: restartedCache);
        timer.Restart();
        var repeated = Assert.IsType<FileContentResult>(await restarted.CreateResponseAsync(input, new DefaultHttpContext()));
        var warm = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(first.FileContents, repeated.FileContents);
        Assert.Equal(1, loads);
        var conditional = new DefaultHttpContext();
        conditional.Request.Headers.IfNoneMatch = $"\"{MixCoverImageService.GetTag(input)}\"";
        Assert.Equal(304, Assert.IsType<StatusCodeResult>(await restarted.CreateResponseAsync(input, conditional)).StatusCode);
        Assert.Equal(1, loads);
        await restarted.CreateResponseAsync(input with { Generation = 2 }, new DefaultHttpContext());
        Assert.Equal(2, loads);
        output.WriteLine($"Mix cover: uncached {cold:F2} ms; restarted disk hit {warm:F2} ms; backend calls 1 -> 0 per repeat.");
    }

    [Fact]
    public async Task ConcurrentColdRequestsShareRenderAndWarmupRunsOneCoverAtATime()
    {
        using var cache = Cache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var active = 0;
        var maximumActive = 0;
        var service = new MixCoverImageService(_ => [9], async (_, _) =>
        {
            Interlocked.Increment(ref loads);
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            entered.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
            return Picture();
        }, cache: cache);
        var input = new MixCoverInput("mix", 1, "daily", Count: 1, Art: ["library:album"]);
        var requests = Enumerable.Range(0, 10).Select(_ => service.CreateResponseAsync(input, new DefaultHttpContext())).ToArray();
        await entered.Task;
        release.SetResult();
        await Task.WhenAll(requests);
        Assert.Equal(1, loads);
        await service.WarmAsync([input with { Generation = 2 }, input with { Generation = 3 }], Guid.NewGuid());
        Assert.Equal(3, loads);
        Assert.Equal(1, maximumActive);
        using var afterRestart = Cache();
        var restarted = new MixCoverImageService(_ => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(), cache: afterRestart);
        await restarted.CreateResponseAsync(input with { Generation = 3 }, new DefaultHttpContext());
    }

    [Fact]
    public async Task DiskQuotaEvictsLeastRecentlyUsedCover()
    {
        using var cache = Cache(700);
        var renders = 0;
        MixCoverImageService Service() => new(_ => { renders++; return new byte[120]; }, cache: cache);
        var input = new MixCoverInput("mix", 1, "daily");
        await Service().CreateResponseAsync(input, new DefaultHttpContext());
        _clock.UtcNow += TimeSpan.FromSeconds(1);
        await Service().CreateResponseAsync(input with { Id = "second" }, new DefaultHttpContext());
        _clock.UtcNow += TimeSpan.FromSeconds(1);
        await Service().CreateResponseAsync(input, new DefaultHttpContext());
        _clock.UtcNow += TimeSpan.FromSeconds(1);
        await Service().CreateResponseAsync(input with { Id = "third" }, new DefaultHttpContext());
        await Service().CreateResponseAsync(input, new DefaultHttpContext());
        Assert.Equal(3, renders);
        await Service().CreateResponseAsync(input with { Id = "second" }, new DefaultHttpContext());
        Assert.Equal(4, renders);
    }

    private static byte[] Picture()
    {
        using var bitmap = new SKBitmap(40, 30);
        bitmap.Erase(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private sealed class TestClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
