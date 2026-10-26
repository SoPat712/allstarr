using System.Collections.Immutable;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("File")]
    [InlineData("Download")]
    public async Task CompatibleOriginalUrl_RetainsAndReplaysTheOriginalWithoutPlaybackWrites(string endpoint)
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        var bytes = ((byte[])[73, 68, 51, 4, 0, 0, 0, 0, 0, 0]).Concat("fLaC"u8.ToArray()).Concat(fixture.Bytes).ToArray();
        var gateway = fixture.Gateway((request, _) =>
        {
            Assert.Null(request.Headers.Range);
            return Task.FromResult(fixture.Response(bytes));
        });
        using var factory = new ProtocolFactory("Jellyfin", _ => DownloadAuthentication("Jellyfin"), services =>
        {
            services.AddSingleton<IStartupFilter, SonicPrincipalStartupFilter>();
            var policies = new Mock<IEffectiveProviderPolicyResolver>();
            policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                    ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                    ImmutableHashSet<string>.Empty, "DataSaver", 0.07)
                { UserId = user });
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton<IProtocolProviderGateway>(gateway);
            services.RemoveAll<ManagedTrackCacheService>();
            services.AddSingleton(fixture.Cache);
        });
        using var client = factory.CreateClient();
        var url = $"/Items/ext-deezer-song-original-track/{endpoint}?api_key=fixture-key&MaxStreamingBitrate=64000&AudioCodec=mp3&StartTimeTicks=300000000";
        using (var initial = await client.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            Assert.Equal(bytes, await initial.Content.ReadAsByteArrayAsync());
        }
        Assert.Single(await fixture.Mappings.ListCacheMappingsAsync());
        using (var replay = await client.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(bytes, await replay.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(1, fixture.LeaseCalls);
        Assert.False(fixture.Activity.WasStreamOpened("ext-deezer-song-original-track"));
    }
}
