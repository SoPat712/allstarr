using allstarr.Core.Music;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed class MixCoverImageServiceTests
{
    [Fact]
    public async Task ConcurrentRequestsRenderOnceAndCacheEvictsWithinBothLimits()
    {
        var renders = 0;
        var service = new MixCoverImageService(_ => { Interlocked.Increment(ref renders); return [1, 2, 3]; }, 2, 6);
        var input = new MixCoverInput("mix-1", 1, "daily");
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            Assert.IsType<FileContentResult>(service.CreateResponse(input, new DefaultHttpContext())))));
        Assert.Equal(1, renders);
        service.CreateResponse(input with { Id = "mix-2" }, new DefaultHttpContext());
        service.CreateResponse(input with { Id = "mix-3" }, new DefaultHttpContext());
        service.CreateResponse(input, new DefaultHttpContext());
        Assert.Equal(4, renders);
        var byteLimited = new MixCoverImageService(_ => { renders++; return [1, 2, 3]; }, 8, 3);
        byteLimited.CreateResponse(input, new DefaultHttpContext());
        byteLimited.CreateResponse(input with { Id = "mix-2" }, new DefaultHttpContext());
        byteLimited.CreateResponse(input, new DefaultHttpContext());
        Assert.Equal(7, renders);
    }

    [Fact]
    public void ConditionalRequestSkipsRenderingAndTagIncludesAllInputs()
    {
        var service = new MixCoverImageService(_ => throw new InvalidOperationException("Unexpected render"));
        var input = new MixCoverInput("mix-1", 1, "daily");
        var tag = MixCoverImageService.GetTag(input);
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = $"\"{tag}\"";
        Assert.Equal(304, Assert.IsType<StatusCodeResult>(service.CreateResponse(input, context)).StatusCode);
        var changes = new[]
        {
            input with { Id = "mix-2" }, input with { Generation = 2 }, input with { Kind = "weekly" },
            input with { Tag = "tag" }, input with { Artists = "artist" }, input with { Seed = "seed" },
            input with { Source = "source" }, input with { SourcePlaylistId = "playlist" },
            input with { Name = "name" }, input with { Count = 1 }
        };
        Assert.All(changes, changed => Assert.NotEqual(tag, MixCoverImageService.GetTag(changed)));
    }
}
