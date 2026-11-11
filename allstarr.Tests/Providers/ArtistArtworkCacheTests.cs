using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ArtistArtworkCacheTests
{
    private static ProtocolExecutionContext Context(Guid? owner = null) => new(ProtocolKind.Jellyfin, "library", "listener",
        new AllstarrPrincipal(owner ?? Guid.NewGuid(), "jellyfin", "library", "listener", "User", false),
        "artwork-test", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    private static Artist Photo(string provider, string id, string name, string? image = null, string? identity = null) => new()
    {
        Id = $"ext-{provider}-artist-{id}",
        ExternalProvider = provider,
        ExternalId = id,
        Name = name,
        ImageUrl = image ?? $"https://images.example.test/{provider}/{id}.png",
        ProviderIds = identity == null ? [] : new() { ["MusicBrainzArtist"] = identity }
    };

    [Fact]
    public async Task ArtistArtworkLearnsRepeatedBytesAndUsesMatchingProviderPhoto()
    {
        var cache = new TestMemoryApplicationCache();
        var context = Context();
        var identity = Guid.NewGuid().ToString();
        var missing = Photo("tidal", "one", "Say Now", identity: identity);
        var other = Photo("deezer", "real", "say now", identity: identity);
        var rows = new[] { missing, Photo("tidal", "two", "Other"), Photo("tidal", "three", "Third"), other };
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], rows);
        foreach (var artist in rows.Take(3))
            await ArtistArtworkCache.ObserveAsync(cache, "tidal", artist.ExternalId!, artist.ImageUrl!, [1, 2, 3]);
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], rows);
        Assert.Equal(other.ImageUrl, missing.ImageUrl);
        Assert.Null(rows[1].ImageUrl);
        Assert.Null(rows[2].ImageUrl);
        Assert.Equal(other.ImageUrl, (await ArtistArtworkCache.ResolveResourceAsync(cache, context, "tidal", "one", null)));
        Assert.Null(await ArtistArtworkCache.ResolveResourceAsync(cache, context, "tidal", "two", null));
    }

    [Fact]
    public async Task ArtistArtworkKeepsDistinctPhotosAndDoesNotCountRepeatRequestsAsArtists()
    {
        var cache = new TestMemoryApplicationCache();
        var context = Context();
        var rows = Enumerable.Range(0, 4).Select(i => Photo("spotify", i.ToString(), "Artist " + i)).ToArray();
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], rows);
        foreach (var artist in rows)
            await ArtistArtworkCache.ObserveAsync(cache, "spotify", artist.ExternalId!, artist.ImageUrl!, [(byte)artist.ExternalId![0]]);
        for (var i = 0; i < 4; i++)
            await ArtistArtworkCache.ObserveAsync(cache, "spotify", "0", rows[0].ImageUrl!, [48]);
        var expected = rows.Select(item => item.ImageUrl).ToArray();
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], rows);
        Assert.Equal(expected, rows.Select(item => item.ImageUrl));
    }

    [Fact]
    public async Task ArtistArtworkDoesNotSubstituteNamesakesOrAnotherListenersLibraryPhoto()
    {
        var cache = new TestMemoryApplicationCache();
        var context = Context();
        var identity = Guid.NewGuid().ToString();
        var missing = Photo("qobuz", "one", "Say Now", "https://images.example.test/default_artist.png", identity);
        var namesake = Photo("deezer", "other", "Say Now", identity: Guid.NewGuid().ToString());
        var native = new Dictionary<string, object?>
        {
            ["Id"] = "native",
            ["Name"] = "Say Now",
            ["ImageTags"] = new Dictionary<string, string> { ["Primary"] = "photo" },
            ["ProviderIds"] = new Dictionary<string, string> { ["MusicBrainzArtist"] = identity }
        };
        await ArtistArtworkCache.PrepareSearchAsync(cache, Context(), [native], []);
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], [missing, namesake]);
        Assert.Null(missing.ImageUrl);
        Assert.NotNull(namesake.ImageUrl);
        missing.ProviderIds = namesake.ProviderIds = new() { ["MusicBrainzArtist"] = "unknown" };
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], [missing, namesake]);
        Assert.Null(missing.ImageUrl);
        missing.ProviderIds = new() { ["MusicBrainzArtist"] = identity };
        missing.ImageUrl = "https://images.example.test/default_artist.png";
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [native], [missing]);
        Assert.Equal("/Items/native/Images/Primary?tag=photo", missing.ImageUrl);
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], [missing]);
        Assert.Null(await ArtistArtworkCache.ResolveResourceAsync(cache, null, "qobuz", "one", null));
    }

    [Fact]
    public async Task ArtistArtworkRequiresThreeDifferentNormalizedNamesFromOneProvider()
    {
        var cache = new TestMemoryApplicationCache();
        var context = Context();
        var rows = new[] { Photo("tidal", "one", "Say Now"), Photo("tidal", "alias", "say now"), Photo("tidal", "two", "Second"), Photo("qobuz", "three", "Third") };
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], rows);
        foreach (var artist in rows)
            await ArtistArtworkCache.ObserveAsync(cache, artist.ExternalProvider!, artist.ExternalId!, artist.ImageUrl!, [9, 9]);
        await ArtistArtworkCache.PrepareSearchAsync(cache, context, [], rows);
        Assert.All(rows, row => Assert.NotNull(row.ImageUrl));
    }
}
