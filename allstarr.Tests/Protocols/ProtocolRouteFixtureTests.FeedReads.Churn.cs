using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.ClientApi;
using allstarr.Services.Common;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task FeedReads_IdenticalPipelineRequestsHaveStableKeys()
    {
        var observed = new List<ProtocolExecutionContext>();
        using var factory = ClientMixFactory(new(), configure: services => services.Configure<MvcOptions>(options =>
            options.Filters.Add(new ClientApiObservationFilter(context => observed.Add(context!)))));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMixAsync(factory);
        var first = await client.GetStringAsync("/allstarr/v1/mixes?timeZoneId=UTC");
        Assert.Equal(first, await client.GetStringAsync("/allstarr/v1/mixes?timeZoneId=UTC"));
        Assert.Equal(2, observed.Count);
        Assert.NotEqual(observed[0].CorrelationId, observed[1].CorrelationId);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var access = factory.Services.GetRequiredService<IBackendLibraryAccessResolver>();
        Assert.Equal(db.FeedReadKey(observed[0], "mixes:UTC", await access.ResolveAsync(observed[0])),
            db.FeedReadKey(observed[1], "mixes:UTC", await access.ResolveAsync(observed[1])));
        var oldProjectionStable = JsonSerializer.Serialize(new { observed[0].Actor, observed[0].Policy }) ==
            JsonSerializer.Serialize(new { observed[1].Actor, observed[1].Policy });
        _lyricsSearchOutput.WriteLine($"Two independently authenticated request contexts have equal keys despite different correlation identifiers; original whole actor/policy projection stable={oldProjectionStable}.");
    }

    [Fact]
    public async Task FeedReads_MatchingWritesKeepWarmMixPollsCached()
    {
        var reads = 0;
        var blockRefresh = false;
        ProtocolExecutionContext? observed = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var factory = ClientMixFactory(new(), configure: services => services.Configure<MvcOptions>(options =>
            options.Filters.Add(new ClientApiObservationFilter(context => observed = context))), observe: request =>
        {
            if (request.RequestUri!.AbsolutePath != "/Users/listener/Items") return;
            Interlocked.Increment(ref reads);
            Thread.Sleep(150);
            if (blockRefresh)
            {
                entered.TrySetResult();
                release.Wait();
            }
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var db = await contexts.CreateDbContextAsync();
        var source = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "personal-mixes");
        var document = JsonSerializer.Deserialize<PersonalMixDocument>(source.PayloadJson)!;
        for (var index = 0; index < 24; index++)
        {
            var id = Guid.NewGuid();
            document.Mixes["custom:" + id] = mix with
            {
                Id = id,
                Name = "Saved " + index,
                Songs = Enumerable.Range(0, 50).Select(position => mix.Songs[position % 2] with { MixId = id }).ToArray()
            };
        }
        source.Id = Guid.NewGuid();
        source.SnapshotVersion++;
        source.PayloadJson = JsonSerializer.Serialize(document, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        db.ExternalMetadataSnapshots.Add(source);
        await db.SaveChangesAsync();
        var match = new TrackMatchRecord { Id = Guid.NewGuid(), OwnerUserId = source.OwnerUserId, ExternalSnapshotId = source.Id, DecisionVersion = 1 };
        db.TrackMatches.Add(match);
        await db.SaveChangesAsync();
        var first = await client.GetStringAsync("/allstarr/v1/mixes?timeZoneId=UTC");
        var baseline = new List<double>();
        for (var poll = 0; poll < 6; poll++)
        {
            match.Revision++;
            await db.SaveChangesAsync();
            await factory.Services.GetRequiredService<IApplicationCache>().DeleteByPatternAsync("feed:read:v1:*");
            var timer = Stopwatch.StartNew();
            Assert.Equal(first, await client.GetStringAsync("/allstarr/v1/mixes?timeZoneId=UTC"));
            baseline.Add(timer.Elapsed.TotalMilliseconds);
        }
        Assert.Equal(7, reads);
        _lyricsSearchOutput.WriteLine($"Mix global-invalidation reproduction: cache hits=0/6, foreground native reads=6; ms={string.Join(", ", baseline.Select(time => time.ToString("F1")))}");
        blockRefresh = true;
        var before = db.FeedReadLibraryRevision;
        var times = new List<double>();
        try
        {
            for (var poll = 0; poll < 6; poll++)
            {
                match.Revision++;
                await db.SaveChangesAsync();
                var timer = Stopwatch.StartNew();
                Assert.Equal(first, await client.GetStringAsync("/allstarr/v1/mixes?timeZoneId=UTC").WaitAsync(TimeSpan.FromSeconds(5)));
                times.Add(timer.Elapsed.TotalMilliseconds);
            }
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(8, reads);
            Assert.Equal(6, db.FeedReadLibraryRevision - before);
            _lyricsSearchOutput.WriteLine($"Mix matching writes=6; library revision bumps={db.FeedReadLibraryRevision - before}; cache hits=6/6, foreground native reads=0, background native reads=1; ms={string.Join(", ", times.Select(time => time.ToString("F1")))}");
        }
        finally
        {
            release.Set();
            var key = db.FeedReadKey(observed!, "mixes:UTC", new(true, ["library"]));
            await factory.Services.GetRequiredService<ApplicationCacheRequestCoalescer>().RunAsync(key, () => Task.FromResult<ClientMixes?>(null), default);
        }
    }

    [Fact]
    public async Task FeedReads_MatchingWritesKeepWarmReleasePollsCached()
    {
        var reads = 0;
        var armed = false;
        var blockRefresh = false;
        ProtocolExecutionContext? observed = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = ClientReleaseFactory(ClientReleaseMetadata(), new(), configure: services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton<IBackendLibraryAccessResolver>(provider => new TestBackendLibraryAccess(
                provider.GetRequiredService<IDbContextFactory<AllstarrDbContext>>(), "library"));
            services.Configure<MvcOptions>(options => options.Filters.Add(new ClientApiObservationFilter(context => observed = context)));
            var original = (IProviderRouter)services.Last(item => item.ServiceType == typeof(IProviderRouter)).ImplementationInstance!;
            var router = new Mock<IProviderRouter>();
            router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
                .Returns(async (ProviderRouteRequest request) =>
                {
                    if (armed)
                    {
                        Interlocked.Increment(ref reads);
                        await Task.Delay(20);
                        if (blockRefresh)
                        {
                            entered.TrySetResult();
                            await release.Task.WaitAsync(request.CancellationToken);
                        }
                    }
                    return await original.PlanAsync<IProviderMetadataCapability>(request);
                });
            services.RemoveAll<IProviderRouter>();
            services.AddSingleton(router.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMixAsync(factory);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        await releases.FollowAsync(owner, new("deezer", "release-artist", "Release artist"));
        await releases.PollAsync(owner, Guid.NewGuid(), default);
        armed = true;
        var first = await client.GetStringAsync("/allstarr/v1/releases");
        var times = new List<double>();
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var source = await db.ExternalMetadataSnapshots.FirstAsync(item => item.ResourceKind == "personal-mixes");
        var match = new TrackMatchRecord { Id = Guid.NewGuid(), OwnerUserId = owner, ExternalSnapshotId = source.Id, DecisionVersion = 1 };
        db.TrackMatches.Add(match);
        var baseline = new List<double>();
        for (var poll = 0; poll < 6; poll++)
        {
            match.Revision++;
            await db.SaveChangesAsync();
            await factory.Services.GetRequiredService<IApplicationCache>().DeleteByPatternAsync("feed:read:v1:*");
            var timer = Stopwatch.StartNew();
            Assert.Equal(first, await client.GetStringAsync("/allstarr/v1/releases"));
            baseline.Add(timer.Elapsed.TotalMilliseconds);
        }
        Assert.Equal(7, reads);
        _lyricsSearchOutput.WriteLine($"Release global-invalidation reproduction: cache hits=0/6, foreground route reads=6; ms={string.Join(", ", baseline.Select(time => time.ToString("F1")))}");
        blockRefresh = true;
        try
        {
            for (var poll = 0; poll < 6; poll++)
            {
                match.Revision++;
                await db.SaveChangesAsync();
                var timer = Stopwatch.StartNew();
                Assert.Equal(first, await client.GetStringAsync("/allstarr/v1/releases").WaitAsync(TimeSpan.FromSeconds(5)));
                times.Add(timer.Elapsed.TotalMilliseconds);
            }
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(8, reads);
            _lyricsSearchOutput.WriteLine($"Release cache hits=6/6, foreground provider route reads=0, background route reads=1; ms={string.Join(", ", times.Select(time => time.ToString("F1")))}");
        }
        finally
        {
            release.TrySetResult();
            var key = db.FeedReadKey(observed!, "releases", new(true, ["library"]));
            await factory.Services.GetRequiredService<ApplicationCacheRequestCoalescer>().RunAsync(key, () => Task.FromResult<ClientReleases?>(null), default);
        }
    }

    [Fact]
    public async Task FeedReads_FollowAndUnfollowAreVisibleOnTheNextReleaseRequest()
    {
        using var factory = ClientReleaseFactory(ClientReleaseMetadata(), new(), configure: services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton<IBackendLibraryAccessResolver>(provider => new TestBackendLibraryAccess(
                provider.GetRequiredService<IDbContextFactory<AllstarrDbContext>>(), "library"));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        var empty = await client.GetStringAsync("/allstarr/v1/releases");
        await releases.FollowAsync(owner, new("deezer", "release-artist", "Release artist"));
        using (var followed = JsonDocument.Parse(await client.GetStringAsync("/allstarr/v1/releases")))
            Assert.Single(followed.RootElement.GetProperty("followedArtists").EnumerateArray());
        await releases.UnfollowAsync(owner, "deezer", "release-artist");
        Assert.Equal(empty, await client.GetStringAsync("/allstarr/v1/releases"));
    }
}
