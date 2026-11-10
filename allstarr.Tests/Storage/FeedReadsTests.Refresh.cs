using System.Collections.Immutable;
using System.Diagnostics;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed partial class FeedReadsTests
{
    private readonly ITestOutputHelper _output;
    public FeedReadsTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task KeysUseStablePolicyFieldsAndNormalizedLibraryScope()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var context = Context(User("listener")) with
        {
            Policy = new(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, "lossless", .07)
            { PreferenceRevision = "first", Preferences = new(LyricsOrder: "one,two") }
        };
        var repeated = context with { Policy = context.Policy! with { PreferenceRevision = "second", Preferences = new(LyricsOrder: "two,one") } };
        Assert.Equal(db.FeedReadKey(context, "mixes", new(true, ["b", "a"])),
            db.FeedReadKey(repeated, "mixes", new(true, ["a", "b", "a"])));
        Assert.NotEqual(db.FeedReadKey(context, "mixes", new(true, ["a"])), db.FeedReadKey(context, "mixes", new(true, ["b"])));
        Assert.NotEqual(db.FeedReadKey(context, "mixes"), db.FeedReadKey(context with
        { Policy = context.Policy! with { Preferences = new(ExplicitFilter: "CleanOnly") } }, "mixes"));
        var labels = context with { Policy = context.Policy! with { Preferences = new(ShowExplicitLabel: false) } };
        Assert.Equal(db.FeedReadKey(context, "mixes"), db.FeedReadKey(labels, "mixes"));
        Assert.NotEqual(db.FeedReadKey(context, "playlist-tracks:one"), db.FeedReadKey(labels, "playlist-tracks:one"));
        Assert.NotEqual(db.FeedReadKey(context, "mixes"), db.FeedReadKey(Context(User("other")), "mixes"));
    }

    [Fact]
    public async Task LibraryChurnRefreshesOnceAndStopsServingStaleAtSixtySeconds()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new FeedFactory(database.Options);
        await using var db = factory.CreateDbContext();
        var owner = User("listener");
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var context = Context(owner);
        var clock = new FeedClock();
        using var cache = new MemoryApplicationCache(clock);
        var coalescer = new ApplicationCacheRequestCoalescer(new());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<FeedValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        async Task<FeedValue?> Read(Microsoft.AspNetCore.Http.HttpContext? _, ProtocolExecutionContext execution)
        {
            if (Interlocked.Increment(ref reads) == 1) return new("before");
            entered.TrySetResult();
            return await release.Task.WaitAsync(execution.CancellationToken);
        }
        Task<FeedValue?> Poll() => FeedReadCache.ReadAsync(factory, cache, coalescer, clock, context, "mixes", null, null, Read);
        Assert.Equal("before", (await Poll())!.Value);
        var key = db.FeedReadKey(context, "mixes");
        var revision = db.FeedReadLibraryRevisionFor(owner.Id);
        db.ExternalMetadataSnapshots.Add(Snapshot(owner.Id, "track"));
        await db.SaveChangesAsync();
        Assert.Equal(revision + 1, db.FeedReadLibraryRevisionFor(owner.Id));
        Assert.Equal(key, db.FeedReadKey(context, "mixes"));
        Assert.Equal("before", (await Poll())!.Value);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Poll())), value => Assert.Equal("before", value!.Value));
        clock.UtcNow += TimeSpan.FromSeconds(59);
        Assert.Equal("before", (await Poll())!.Value);
        Assert.Equal(2, reads);
        clock.UtcNow += TimeSpan.FromSeconds(1);
        var expired = Poll();
        Assert.False(expired.IsCompleted);
        Assert.Equal(2, reads);
        release.SetResult(new("after"));
        Assert.Equal("after", (await expired)!.Value);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task OwnChangeBypassesHeldRefreshAndCannotBeOverwrittenByIt()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new FeedFactory(database.Options);
        await using var db = factory.CreateDbContext();
        var owner = User("listener");
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var context = Context(owner);
        var clock = new FeedClock();
        using var cache = new MemoryApplicationCache(clock);
        var coalescer = new ApplicationCacheRequestCoalescer(new());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<FeedValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        async Task<FeedValue?> Read(Microsoft.AspNetCore.Http.HttpContext? _, ProtocolExecutionContext execution)
        {
            var read = Interlocked.Increment(ref reads);
            if (read != 2) return new(read == 1 ? "before" : "own change");
            entered.SetResult();
            return await release.Task.WaitAsync(execution.CancellationToken);
        }
        Task<FeedValue?> Poll() => FeedReadCache.ReadAsync(factory, cache, coalescer, clock, context, "mixes", null, null, Read);
        await Poll();
        clock.UtcNow += TimeSpan.FromSeconds(30);
        Assert.Equal("before", (await Poll())!.Value);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldKey = db.FeedReadKey(context, "mixes");
        db.ExternalMetadataSnapshots.Add(Snapshot(owner.Id, "personal-mixes"));
        await db.SaveChangesAsync();
        Assert.Equal("own change", (await Poll())!.Value);
        release.SetResult(new("outdated refresh"));
        await coalescer.RunAsync(oldKey, () => Task.FromResult<FeedValue?>(null), default);
        Assert.Equal("own change", (await Poll())!.Value);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task PollsEveryThreeSecondsStayCachedWhileMatchingWritesEveryTwoSeconds()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new FeedFactory(database.Options);
        await using var db = factory.CreateDbContext();
        var owner = User("listener");
        var source = Snapshot(owner.Id, "track");
        var match = new TrackMatchRecord { Id = Guid.NewGuid(), OwnerUserId = owner.Id, ExternalSnapshotId = source.Id, DecisionVersion = 1 };
        db.Users.Add(owner);
        db.ExternalMetadataSnapshots.Add(source);
        db.TrackMatches.Add(match);
        await db.SaveChangesAsync();
        var context = Context(owner);
        var clock = new FeedClock();
        using var cache = new MemoryApplicationCache(clock);
        var coalescer = new ApplicationCacheRequestCoalescer(new());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var reads = new int[2];
        var scopes = new[] { "mixes:UTC", "releases" };
        var delays = new[] { 150, 800 };
        Task<FeedValue?> Poll(int index) => FeedReadCache.ReadAsync(factory, cache, coalescer, clock, context,
            scopes[index], null, null, async (_, execution) =>
            {
                if (Interlocked.Increment(ref reads[index]) == 1) await Task.Delay(delays[index], execution.CancellationToken);
                else
                {
                    entered[index].TrySetResult();
                    await release.Task.WaitAsync(execution.CancellationToken);
                }
                return new FeedValue("unchanged");
            });
        var cold = new double[2];
        for (var index = 0; index < 2; index++)
        {
            var timer = Stopwatch.StartNew();
            await Poll(index);
            cold[index] = timer.Elapsed.TotalMilliseconds;
        }
        var revision = db.FeedReadLibraryRevisionFor(owner.Id);
        var keys = scopes.Select(scope => db.FeedReadKey(context, scope)).ToArray();
        var epoch = clock.UtcNow;
        var nextWrite = 0;
        var samples = new[] { new List<double>(), new List<double>() };
        try
        {
            for (var poll = 0; poll < 20; poll++)
            {
                var second = poll * 3;
                while (nextWrite <= second)
                {
                    clock.UtcNow = epoch.AddSeconds(nextWrite);
                    match.Revision++;
                    await db.SaveChangesAsync();
                    nextWrite += 2;
                }
                clock.UtcNow = epoch.AddSeconds(second);
                for (var index = 0; index < 2; index++)
                {
                    var timer = Stopwatch.StartNew();
                    Assert.Equal("unchanged", (await Poll(index).WaitAsync(TimeSpan.FromSeconds(5)))!.Value);
                    samples[index].Add(timer.Elapsed.TotalMilliseconds);
                }
                if (poll == 0) await Task.WhenAll(entered.Select(item => item.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(29, db.FeedReadLibraryRevisionFor(owner.Id) - revision);
            Assert.Equal(keys, scopes.Select(scope => db.FeedReadKey(context, scope)));
            Assert.Equal(new[] { 2, 2 }, reads);
            for (var index = 0; index < 2; index++)
                _output.WriteLine($"{scopes[index]}: cold={cold[index]:F1} ms; 20/20 polls returned cached content while refresh held; warm ms={string.Join(",", samples[index].Select(value => value.ToString("F1")))}; foreground backend reads=0, background reads=1; match writes=29.");
        }
        finally
        {
            release.TrySetResult();
            foreach (var key in keys) await coalescer.RunAsync(key, () => Task.FromResult<FeedValue?>(null), default);
        }
    }

    private sealed record FeedValue(string Value);

    [Fact]
    public async Task FailedRefreshDoesNotExtendTheStaleWindow()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new FeedFactory(database.Options);
        var context = Context(User("listener"));
        var clock = new FeedClock();
        using var cache = new MemoryApplicationCache(clock);
        var coalescer = new ApplicationCacheRequestCoalescer(new());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Task<FeedValue?> Read(Microsoft.AspNetCore.Http.HttpContext? _, ProtocolExecutionContext execution)
        {
            var count = Interlocked.Increment(ref reads);
            if (count == 2)
            {
                entered.SetResult();
                throw new HttpRequestException("Unavailable");
            }
            return Task.FromResult<FeedValue?>(new(count == 1 ? "before" : "after"));
        }
        Task<FeedValue?> Poll() => FeedReadCache.ReadAsync(factory, cache, coalescer, clock, context, "releases", null, null, Read);
        Assert.Equal("before", (await Poll())!.Value);
        clock.UtcNow += TimeSpan.FromSeconds(30);
        Assert.Equal("before", (await Poll())!.Value);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var db = factory.CreateDbContext();
        try { await coalescer.RunAsync(db.FeedReadKey(context, "releases"), () => Task.FromResult<FeedValue?>(null), default); }
        catch (HttpRequestException) { }
        clock.UtcNow += TimeSpan.FromSeconds(30);
        Assert.Equal("after", (await Poll())!.Value);
        Assert.Equal(3, reads);
    }

    private sealed class FeedClock : IPlatformClock
    {
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
        public DateTimeOffset UtcNow
        {
            get => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _ticks, value.UtcTicks);
        }
    }
    private sealed class FeedFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }
}
