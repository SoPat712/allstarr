using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task FeishinAndTempo_SavedQueueAndNowPlayingRetainSparseMediaFacts(string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new Song
            {
                Id = QueueSongA,
                Title = "Saved song",
                Artist = "Saved artist",
                ExternalProvider = "deezer",
                ExternalId = "42"
            });
        using var factory = SubsonicPlaybackFactory(format, new List<PlaybackSignalRequest>(), [], configure: services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        AssertQueueOk(await SaveQueue(client, "savePlayQueue", format, true, [QueueSongA], "current", QueueSongA, "0", "Feishin"), format);
        AssertQueueOk(await client.GetStringAsync(SubsonicPlaybackUrl("scrobble", format, "Feishin") + $"&id={QueueSongA}&submission=false"), format);
        gateway.Invocations.Clear();

        var queue = await client.GetStringAsync(QueueUrl("getPlayQueue", format, "Tempo"));
        var playing = await client.GetStringAsync(SubsonicPlaybackUrl("getNowPlaying", format, "Feishin"));

        SubsonicSongMediaContractTests.AssertSparseMedia(Assert.Single(ReadSongCollection(queue, format, "playQueue", "entry")));
        SubsonicSongMediaContractTests.AssertSparseMedia(Assert.Single(ReadSongCollection(playing, format, "nowPlaying", "entry"), song => song["id"] == QueueSongA));
        Assert.Contains(SubsonicNativeNowPlayingEntry(format), playing, StringComparison.Ordinal);
        gateway.VerifyNoOtherCalls();
    }
}
