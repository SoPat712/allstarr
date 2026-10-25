using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging;
using Moq;

namespace allstarr.Tests;

public sealed class MetadataLyricsCapabilityAdapterTests
{
    [Fact]
    public async Task GeniusContractKeepsSectionsAndRemovesPageFootersWithoutClaimingTiming()
    {
        var handler = new Handler((_, _) => Response(JsonSerializer.Serialize(new { lyrics = "  [Chorus]\nSynthetic words\n\nLast line\n12Embed\n3 Contributors" })));
        var value = (await Adapter("genius", handler).FetchLyricsAsync(Context("genius"), Request("genius"))).RequireValue();
        Assert.Equal("https://fetch-genius.samidy.workers.dev/?title=Fixture&artist=Artist", Assert.Single(handler.Requests).AbsoluteUri);
        Assert.Equal("[Chorus]\nSynthetic words\n\nLast line", value.Content);
        Assert.Equal(ProviderLyricsFormat.PlainText, value.Format);
        Assert.Null(value.TimedLines);
    }

    [Theory]
    [InlineData("Fixture", "Artist", true)]
    [InlineData("Fixture (feat. Guest)", "Artist, Guest", true)]
    [InlineData("Fixture (Live)", "Artist", false)]
    [InlineData("Fixture", "Another artist", false)]
    public async Task GeniusRejectsConflictingReturnedIdentity(string title, string artist, bool accepted)
    {
        var handler = new Handler((_, _) => Response(JsonSerializer.Serialize(new { title, artist, lyrics = "Synthetic words" })));
        var result = await Adapter("genius", handler).FetchLyricsAsync(Context("genius"), Request("genius"));
        Assert.Equal(accepted ? ProviderLyricsAvailabilityState.Available : ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
    }

    [Theory]
    [InlineData("genius")]
    public async Task MissingMetadataNeverSendsAndWrongOwnerIsForbidden(string provider)
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException());
        var adapter = Adapter(provider, handler);
        var empty = new ProviderLyricsRequest(Guid.CreateVersion7(), new(provider, ProviderResourceKind.Track, "lookup"));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, (await adapter.FetchLyricsAsync(Context(provider), empty)).RequireValue().Availability);
        Assert.Equal(ProviderErrorKind.Forbidden, (await adapter.FetchLyricsAsync(Context("other"), Request(provider))).Error!.Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("genius", 503)]
    [InlineData("genius", 429)]
    public async Task BusyFailuresAreCachedForTenMinutesAndLogsExcludePayloads(string provider, int status)
    {
        var clock = new ManualTimeProvider();
        var logs = new List<string>();
        var logger = new Mock<ILogger>();
        logger.Setup(value => value.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation => logs.Add(invocation.Arguments[2].ToString()!)));
        var handler = new Handler((_, _) => new((HttpStatusCode)status) { Content = new StringContent("Sensitive lyric payload") });
        var adapter = Adapter(provider, handler, clock, logger: logger.Object);
        var first = await adapter.FetchLyricsAsync(Context(provider, clock: clock), Request(provider));
        var second = await adapter.FetchLyricsAsync(Context(provider, clock: clock), Request(provider));
        Assert.False(first.IsSuccess);
        Assert.Equal(first.Error!.Kind, second.Error!.Kind);
        Assert.Single(handler.Requests);
        Assert.NotEmpty(logs);
        Assert.All(logs, line => Assert.DoesNotContain("Sensitive lyric payload", line));
        clock.Advance(TimeSpan.FromMinutes(10));
        await adapter.FetchLyricsAsync(Context(provider, clock: clock), Request(provider));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("genius")]
    public async Task RequestTimeoutIsFourSecondsAndNegativelyCached(string provider)
    {
        var clock = new ManualTimeProvider();
        var handler = new WaitingHandler();
        var adapter = Adapter(provider, handler, clock);
        var pending = adapter.FetchLyricsAsync(Context(provider, clock: clock), Request(provider));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(4));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal("lyrics-upstream-busy-or-timed-out", (await pending).Error!.Code);
        Assert.Equal("lyrics-upstream-busy-or-timed-out", (await adapter.FetchLyricsAsync(Context(provider, clock: clock), Request(provider))).Error!.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("genius", "[]")]
    [InlineData("genius", "{\"lyrics\":42}")]
    public async Task UnexpectedShapesFailSafely(string provider, string payload)
    {
        var result = await Adapter(provider, new Handler((_, _) => Response(payload)))
            .FetchLyricsAsync(Context(provider), Request(provider));
        Assert.Equal("provider-contract-changed", result.Error!.Code);
    }

    [Theory]
    [InlineData("genius")]
    public async Task SuccessfulLookupLogsExcludeLyricsAndTransportDetails(string provider)
    {
        var logs = new List<string>();
        var logger = new Mock<ILogger>();
        logger.Setup(value => value.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation => logs.Add(invocation.Arguments[2].ToString()!)));
        var handler = new Handler((_, _) => Response("{\"lyrics\":\"Synthetic private words\"}"));
        await Adapter(provider, handler, logger: logger.Object).FetchLyricsAsync(Context(provider), Request(provider));
        Assert.Single(logs);
        Assert.Contains(provider, logs[0]);
        Assert.DoesNotContain("Synthetic", logs[0]);
        Assert.DoesNotContain("https:", logs[0]);
    }

    [Theory]
    [InlineData("genius", "{\"lyrics\":\"\"}")]
    public async Task CallerCancellationDoesNotPoisonTheFailureCache(string provider, string payload)
    {
        var handler = new CancelOnceHandler(payload);
        var adapter = Adapter(provider, handler);
        using var cancellation = new CancellationTokenSource();
        var pending = adapter.FetchLyricsAsync(Context(provider, cancellation.Token), Request(provider));
        cancellation.Cancel();
        Assert.Equal(ProviderErrorKind.Canceled, (await pending).Error!.Kind);
        Assert.True((await adapter.FetchLyricsAsync(Context(provider), Request(provider))).IsSuccess);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class CancelOnceHandler(string payload) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++Calls == 1) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response(payload);
        }
    }

    [Fact]
    public async Task GeniusRemovesEmbedWhenItIsAttachedToTheLastLine()
    {
        var handler = new Handler((_, _) => Response("{\"lyrics\":\"[Outro]\\nSynthetic final line12Embed\"}"));
        var result = await Adapter("genius", handler).FetchLyricsAsync(Context("genius"), Request("genius"));
        Assert.Equal("[Outro]\nSynthetic final line", result.RequireValue().Content);
    }

    private static IProviderLyricsCapability Adapter(string provider, HttpMessageHandler handler, TimeProvider? clock = null,
        IApplicationCache? cache = null, ILogger? logger = null) =>
        new GeniusCapabilityAdapter(new HttpClient(handler), cache, clock ?? TimeProvider.System, new(clock ?? TimeProvider.System), logger);
    private static HttpResponseMessage Response(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    private static ProviderLyricsRequest Request(string provider, string? isrc = null, bool availability = false) =>
        new(Guid.CreateVersion7(), new(provider, ProviderResourceKind.Track, "lookup"), availability,
            trackTitle: "Fixture", artistNames: ["Artist"], albumTitle: "Album", durationSeconds: 180, isrc: isrc);
    private static ProviderExecutionContext Context(string provider, CancellationToken token = default, TimeProvider? clock = null) => new(new(ProviderActorKind.PublicRead, null), provider, null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
            true, true, false, [provider]), "lyrics-test", "lyrics-test", (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(1), token);

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(response(request, Requests.Count));
        }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response("");
        }
    }
}
