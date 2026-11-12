using allstarr.Core.Caching;
using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Search;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper searchOutput;
    public ProtocolProviderStreamingGatewayTests(Xunit.Abstractions.ITestOutputHelper output) => searchOutput = output;

    [Fact]
    public async Task ProviderSearchStartsAllSourcesBeforeAnySourceCompletes()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var first = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var second = DelayedSearchProvider("qobuz", clock, TimeSpan.Zero);
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var provider in new[] { first, second })
            provider.Setup(service => service.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .Returns(async () =>
                {
                    if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
                    await release.Task;
                    return ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", []));
                });
        var gateway = BudgetGateway(clock, new TestMemoryApplicationCache(), first.Object, second.Object);
        var pending = gateway.SearchAsync(Context(), "Track", 10, 0, 0);
        try { await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        await pending;
        VerifySearchCount(first, "songs", 1);
        VerifySearchCount(second, "songs", 1);
    }

    [Fact]
    public async Task ProviderSearchTimingReportsColdAndWarmProviderFloor()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var serviceTimes = new Dictionary<string, List<double>> { ["deezer"] = [], ["qobuz"] = [] };
        Mock<IProviderMetadataCapability> TimedProvider(string id, int delay)
        {
            var provider = DelayedSearchProvider(id, TimeProvider.System, TimeSpan.Zero);
            provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .Returns(async (ProviderExecutionContext execution, ProviderMetadataSearchRequest _) =>
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    await Task.Delay(delay, execution.CancellationToken);
                    var page = ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new(id,
                        [new(new(id, ProviderResourceKind.Track, "track"), "Track", [new("Artist")])]));
                    serviceTimes[id].Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return page;
                });
            return provider;
        }
        var first = TimedProvider("deezer", 800);
        var second = TimedProvider("qobuz", 600);
        var gateway = BudgetGateway(clock, new TestMemoryApplicationCache(), first.Object, second.Object);
        var context = Context();
        var cold = new List<double>();
        var warm = new List<double>();
        var slowest = new List<double>();
        var processing = new List<double>();
        SearchResult? previous = null;
        for (var index = 0; index < 3; index++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            previous = await gateway.SearchAsync(context, "Track " + index, 10, 0, 0);
            cold.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            slowest.Add(Math.Max(serviceTimes["deezer"][index], serviceTimes["qobuz"][index]));
            processing.Add(cold[index] - slowest[index]);
        }
        for (var index = 0; index < 7; index++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = await gateway.SearchAsync(context, "Track 2", 10, 0, 0);
            warm.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.Equal(JsonSerializer.Serialize(previous), JsonSerializer.Serialize(result));
        }
        VerifySearchCount(first, "songs", 3);
        VerifySearchCount(second, "songs", 3);
        static double Percentile(List<double> samples, double fraction) =>
            samples.Order().ElementAt((int)Math.Ceiling(samples.Count * fraction) - 1);
        searchOutput.WriteLine("Controlled provider delays: 800 ms and 600 ms; cold provider floor: 800 ms. " +
            $"Cold n={cold.Count}: p50={Percentile(cold, .5):F1} ms, p95={Percentile(cold, .95):F1} ms. " +
            $"Warm n={warm.Count}: p50={Percentile(warm, .5):F1} ms, p95={Percentile(warm, .95):F1} ms. " +
            $"Actual slowest provider p50={Percentile(slowest, .5):F1} ms, p95={Percentile(slowest, .95):F1} ms; " +
            $"processing p50={Percentile(processing, .5):F1} ms, p95={Percentile(processing, .95):F1} ms. " +
            "Provider requests: 3 per provider; warm additional requests: 0.");
    }

    [Fact]
    public async Task ProviderSearchCacheSharesConcurrentCallsAndPreservesResponseContent()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var entries = new ConcurrentDictionary<string, string>();
        var cache = new Mock<IApplicationCache>();
        cache.SetupGet(item => item.IsEnabled).Returns(true);
        cache.Setup(item => item.GetStringAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => entries.GetValueOrDefault(key));
        cache.Setup(item => item.SetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .ReturnsAsync((string key, string value, TimeSpan? _) => { entries[key] = value; return true; });
        var gateway = BudgetGateway(clock, cache.Object, provider.Object);
        var context = Context();
        var first = gateway.SearchAsync(context, " Track ", 10, 0, 0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = gateway.SearchAsync(context, "track", 10, 0, 0);
        release.SetResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
            [new(new("deezer", ProviderResourceKind.Track, "track"), "Track", [new("Artist")],
                duration: TimeSpan.FromMilliseconds(3210))])));
        var answers = await Task.WhenAll(first, second);
        var expected = JsonSerializer.Serialize(answers[0]);
        Assert.Equal(expected, JsonSerializer.Serialize(answers[1]));
        answers[0].Songs[0].Title = "Changed";
        Assert.Equal(expected, JsonSerializer.Serialize(await gateway.SearchAsync(context, "TRACK", 10, 0, 0)));
        VerifySearchCount(provider, "songs", 1);
        cache.Verify(item => item.SetStringAsync(It.Is<string>(key => key.StartsWith("search:", StringComparison.Ordinal)),
            It.IsAny<string>(), TimeSpan.FromMinutes(10)), Times.Once);
    }

    [Fact]
    public async Task ProviderSearchCacheSeparatesListenersAccountsAndRevisions()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var registry = MetadataRegistry(provider.Object);
        var accountId = Guid.NewGuid();
        long revision = 1;
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
            {
                var plan = MetadataPlan(request, registry, provider.Object);
                return plan with
                {
                    Candidates = plan.Candidates.Select(candidate => candidate with
                    {
                        Context = new(candidate.Context.Actor, "deezer",
                        new ProviderAccountContext(accountId, "deezer", ProviderAccountScope.Shared, revision),
                        candidate.Context.Policy, candidate.Context.OperationId, candidate.Context.CorrelationId,
                        candidate.Context.Deadline, candidate.Context.CancellationToken)
                    }).ToArray()
                };
            });
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry,
                registry.GetRequiredCapability<IProviderStreamingCapability>("deezer", ProviderCapabilityKind.Streaming)));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(),
            applicationCache: new TestMemoryApplicationCache(), timeProvider: clock);
        var context = Context();
        await gateway.SearchAsync(context, "Track", 10, 0, 0);
        await gateway.SearchAsync(context, "Track", 10, 0, 0);
        await gateway.SearchAsync(Context(), "Track", 10, 0, 0);
        revision++;
        await gateway.SearchAsync(context, "Track", 10, 0, 0);
        accountId = Guid.NewGuid();
        await gateway.SearchAsync(context, "Track", 10, 0, 0);
        VerifySearchCount(provider, "songs", 4);
    }

    [Fact]
    public async Task ProviderSearchCacheSeparatesLimitsAndRequestedKinds()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var gateway = BudgetGateway(clock, new TestMemoryApplicationCache(), provider.Object);
        var context = Context();
        await gateway.SearchAsync(context, "Track", 10, 0, 0);
        await gateway.SearchAsync(context, "Track", 10, 0, 0);
        await gateway.SearchAsync(context, "Track", 5, 0, 0);
        await gateway.SearchAsync(context, "Track", 10, 10, 0);
        VerifySearchCount(provider, "songs", 3);
        VerifySearchCount(provider, "albums", 1);
    }
}
