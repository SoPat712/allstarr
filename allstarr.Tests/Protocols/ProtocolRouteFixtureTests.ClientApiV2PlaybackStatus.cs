using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using allstarr.Core.Capabilities;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApiV2_PlaybackStatusRemainsLiveWhileAnotherResponseIsOpen()
    {
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new V2PlaybackCompletions();
        var opens = 0;
        using var factory = V2PlaybackFactory(audio: request => request?.Method == HttpMethod.Head ? V2AudioResponse(request) :
            new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new V2HeldStream(++opens == 1 ? firstRelease.Task : secondRelease.Task))
                { Headers = { ContentType = new("audio/flac") } }
            }, configure: services => services.AddSingleton<IStartupFilter>(completions));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var playback = await ResolveV2Playback(client, "native1", "original");
        var path = "/allstarr/v2/playback/" + playback["playbackId"]!.GetValue<string>();
        using var first = await client.SendAsync(new(HttpMethod.Get, playback["url"]!.GetValue<string>()), HttpCompletionOption.ResponseHeadersRead);
        using var second = await client.SendAsync(new(HttpMethod.Get, playback["url"]!.GetValue<string>()), HttpCompletionOption.ResponseHeadersRead);
        try
        {
            first.EnsureSuccessStatusCode();
            second.EnsureSuccessStatusCode();
            secondRelease.TrySetResult();
            await second.Content.ReadAsByteArrayAsync();
            await completions.WaitAsync();
            Assert.NotNull((await ReadClientLyricsJsonAsync(client, path))["serving"]);
        }
        finally { firstRelease.TrySetResult(); secondRelease.TrySetResult(); }
        await first.Content.ReadAsByteArrayAsync();
        await completions.WaitAsync();
        Assert.Null((await ReadClientLyricsJsonAsync(client, path))["serving"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApiV2_PlaybackStatusReadsOnlyTheResolvedPlaybackAndItsLiveServingFacts(bool external)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioCalls = 0;
        var preparation = new Mock<IUpcomingTrackPreparation>(MockBehavior.Strict);
        var completions = new V2PlaybackCompletions();
        HttpResponseMessage HeldAudio() => new(HttpStatusCode.OK)
        { Content = new StreamContent(new V2HeldStream(release.Task)) { Headers = { ContentType = new("audio/flac") } } };
        using var factory = V2PlaybackFactory(audio: request =>
        {
            audioCalls++;
            return request?.Method == HttpMethod.Head ? V2AudioResponse(request) : HeldAudio();
        }, source: () =>
        {
            audioCalls++;
            return new(HeldAudio(), new("fixture", new("https://fixture.invalid/audio"), DateTimeOffset.UtcNow.AddMinutes(1), true, true,
                new("audio/flac", "flac", "flac", 1000000, 96000, 24, 2), ProviderStreamRetryBehavior.DoNotRetry), "tidal", "served", IsCached: true);
        }, configure: services =>
        {
            services.RemoveAll<IUpcomingTrackPreparation>();
            services.AddSingleton(preparation.Object);
            services.AddSingleton<IStartupFilter>(completions);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        if (external) await SeedV2DetailsAsync(factory, "listener", "known", personal: true);
        var playback = await ResolveV2Playback(client, external ? "ext-deezer-song-known" : "native1", "original");
        var other = await ResolveV2Playback(client, external ? "ext-deezer-song-known" : "native1", "high");
        var path = "/allstarr/v2/playback/" + playback["playbackId"]!.GetValue<string>();
        var callsAfterResolve = audioCalls;
        var before = await ReadClientLyricsJsonAsync(client, path);
        Assert.Equal(playback["playbackId"]!.GetValue<string>(), before["playbackId"]!.GetValue<string>());
        Assert.Null(before["delivered"]);
        Assert.Null(before["serving"]);
        await AssertV2ResourceRead(client, path, before);
        Assert.Equal(callsAfterResolve, audioCalls);
        using var head = await client.SendAsync(new(HttpMethod.Head, playback["url"]!.GetValue<string>()));
        head.EnsureSuccessStatusCode();
        Assert.Null((await ReadClientLyricsJsonAsync(client, path))["serving"]);
        using var streaming = await client.SendAsync(new(HttpMethod.Get, playback["url"]!.GetValue<string>()), HttpCompletionOption.ResponseHeadersRead);
        streaming.EnsureSuccessStatusCode();
        try
        {
            var callsAfterOpen = audioCalls;
            var live = await ReadClientLyricsJsonAsync(client, path);
            Assert.Equal(external ? "tidal" : "jellyfin", live["serving"]!["source"]!.GetValue<string>());
            Assert.Equal("flac", live["serving"]!["codec"]!.GetValue<string>());
            Assert.Equal(external ? 1000000 : 2000000, live["serving"]!["bitrate"]!.GetValue<int>());
            Assert.Equal(96000, live["serving"]!["sampleRate"]!.GetValue<int>());
            Assert.Equal(24, live["serving"]!["bitDepth"]!.GetValue<int>());
            Assert.Equal("flac", live["delivered"]!["codec"]!.GetValue<string>());
            Assert.Equal(external ? 1000000 : 2000000, live["delivered"]!["bitrate"]!.GetValue<int>());
            await AssertV2ResourceRead(client, path, live);
            Assert.Null((await ReadClientLyricsJsonAsync(client, "/allstarr/v2/playback/" + other["playbackId"]!.GetValue<string>()))["serving"]);
            Assert.Equal(callsAfterOpen, audioCalls);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
            using var foreign = await client.GetAsync(path);
            await AssertClientApiError(foreign, 404, "not_found");
            Assert.Equal(404, (await ReadV2Batch(client, path))["status"]!.GetValue<int>());
            client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        }
        finally { release.TrySetResult(); }
        await streaming.Content.ReadAsByteArrayAsync();
        await completions.WaitAsync();
        var closed = await ReadClientLyricsJsonAsync(client, path);
        Assert.Null(closed["serving"]);
        Assert.Null(closed["delivered"]);
        preparation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("bad", 400)]
    [InlineData("00000000000000000000000000000001", 404)]
    public async Task ClientApiV2_PlaybackStatusValidatesIdsWithoutOpeningAudio(string id, int expected)
    {
        using var factory = V2PlaybackFactory(audio: _ => throw new InvalidOperationException("Unexpected audio request"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var path = "/allstarr/v2/playback/" + id;
        using var response = await client.GetAsync(path);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(expected, (await ReadV2Batch(client, path))["status"]!.GetValue<int>());
    }

    private sealed class V2PlaybackCompletions : IStartupFilter
    {
        private readonly Channel<bool> completed = Channel.CreateUnbounded<bool>();

        internal Task WaitAsync() => completed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (http, run) =>
            {
                if (http.Request.Method == "GET" && http.Request.Path.StartsWithSegments("/allstarr/v2/stream"))
                    http.Response.OnCompleted(() => { completed.Writer.TryWrite(true); return Task.CompletedTask; });
                await run();
            });
            next(app);
        };
    }
}
