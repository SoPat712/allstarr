using allstarr.Core.Caching;
using System.Diagnostics;
using System.Reflection;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class MixCoverDiskReadConcurrencyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SixteenCachedCoversKeepTheirBytesAndHeadersWithoutBuildingWhileWriterIsBusy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-cover-burst-{Guid.NewGuid():N}");
        var options = new FileMediaCacheOptions(root);
        var clock = new SystemPlatformClock();
        var owner = Guid.NewGuid();
        var inputs = Enumerable.Range(0, 16).Select(_ => new MixCoverInput(Guid.NewGuid().ToString(), 1, "daily",
            Count: 1, Art: ["library:album"])).ToArray();
        using var bitmap = new SKBitmap(40, 30);
        bitmap.Erase(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        var picture = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        var expected = new List<(byte[] Bytes, string ContentType, string ETag, string CacheControl)>();
        try
        {
            using (var seed = new FileMediaApplicationCache(options, clock, NullLogger<FileMediaApplicationCache>.Instance))
            {
                var service = new MixCoverImageService(_ => throw new InvalidOperationException("Unexpected render"),
                    (_, _) => Task.FromResult<byte[]?>(picture), cache: seed);
                foreach (var input in inputs)
                {
                    var context = new DefaultHttpContext();
                    var result = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, context, MixCoverImageService.Version(input), owner));
                    expected.Add((result.FileContents, result.ContentType, context.Response.Headers.ETag.ToString(), context.Response.Headers.CacheControl.ToString()));
                }
                await seed.FlushWritesAsync();
            }
            using var disk = new FileMediaApplicationCache(options, clock, NullLogger<FileMediaApplicationCache>.Instance);
            var builds = 0;
            var loads = 0;
            var restarted = new MixCoverImageService(_ => throw new InvalidOperationException("Unexpected render"), async (_, _) =>
            {
                Interlocked.Increment(ref loads);
                await Task.Delay(1200);
                return picture;
            }, cache: disk);
            var gate = (SemaphoreSlim)typeof(FileMediaApplicationCache).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(disk)!;
            await gate.WaitAsync();
            var elapsed = new double[inputs.Length];
            try
            {
                await disk.SetStringAsync("artwork:payload:v1:background", new string('x', 65536));
                Assert.False(disk.FlushWritesAsync().IsCompleted);
                await Task.WhenAll(inputs.Select(async (input, index) =>
                {
                    var timer = Stopwatch.StartNew();
                    var context = new DefaultHttpContext();
                    var response = await restarted.TryCreateCachedResponseAsync(Guid.Parse(input.Id), owner, MixCoverImageService.Version(input), context);
                    if (response == null)
                    {
                        Interlocked.Increment(ref builds);
                        response = await restarted.CreateResponseAsync(input, context, MixCoverImageService.Version(input), owner);
                    }
                    elapsed[index] = timer.Elapsed.TotalMilliseconds;
                    var result = Assert.IsType<FileContentResult>(response);
                    Assert.Equal(expected[index].Bytes, result.FileContents);
                    Assert.Equal(expected[index].ContentType, result.ContentType);
                    Assert.Equal(expected[index].ETag, context.Response.Headers.ETag.ToString());
                    Assert.Equal(expected[index].CacheControl, context.Response.Headers.CacheControl.ToString());
                })).WaitAsync(TimeSpan.FromSeconds(15));
                output.WriteLine($"16 cached versioned covers with busy writer: builds={builds}; artwork-loads={loads}; min={elapsed.Min():F2} ms; max={elapsed.Max():F2} ms.");
                Assert.Equal(0, builds);
                Assert.Equal(0, loads);
            }
            finally
            {
                gate.Release();
                await disk.FlushWritesAsync();
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
