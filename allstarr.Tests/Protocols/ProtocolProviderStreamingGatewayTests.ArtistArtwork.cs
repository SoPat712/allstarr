using allstarr.Core.Caching;
using allstarr.Core.Capabilities;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task SearchAndStreamReplaceLearnedArtistPlaceholderWithoutFetchingPhotos()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new TestMemoryApplicationCache();
        var context = Context();
        var identity = Guid.NewGuid().ToString();
        var missing = Enumerable.Range(1, 3).Select(i => new Artist
        {
            Id = $"ext-qobuz-artist-{i}",
            ExternalProvider = "qobuz",
            ExternalId = i.ToString(),
            Name = i == 1 ? "Say Now" : "Other " + i,
            ProviderIds = new() { ["MusicBrainzArtist"] = identity },
            ImageUrl = $"https://images.example.test/qobuz-{i}.png"
        }).ToArray();
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], missing);
        foreach (var artist in missing)
            await ArtistArtworkCache.ObserveAsync(cache, "qobuz", artist.ExternalId!, artist.ImageUrl!, [4, 5, 6]);
        var source = DelayedSearchProvider("qobuz", clock, TimeSpan.Zero);
        var real = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        source.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("qobuz",
                [new(new("qobuz", ProviderResourceKind.Artist, "1"), "Say Now",
                    artwork: new(publicUri: new("https://images.example.test/qobuz-1.png")),
                    providerIds: new Dictionary<string, string> { ["MusicBrainzArtist"] = identity })])));
        real.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Artist, "real"), "Say Now",
                    artwork: new(publicUri: new("https://images.example.test/real.png")),
                    providerIds: new Dictionary<string, string> { ["MusicBrainzArtist"] = identity })])));
        var gateway = BudgetGateway(clock, cache, source.Object, real.Object);
        var merged = await gateway.SearchAsync(context, "say now", 0, 0, 10);
        Assert.All(merged.Artists, artist => Assert.Equal("https://images.example.test/real.png", artist.ImageUrl));
        var streamed = new List<SearchResult>();
        await gateway.StreamSearchAsync(context, "say now", 10, false, false, true, false,
            (_, result, _, _) => { streamed.Add(result); return Task.CompletedTask; });
        Assert.All(streamed.SelectMany(result => result.Artists), artist => Assert.Equal("https://images.example.test/real.png", artist.ImageUrl));
        VerifySearchCount(source, "artists", 1);
        VerifySearchCount(real, "artists", 1);
        source.Verify(item => item.GetArtistAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistLookupRequest>()), Times.Never);
        real.Verify(item => item.GetArtistAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistLookupRequest>()), Times.Never);
    }

    [Fact]
    public async Task SearchAndStreamReplaceLearnedArtistPlaceholderThroughOverlappingAlbumWhenNoIdentifiersExist()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new TestMemoryApplicationCache();
        var context = Context();
        var missing = Enumerable.Range(1, 3).Select(i => new Artist
        {
            Id = $"ext-qobuz-artist-{i}",
            ExternalProvider = "qobuz",
            ExternalId = i.ToString(),
            Name = i == 1 ? "Say Now" : "Other " + i,
            ImageUrl = $"https://images.example.test/qobuz-{i}.png"
        }).ToArray();
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], missing);
        foreach (var artist in missing)
            await ArtistArtworkCache.ObserveAsync(cache, "qobuz", artist.ExternalId!, artist.ImageUrl!, [4, 5, 6]);
        var source = DelayedSearchProvider("qobuz", clock, TimeSpan.Zero);
        var real = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        source.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("qobuz",
                [new(new("qobuz", ProviderResourceKind.Artist, "1"), "Say Now",
                    artwork: new(publicUri: new("https://images.example.test/qobuz-1.png")))])));
        source.Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("qobuz",
                [new(new("qobuz", ProviderResourceKind.Album, "album-1"), "S.O.S.", [new("Say Now")])])));
        real.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Artist, "real"), "Say Now",
                    artwork: new(publicUri: new("https://images.example.test/real.png")))])));
        real.Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Album, "album-2"), "s.o.s.", [new("say now")])])));
        var gateway = BudgetGateway(clock, cache, source.Object, real.Object);
        var merged = await gateway.SearchAsync(context, "say now", 0, 10, 10);
        Assert.All(merged.Artists, artist => Assert.Equal("https://images.example.test/real.png", artist.ImageUrl));
    }
}
