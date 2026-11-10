using allstarr.Core.Routing;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed partial class ProtocolLyricsAutomaticTests
{
    [Fact]
    public async Task BackgroundRefreshUsesAutomaticRoutingForExactSourceReads()
    {
        await using var fixture = new Fixture(new Source("lyricsplus", Result("lyricsplus", true)));
        var context = fixture.Protocol with { RefreshLyrics = true, IsReadOnly = true };
        Assert.NotNull(await fixture.Gateway.GetLyricsAsync(context, "lyricsplus", "fixture"));
        Assert.All(fixture.Plans, purpose => Assert.Equal(ProviderRoutePurpose.Automatic, purpose));
    }

    [Fact]
    public async Task BackgroundRefreshKeepsAutomaticRoutingWhenTheResolverReadsAnExactSource()
    {
        await using var fixture = new Fixture(new Source("lyricsplus", Result("lyricsplus", true)));
        var context = fixture.Protocol with { RefreshLyrics = true, IsReadOnly = true };
        var found = await fixture.Resolver.FindAllAsync(context,
            new Song { Title = "Fixture", Artist = "Artist", Duration = 120 }, "recording",
            new ProtocolLyricsSources { OnlySourceId = "lyricsplus", ReadOnly = true });
        Assert.NotNull(Assert.Single(found).Lyrics);
        Assert.All(fixture.Plans, purpose => Assert.Equal(ProviderRoutePurpose.Automatic, purpose));
    }

    [Fact]
    public async Task CacheOnlyRefreshRankingNeverStartsAColdProviderRead()
    {
        var source = new Source("lyricsplus", Result("lyricsplus", true));
        await using var fixture = new Fixture(source);
        var found = await fixture.Resolver.FindAllAsync(fixture.Protocol,
            new Song { Title = "Fixture", Artist = "Artist", Artists = ["Artist"], Duration = 120 }, "recording",
            new ProtocolLyricsSources { CacheOnly = true, ReadOnly = true });
        Assert.DoesNotContain(found, candidate => candidate.Lyrics != null);
        Assert.Equal(0, source.Calls);
    }
}
