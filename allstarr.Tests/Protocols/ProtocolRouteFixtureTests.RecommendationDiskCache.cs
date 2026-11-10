using System.Diagnostics;
using System.Net;
using allstarr.Core.Operations;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("/Songs/seed/Similar?limit=2")]
    [InlineData("/Items/seed/InstantMix?limit=2")]
    public async Task RecommendationDiskCacheSurvivesRestartWithIdenticalResponse(string path)
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-recommendations-{Guid.NewGuid():N}");
        var backendCalls = 0;
        var verificationCalls = 0;
        var clock = new SystemPlatformClock();
        try
        {
            byte[]? expected = null;
            for (var instance = 0; instance < 4; instance++)
            {
                using var memory = new MemoryApplicationCache(clock);
                using var disk = new FileMediaApplicationCache(new FileMediaCacheOptions(root), clock, NullLogger<FileMediaApplicationCache>.Instance);
                IApplicationCache cache = instance < 2 ? memory : new HybridApplicationCache(memory, disk, clock: clock);
                using var factory = new ProtocolFactory("Jellyfin", request =>
                {
                    if (IsItemLookup(request, "seed")) return ItemLookup("""{"Id":"seed","Type":"Audio"}""");
                    if (request.RequestUri!.AbsolutePath == "/Users/Me")
                    {
                        Interlocked.Increment(ref verificationCalls);
                        Thread.Sleep(150);
                        return Json(200, """{"Id":"listener"}""");
                    }
                    if (request.RequestUri.AbsolutePath.EndsWith("/Similar", StringComparison.Ordinal) ||
                        request.RequestUri.AbsolutePath.EndsWith("/InstantMix", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref backendCalls);
                        Thread.Sleep(800);
                        return Json(200, """{"Items":[{"Id":"second","Type":"Audio","Name":"Second","Future":{"a":2}},{"Id":"first","Type":"Audio","Name":"First"}],"TotalRecordCount":2,"StartIndex":0}""");
                    }
                    return Json(200, """{"Items":[],"TotalRecordCount":0}""");
                }, services =>
                {
                    services.RemoveAll<IApplicationCache>();
                    services.AddSingleton<IApplicationCache>(cache);
                });
                using var client = factory.CreateClient();
                client.DefaultRequestHeaders.Add("X-Emby-Token", "fixture");
                var timer = Stopwatch.StartNew();
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(Math.Min(instance + 1, 3), backendCalls);
                var body = await response.Content.ReadAsByteArrayAsync();
                if (instance == 0) expected = body;
                else Assert.Equal(expected, body);
                _lyricsSearchOutput.WriteLine($"{path}: mode={(instance < 2 ? "memory" : "disk")} instance={instance % 2} elapsed={timer.Elapsed.TotalMilliseconds:F2} ms backend-calls={backendCalls}");
            }
            Assert.Equal(3, backendCalls);
            Assert.Equal(4, verificationCalls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
