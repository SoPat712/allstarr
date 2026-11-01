using System.Net;
using allstarr.Core.Intelligence;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class HistoryRequestBudgetTests
{
    [Fact]
    public async Task FiveThousandSongsNeverExceedSixtyActualRequestsPerMinute()
    {
        var clock = new ManualTimeProvider();
        var rate = new HistoryRequestRate(clock);
        var outbound = new Outbound(clock);
        using var client = new HttpClient(new HistoryRequestBudgetHandler { InnerHandler = outbound });
        using var budget = new HistoryRequestBudget(rate);
        for (var song = 0; song < 5000; song++)
        {
            for (var call = 0; call < 3; call++)
            {
                var pending = client.GetAsync("https://catalog.example.test/track");
                if (!pending.IsCompleted)
                {
                    Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimerAsync());
                    clock.Advance(TimeSpan.FromSeconds(1));
                }
                using var response = await pending;
            }
        }
        Assert.Equal(15000, budget.Calls);
        Assert.All(outbound.At.Select((at, index) => (at, index)).Where(item => item.index >= 60), item =>
            Assert.True(item.at - outbound.At[item.index - 60] >= TimeSpan.FromMinutes(1)));
        Console.WriteLine("Search fixture: 5000 songs, 15000 actual fake HTTP requests, 3.0 calls/song; virtual elapsed 14999 seconds");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfterIsHonoredAndInteractiveRequestDoesNotWaitForHistory(bool absolute)
    {
        var clock = new ManualTimeProvider();
        var rate = new HistoryRequestRate(clock);
        var outbound = new Outbound(clock, true, absolute);
        using var client = new HttpClient(new HistoryRequestBudgetHandler { InnerHandler = outbound });
        using (var budget = new HistoryRequestBudget(rate))
        {
            using var first = await client.GetAsync("https://catalog.example.test/track");
            await rate.BackoffAsync("catalog.example.test", TimeSpan.FromMinutes(1), default);
            var pending = client.GetAsync("https://catalog.example.test/track");
            Assert.Equal(TimeSpan.FromMinutes(2), await clock.NextTimerAsync());
            clock.Advance(TimeSpan.FromSeconds(119));
            Assert.False(pending.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            using var second = await pending;
            Assert.Equal(2, budget.Calls);
        }
        using var interactive = await client.GetAsync("https://catalog.example.test/interactive");
        Assert.Equal(outbound.At[1], outbound.At[2]);
    }

    [Fact]
    public async Task EndingHistoryRunCancelsQueuedRequests()
    {
        var clock = new ManualTimeProvider();
        var outbound = new Outbound(clock);
        using var client = new HttpClient(new HistoryRequestBudgetHandler { InnerHandler = outbound });
        var budget = new HistoryRequestBudget(new(clock));
        using var first = await client.GetAsync("https://catalog.example.test/track");
        var pending = client.GetAsync("https://catalog.example.test/track");
        await clock.NextTimerAsync();
        budget.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(outbound.At);
    }

    [Fact]
    public async Task InteractiveHookPreemptsHistoryWaitingInsideTheInterpreter()
    {
        var clock = new ManualTimeProvider();
        var outbound = new Outbound(clock);
        using var transport = new HistoryRequestBudgetHandler { InnerHandler = outbound };
        var root = Path.Combine(Path.GetTempPath(), "history-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string manifest = """
            {"id":"deezer","name":"Fixture","displayName":"Fixture","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js",
             "capabilities":[{"kind":"Metadata","hooks":["searchTracks"],"accountScopes":[],"accountRequired":false}],"permissions":[]}
            """;
        var sandbox = new ExtensionSandbox(root, manifest, """
            registerExtension({searchTracks: function() {
              fetch('https://catalog.example.test/one');
              fetch('https://catalog.example.test/two');
              return {items:[]};
            }});
            """, new BudgetClientFactory(transport), NullLogger.Instance,
            new ExtensionRuntimePermissionSet(new HashSet<string>(["https://catalog.example.test/"]),
                new HashSet<string>(), new HashSet<string>()), runtimeStateDirectory: root);
        var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, ExtensionSdkV1.ParseManifest(manifest));
        var owner = Guid.NewGuid();
        var context = new ProviderExecutionContext(new(ProviderActorKind.User, owner, new("jellyfin", "fixture", "listener")), "deezer", null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, false, true, ["deezer"]), "history-search", "history-search", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var history = Task.Run(async () =>
        {
            using var budget = new HistoryRequestBudget(new(clock));
            return await adapter.SearchTracksAsync(context, new("Song", new()));
        });
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimerAsync());
            Assert.True(await Task.Run(() => sandbox.HasCallableHook("searchTracks")).WaitAsync(TimeSpan.FromSeconds(5)));
            var result = await history.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.IsSuccess);
            Assert.Equal(ProviderErrorKind.Canceled, result.Error!.Kind);
            Assert.Single(outbound.At);
        }
        finally
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await history.WaitAsync(TimeSpan.FromSeconds(5));
            sandbox.Cleanup();
            Directory.Delete(root, true);
        }
    }

    private sealed class BudgetClientFactory(HttpMessageHandler transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(transport, disposeHandler: false);
    }

    private sealed class Outbound(TimeProvider clock, bool rateLimited = false, bool absolute = false) : HttpMessageHandler
    {
        public List<DateTimeOffset> At { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            At.Add(clock.GetUtcNow());
            var response = new HttpResponseMessage(rateLimited && At.Count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = absolute ? new(clock.GetUtcNow().AddMinutes(2)) : new(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        }
    }
}
