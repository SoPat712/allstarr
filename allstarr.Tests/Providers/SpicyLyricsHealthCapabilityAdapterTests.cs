using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class SpicyLyricsHealthCapabilityAdapterTests
{
    [Fact]
    public async Task SpicyCheckUsesFreshAuthorizedContentWithoutContentCache()
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload("line")) { MatchRequestedId = true };
        using var http = new HttpClient(handler);
        var cache = new SpicyLyricsTestCache();
        var source = new SpicyLyricsCapabilityAdapter(http, new SpicyLyricsTestSecrets(), cache);
        var context = SpicyLyricsTestData.Context();
        await source.FetchLyricsAsync(context, SpicyLyricsTestData.Request());
        var reads = cache.Reads;
        var helper = new SpicyLyricsHealthCapabilityAdapter(source);
        var result = await helper.CheckLyricsAsync(context);
        Assert.Equal(ProviderProbeStatus.Healthy, result.RequireValue().Status);
        Assert.Equal(reads, cache.Reads);
        Assert.Equal(1, cache.Writes);
        Assert.Equal(2, handler.Calls);
        var registry = new ProviderRegistry([SpicyLyricsCapabilityAdapter.CreateRegistration(source)]);
        Assert.False(registry.GetRequired("spicylyrics").HealthProbe);
        Assert.Equal(ProviderCapabilityKind.Lyrics, Assert.Single(registry.GetRequired("spicylyrics").Capabilities).Capability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpicyNoSampleOrCatalogMissIsNeutral(bool missing)
    {
        using var handler = new SpicyLyricsTestHandler("unused") { Status = HttpStatusCode.NotFound };
        using var http = new HttpClient(handler);
        var helper = new SpicyLyricsHealthCapabilityAdapter(new(http, new SpicyLyricsTestSecrets()));
        var result = await helper.CheckLyricsAsync(SpicyLyricsTestData.Context(), missing ? [] : null);
        Assert.True(result.IsSuccess);
        Assert.Equal("sample-unavailable", result.RequireValue().SafeCode);
        Assert.Equal(ProviderProbeStatus.Unavailable, result.RequireValue().Status);
        Assert.Equal(missing ? 0 : ProviderDiagnosticSamples.Recordings("spicylyrics").Count, handler.Calls);
    }
}
