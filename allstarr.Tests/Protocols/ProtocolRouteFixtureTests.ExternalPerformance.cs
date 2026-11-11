using System.Net;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using Microsoft.Extensions.DependencyInjection.Extensions;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ExternalSimilar_HangingProviderStopsAtTheRequestBudget()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hanging = new TaskCompletionSource<List<Artist>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "hanging", 50))
            .Returns(() => { entered.TrySetResult(); return hanging.Task; });
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.AddSingleton<TimeProvider>(new RecommendationClock(clock));
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var pending = client.GetAsync("/Items/ext-deezer-artist-hanging/Similar");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(3));
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        hanging.TrySetResult([]);
        await client.GetStringAsync("/Items/ext-deezer-artist-hanging/Similar");
        gateway.Verify(value => value.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "hanging", 50), Times.Exactly(2));
    }

    [Fact]
    public async Task ExternalSimilar_RepeatedCompletedReadKeepsTheSameBytesWithoutProviderCalls()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync(new Song { Id = "ext-deezer-song-seed", ExternalId = "seed", Artist = "Artist" });
        gateway.Setup(value => value.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 50))
            .ReturnsAsync([new Song { Id = "ext-deezer-song-ready", ExternalId = "ready", ExternalProvider = "deezer", Title = "Ready" }]);
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services => services.AddSingleton(gateway.Object));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var initial = await client.GetAsync("/Items/ext-deezer-song-seed/Similar");
        var first = await initial.Content.ReadAsStringAsync();
        Assert.True(initial.IsSuccessStatusCode, first);
        Assert.Equal(first, await client.GetStringAsync("/Items/ext-deezer-song-seed/Similar"));
        gateway.Verify(value => value.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"), Times.Once);
    }
    [Fact]
    public async Task ExternalSimilar_InstantMixKeepsReadyAlbumTracksWhenArtistSearchHangs()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hanging = new TaskCompletionSource<allstarr.Models.Search.SearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync(new Album { Artist = "Artist", Songs = [new Song { Id = "ext-deezer-song-ready", ExternalId = "ready", ExternalProvider = "deezer", Title = "Ready" }] });
        gateway.Setup(value => value.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist", 2, 0, 0, "deezer"))
            .Returns(() => { entered.TrySetResult(); return hanging.Task; });
        var interaction = new Mock<IJellyfinInteractionProtocolAdapter>();
        var shaper = new JellyfinInteractionProtocolAdapter();
        interaction.Setup(value => value.CanRunOptionalUserWork(It.IsAny<ProtocolExecutionContext?>())).Returns(true);
        interaction.Setup(value => value.ShapeInstantMix(It.IsAny<IReadOnlyList<Dictionary<string, object?>>>()))
            .Returns((IReadOnlyList<Dictionary<string, object?>> items) => shaper.ShapeInstantMix(items));
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.AddSingleton<TimeProvider>(new RecommendationClock(clock));
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IJellyfinInteractionProtocolAdapter>();
            services.AddSingleton(interaction.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var pending = client.GetAsync("/Albums/ext-deezer-album-seed/InstantMix?Limit=2");
        var started = await Task.WhenAny(entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(10));
        if (started == pending)
        {
            using var early = await pending;
            Assert.Fail($"Unexpected early response {(int)early.StatusCode}: {await early.Content.ReadAsStringAsync()}");
        }
        clock.Advance(TimeSpan.FromMilliseconds(2700));
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ext-deezer-song-ready", Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetString());
        hanging.TrySetResult(new());
    }

    [Fact]
    public async Task ExternalSimilar_InstantMixForSong_KeepsReadySeedTrackWhenSimilarLookupHangs()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hanging = new TaskCompletionSource<List<Song>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync(new Song { Id = "ext-deezer-song-seed", ExternalId = "seed", ExternalProvider = "deezer", Title = "Seed", Artist = "Artist" });
        gateway.Setup(value => value.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 2))
            .Returns(() => { entered.TrySetResult(); return hanging.Task; });
        gateway.Setup(value => value.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
            .ReturnsAsync(true);
        var interaction = new Mock<IJellyfinInteractionProtocolAdapter>();
        var shaper = new JellyfinInteractionProtocolAdapter();
        interaction.Setup(value => value.CanRunOptionalUserWork(It.IsAny<ProtocolExecutionContext?>())).Returns(true);
        interaction.Setup(value => value.ShapeInstantMix(It.IsAny<IReadOnlyList<Dictionary<string, object?>>>()))
            .Returns((IReadOnlyList<Dictionary<string, object?>> items) => shaper.ShapeInstantMix(items));
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.AddSingleton<TimeProvider>(new RecommendationClock(clock));
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IJellyfinInteractionProtocolAdapter>();
            services.AddSingleton(interaction.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var pending = client.GetAsync("/Items/ext-deezer-song-seed/InstantMix?Limit=2");
        var started = await Task.WhenAny(entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(10));
        if (started == pending)
        {
            using var early = await pending;
            Assert.Fail($"Unexpected early response {(int)early.StatusCode}: {await early.Content.ReadAsStringAsync()}");
        }
        clock.Advance(TimeSpan.FromMilliseconds(2700));
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ext-deezer-song-seed", Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetString());
        hanging.TrySetResult([]);
    }

    [Fact]
    public async Task ExternalSimilar_InstantMixForSong_TotalFailureReturnsEmptyMixWithoutThrowing()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ThrowsAsync(new HttpRequestException("network down"));
        var interaction = new Mock<IJellyfinInteractionProtocolAdapter>();
        var shaper = new JellyfinInteractionProtocolAdapter();
        interaction.Setup(value => value.CanRunOptionalUserWork(It.IsAny<ProtocolExecutionContext?>())).Returns(true);
        interaction.Setup(value => value.ShapeInstantMix(It.IsAny<IReadOnlyList<Dictionary<string, object?>>>()))
            .Returns((IReadOnlyList<Dictionary<string, object?>> items) => shaper.ShapeInstantMix(items));
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IJellyfinInteractionProtocolAdapter>();
            services.AddSingleton(interaction.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/Items/ext-deezer-song-seed/InstantMix?Limit=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
    }

    private sealed class RecommendationClock(ManualTimeProvider clock) : TimeProvider
    {
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override long GetTimestamp() => clock.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            period == Timeout.InfiniteTimeSpan ? clock.CreateTimer(callback, state, dueTime, period) :
                TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }

}
