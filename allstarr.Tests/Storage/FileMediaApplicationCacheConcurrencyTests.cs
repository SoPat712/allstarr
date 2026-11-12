using allstarr.Core.Caching;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using allstarr.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class FileMediaApplicationCacheConcurrencyTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"allstarr-concurrent-media-{Guid.NewGuid():N}");

    private FileMediaApplicationCache Cache() => new(new FileMediaCacheOptions(_root),
        new SystemPlatformClock(), NullLogger<FileMediaApplicationCache>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SixteenDifferentImagesHitWhileBackgroundWriterIsBusy(bool busyWriter)
    {
        using var cache = Cache();
        var keys = Enumerable.Range(0, 16).Select(index => $"artwork:payload:v1:burst-{index}").ToArray();
        var values = keys.Select((_, index) => new string((char)('a' + index), 112 * 1024)).ToArray();
        for (var index = 0; index < keys.Length; index++)
            Assert.True(await cache.SetStringAsync(keys[index], values[index]));
        await cache.FlushWritesAsync();
        if (busyWriter)
        {
            foreach (var path in Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories))
            {
                var metadata = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
                metadata.Remove("payloadHash");
                await File.WriteAllTextAsync(path, metadata.ToJsonString());
            }
        }
        var gate = (SemaphoreSlim)typeof(FileMediaApplicationCache).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        if (busyWriter) await gate.WaitAsync();
        var fallbacks = 0;
        var elapsed = new double[keys.Length];
        try
        {
            if (busyWriter)
            {
                Assert.True(await cache.SetStringAsync("artwork:payload:v1:background", new string('x', 65536)));
                Assert.False(cache.FlushWritesAsync().IsCompleted);
            }
            await Task.WhenAll(keys.Select(async (key, index) =>
            {
                var timer = Stopwatch.StartNew();
                var value = await cache.GetStringAsync(key);
                if (value == null)
                {
                    Interlocked.Increment(ref fallbacks);
                    await Task.Delay(1200);
                    value = values[index];
                }
                elapsed[index] = timer.Elapsed.TotalMilliseconds;
                Assert.Equal(values[index], value);
            })).WaitAsync(TimeSpan.FromSeconds(10));
            output.WriteLine($"16 different 112 KiB images: busy-writer={busyWriter}; hits={16 - fallbacks}; fallbacks={fallbacks}; min={elapsed.Min():F2} ms; max={elapsed.Max():F2} ms.");
            Assert.Equal(0, fallbacks);
        }
        finally
        {
            if (busyWriter) gate.Release();
            await cache.FlushWritesAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MediaReadsKeepLegacyEntriesAndRejectMismatchedPayloads(bool legacy)
    {
        const string key = "artwork:payload:v1:consistency";
        using var cache = Cache();
        Assert.True(await cache.SetStringAsync(key, "original"));
        await cache.FlushWritesAsync();
        var sidecar = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(sidecar))!.AsObject();
        if (legacy)
        {
            metadata.Remove("payloadHash");
            await File.WriteAllTextAsync(sidecar, metadata.ToJsonString());
        }
        else Assert.NotNull(metadata["payloadHash"]);
        Assert.Equal("original", await cache.GetStringAsync(key));
        await cache.FlushWritesAsync();
        if (!legacy)
        {
            await File.WriteAllTextAsync(Path.ChangeExtension(sidecar, ".payload"), "replaced");
            Assert.Null(await cache.GetStringAsync(key));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
