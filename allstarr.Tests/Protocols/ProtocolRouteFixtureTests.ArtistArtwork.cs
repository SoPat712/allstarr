using System.Diagnostics;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using SkiaSharp;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ArtistImageUsesMatchingLibraryPortraitAndPreservesItsCurrentTag()
    {
        var cache = new TestMemoryApplicationCache();
        var owner = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd");
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "user-1",
            new AllstarrPrincipal(owner, "jellyfin", "primary", "user-1", "Fixture User", false),
            "artist-photo", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var identity = Guid.NewGuid().ToString();
        var missing = new Artist
        {
            Id = "ext-qobuz-artist-owned",
            ExternalProvider = "qobuz",
            ExternalId = "owned",
            Name = "Say Now",
            ImageUrl = "https://images.example.test/default_artist.png",
            ProviderIds = new() { ["MusicBrainzArtist"] = identity }
        };
        var native = new Dictionary<string, object?>
        {
            ["Id"] = "library-artist",
            ["Name"] = "Say Now",
            ["ProviderIds"] = new Dictionary<string, string> { ["MusicBrainzArtist"] = identity },
            ["ImageTags"] = new Dictionary<string, string> { ["Primary"] = "first" }
        };
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [native], [missing]);
        var blue = CaseArtwork(SKColors.Blue);
        var red = CaseArtwork(SKColors.Red);
        var tag = "first";
        var images = 0;
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            Assert.Equal("/Items/library-artist/Images/Primary", request.RequestUri.AbsolutePath);
            Assert.Equal(tag, Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["tag"].ToString());
            images++;
            return new(HttpStatusCode.OK)
            { Content = new ByteArrayContent(tag == "first" ? blue : red) { Headers = { ContentType = new("image/png") } } };
        }, services =>
        {
            AddArtworkPrincipal(services, "user-1");
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(Mock.Of<IProtocolProviderGateway>(MockBehavior.Strict));
            services.RemoveAll<IApplicationCache>();
            services.AddSingleton<IApplicationCache>(cache);
        });
        using var client = factory.CreateClient();
        const string path = "/Items/ext-qobuz-artist-owned/Images/Primary?api_key=fixture-key";
        using var first = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(blue, await first.Content.ReadAsByteArrayAsync());
        tag = "second";
        native["ImageTags"] = new Dictionary<string, string> { ["Primary"] = tag };
        missing.ImageUrl = "https://images.example.test/default_artist.png";
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [native], [missing]);
        using var second = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(red, await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, images);
    }

    [Fact]
    public async Task ArtistImageLearnsSharedPlaceholderAndStopsServingPreviouslyCachedCopies()
    {
        var cache = new TestMemoryApplicationCache();
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "public", "public", null,
            "artist-photo", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var rows = Enumerable.Range(1, 4).Select(i => new Artist
        {
            Id = $"ext-tidal-artist-{i}",
            ExternalProvider = "tidal",
            ExternalId = i.ToString(),
            Name = "Different " + i,
            ImageUrl = $"https://images.example.test/artist-{(i == 4 ? 1 : i)}.png"
        }).ToArray();
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], rows);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetPublicArtworkUriAsync("tidal", ProviderResourceKind.Artist,
            It.IsAny<string>(), It.IsAny<CancellationToken>(), false, false))
            .ReturnsAsync((string _, ProviderResourceKind _, string id, CancellationToken _, bool _, bool _) =>
                new Uri($"https://images.example.test/artist-{(id == "4" ? "1" : id)}.png"));
        var bytes = CaseArtwork(SKColors.Gray);
        var fetches = 0;
        using var factory = new ProtocolFactory("Jellyfin", _ =>
        {
            Interlocked.Increment(ref fetches);
            Thread.Sleep(710);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/png") } } };
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IApplicationCache>();
            services.AddSingleton<IApplicationCache>(cache);
        });
        using var client = factory.CreateClient();
        var first = Stopwatch.StartNew();
        using var one = await client.GetAsync("/Items/ext-tidal-artist-1/Images/Primary");
        first.Stop();
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        using var two = await client.GetAsync("/Items/ext-tidal-artist-2/Images/Primary");
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);
        using var three = await client.GetAsync("/Items/ext-tidal-artist-3/Images/Primary");
        Assert.Equal(HttpStatusCode.NotFound, three.StatusCode);
        Assert.Equal(3, fetches);
        var learned = Stopwatch.StartNew();
        using var unseen = await client.GetAsync("/Items/ext-tidal-artist-4/Images/Primary");
        Assert.Equal(HttpStatusCode.NotFound, unseen.StatusCode);
        using var repeat = await client.GetAsync("/Items/ext-tidal-artist-1/Images/Primary");
        learned.Stop();
        Assert.Equal(HttpStatusCode.NotFound, repeat.StatusCode);
        Assert.Equal(3, fetches);
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], rows);
        Assert.All(rows, row => Assert.Null(row.ImageUrl));
        _lyricsSearchOutput.WriteLine($"Artist placeholder with 710 ms image source: unknown {first.Elapsed.TotalMilliseconds:F2} ms, learned {learned.Elapsed.TotalMilliseconds:F2} ms; image calls per request 1 -> 0.");
    }
}
