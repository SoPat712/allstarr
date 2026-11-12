using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task ServiceFeeds_ColdCardsShareTheirFetchWithoutBlockingAnotherListener()
    {
        var (capability, feeds) = await ConcurrentFeedsAsync(2);
        await using (var db = factory.CreateDbContext())
            await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "service-feeds").ExecuteDeleteAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentDictionary<Guid, int>();
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderUserPlaylistsRequest _) =>
            {
                calls.AddOrUpdate(context.Account!.AccountId, 1, (_, count) => count + 1);
                if (calls.Count == 2) entered.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                var card = feeds.Single(feed => feed.Account == context.Account!.AccountId).Cards[0];
                return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify",
                    [new(new("spotify", ProviderResourceKind.Playlist, card.SourcePlaylistId!), card.Name, new("spotify"), "revision", trackCount: 2)]));
            });
        using var cancellation = new CancellationTokenSource();
        var reads = new[]
        {
            service.ListCardsAsync(feeds[0].Owner, token: cancellation.Token),
            service.ListCardsAsync(feeds[0].Owner, token: cancellation.Token),
            service.ListCardsAsync(feeds[1].Owner, token: cancellation.Token)
        };
        var all = Task.WhenAll(reads);
        try
        {
            await entered.Task.WaitAsync(CompletionDeadline);
            Assert.All(calls.Values, count => Assert.Equal(1, count));
            release.TrySetResult();
            var results = await all.WaitAsync(CompletionDeadline);
            Assert.Equal(feeds[0].Cards[0].Id, Assert.Single(results[0], item => item.Kind == "external").Id);
            Assert.Equal(feeds[0].Cards[0].Id, Assert.Single(results[1], item => item.Kind == "external").Id);
            Assert.Equal(feeds[1].Cards[0].Id, Assert.Single(results[2], item => item.Kind == "external").Id);
            Assert.All(calls.Values, count => Assert.Equal(1, count));
            capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()), Times.Never);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try { await all; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ServiceFeeds_IndependentListenersPrepareThirtyTwoFeedsConcurrently()
    {
        var (capability, feeds) = await ConcurrentFeedsAsync(32);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentDictionary<Guid, int>();
        var active = 0;
        var maximum = 0;
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderPlaylistTracksRequest request) =>
            {
                calls.AddOrUpdate(context.Account!.AccountId, 1, (_, count) => count + 1);
                var running = Interlocked.Increment(ref active);
                FeedMaximum(ref maximum, running);
                if (running == feeds.Length) entered.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(context.CancellationToken);
                    return ConcurrentFeedPage(request);
                }
                finally { Interlocked.Decrement(ref active); }
            });
        using var cancellation = new CancellationTokenSource();
        var timer = new Stopwatch();
        var preparations = feeds.Select(feed => service.PrepareServiceFeedsAsync(feed.Owner, new(feed.Account, 0), cancellation.Token)).ToArray();
        var all = Task.WhenAll(preparations);
        try
        {
            await entered.Task.WaitAsync(CompletionDeadline);
            timer.Start();
            release.TrySetResult();
            await all.WaitAsync(TimeSpan.FromSeconds(90));
            timer.Stop();
            Assert.Equal(32, maximum);
            Assert.Equal(32, calls.Count);
            Assert.All(calls.Values, count => Assert.Equal(1, count));
            foreach (var feed in feeds)
                Assert.Equal(["First song", "Second song"], (await service.GetAsync(feed.Owner, feed.Cards[0].Id)).Songs.Select(item => item.Song.Title));
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try { await all; } catch (OperationCanceledException) { }
            timer.Stop();
            output.WriteLine($"32 listener feeds after releasing provider gate: {timer.Elapsed.TotalMilliseconds:F1} ms, calls={calls.Values.Sum()}, maximum={maximum}");
        }
    }

    [Fact]
    public async Task ServiceFeeds_SameAccountLimitsProviderReadsToTwoAndPreparesEachFeedOnce()
    {
        var (capability, feeds) = await ConcurrentFeedsAsync(1, 4);
        var feed = feeds[0];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentDictionary<string, int>();
        var active = 0;
        var maximum = 0;
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderPlaylistTracksRequest request) =>
            {
                calls.AddOrUpdate(request.PlaylistId.Value, 1, (_, count) => count + 1);
                var running = Interlocked.Increment(ref active);
                FeedMaximum(ref maximum, running);
                if (running == 2) entered.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(context.CancellationToken);
                    return ConcurrentFeedPage(request);
                }
                finally { Interlocked.Decrement(ref active); }
            });
        using var cancellation = new CancellationTokenSource();
        var preparations = Enumerable.Range(0, 4).Select(offset =>
            service.PrepareServiceFeedsAsync(feed.Owner, new(feed.Account, offset), cancellation.Token)).ToArray();
        var all = Task.WhenAll(preparations);
        try
        {
            await entered.Task.WaitAsync(CompletionDeadline);
            Assert.Equal(2, calls.Values.Sum());
            var cards = await service.ListCardsAsync(feed.Owner).WaitAsync(CompletionDeadline);
            Assert.Equal(4, cards.Count(item => item.Kind == "external"));
            Assert.Equal(2, calls.Values.Sum());
            release.TrySetResult();
            await all.WaitAsync(CompletionDeadline);
            Assert.Equal(2, maximum);
            Assert.Equal(4, calls.Count);
            Assert.All(calls.Values, count => Assert.Equal(1, count));
            await using var db = factory.CreateDbContext();
            Assert.Equal(4, await db.ExternalMetadataSnapshots.CountAsync(item => item.ResourceKind == "service-feed-prepared"));
            output.WriteLine($"One account, four feeds: calls={calls.Values.Sum()}, maximum={maximum}");
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try { await all; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ServiceFeeds_StaleSongsAndCardsReturnWhileTheirRefreshIsHeld()
    {
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new(options), clock);
        var (capability, feeds) = await ConcurrentFeedsAsync(1, queue: queue);
        var feed = feeds[0];
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync((ProviderExecutionContext _, ProviderPlaylistTracksRequest request) => ConcurrentFeedPage(request));
        await service.PrepareServiceFeedsAsync(feed.Owner, new(feed.Account, 0), default);
        var before = await service.GetAsync(feed.Owner, feed.Cards[0].Id);
        clock.UtcNow += TimeSpan.FromHours(7);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capability.Invocations.Clear();
        gateway.Invocations.Clear();
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderPlaylistTracksRequest request) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                return ConcurrentFeedPage(request);
            });
        using var cancellation = new CancellationTokenSource();
        var preparation = service.PrepareServiceFeedsAsync(feed.Owner, new(feed.Account, 0), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(CompletionDeadline);
            var detail = await service.GetAsync(feed.Owner, feed.Cards[0].Id).WaitAsync(CompletionDeadline);
            Assert.Equal(before.Songs.Select(item => item.Song.Id), detail.Songs.Select(item => item.Song.Id));
            Assert.Equal(before.Songs.Select(item => item.Song.Title), detail.Songs.Select(item => item.Song.Title));
            var cards = await service.ListCardsAsync(feed.Owner).WaitAsync(CompletionDeadline);
            Assert.Equal(feed.Cards[0].Id, Assert.Single(cards, item => item.Kind == "external").Id);
            Assert.DoesNotContain(await service.ListCardsAsync(other).WaitAsync(CompletionDeadline), item => item.Kind == "external");
            Assert.False(preparation.IsCompleted);
            capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()), Times.Once);
            gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()), Times.Never);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try { await preparation; } catch (OperationCanceledException) { }
        }
    }

    private async Task<(Mock<IProviderPlaylistCapability> Capability, (Guid Owner, Guid Account, PersonalMix[] Cards)[] Feeds)> ConcurrentFeedsAsync(
        int listeners, int cardsPerAccount = 1, DurableJobQueue? queue = null)
    {
        var feeds = new List<(Guid Owner, Guid Account, PersonalMix[] Cards)>();
        await using var db = factory.CreateDbContext();
        for (var listener = 0; listener < listeners; listener++)
        {
            var user = listener == 0 ? await db.Users.SingleAsync(item => item.Id == owner) : new UserRecord
            {
                Id = Guid.NewGuid(),
                BackendType = protocol,
                BackendInstanceId = "main",
                BackendPrincipalId = Guid.NewGuid().ToString()
            };
            if (listener != 0) db.Users.Add(user);
            var account = new ProviderAccountRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user.Id,
                ProviderId = "spotify",
                Enabled = true,
                Revision = 1,
                CreatedAt = clock.UtcNow
            };
            db.ProviderAccounts.Add(account);
            var scope = FeedSnapshotHash($"{user.Id}:{user.BackendType}:{user.BackendInstanceId}:{user.BackendPrincipalId}");
            var cards = Enumerable.Range(0, cardsPerAccount).Select(index => new PersonalMix(
                new Guid(Convert.FromHexString(FeedSnapshotHash($"{scope}:service-feed:{account.Id}:37i9dQZ{index}"))[..16]), "external", "Service mix " + index,
                "From Spotify", 1, null, new(false, "Preparing songs from your service…"), false, [])
            {
                Source = "spotify",
                SourcePlaylistId = "37i9dQZ" + index,
                ProviderAccountId = account.Id,
                SongCount = 2
            }).ToArray();
            feeds.Add((user.Id, account.Id, cards));
            var json = JsonSerializer.Serialize(cards);
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user.Id,
                ProviderAccountId = account.Id,
                ProviderId = "spotify",
                ResourceKind = "service-feeds",
                ExternalIdHash = FeedSnapshotHash(scope + ":1"),
                SnapshotVersion = 1,
                Protocol = protocol,
                BackendInstanceId = "main",
                BackendPrincipalId = user.BackendPrincipalId,
                ProviderRevision = "1:artwork-v1",
                RetrievedAt = clock.UtcNow,
                PayloadJson = json,
                PayloadSha256 = FeedSnapshotHash(json),
                CorrelationId = "feed-concurrency"
            });
        }
        await db.SaveChangesAsync();
        var capability = new Mock<IProviderPlaylistCapability>();
        capability.SetupGet(item => item.ProviderId).Returns("spotify");
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync((ProviderExecutionContext context, ProviderUserPlaylistsRequest _) => ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify",
                feeds.Single(feed => feed.Account == context.Account!.AccountId).Cards.Select(card => new ProviderPlaylistSummary(
                    new("spotify", ProviderResourceKind.Playlist, card.SourcePlaylistId!), card.Name, new("spotify"), "revision", trackCount: 2)))));
        IProviderPlaylistCapability? implementation = capability.Object;
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.TryGetCapability("spotify", ProviderCapabilityKind.Playlist, out implementation)).Returns(true);
        registry.Setup(item => item.GetRequiredCapability<IProviderPlaylistCapability>("spotify", ProviderCapabilityKind.Playlist)).Returns(capability.Object);
        service = new(factory, clock, tags, taste, new(gateway.Object, timeProvider: clock), gateway.Object,
            NewReleaseService(), settings.Object, policies.Object, access.Object, registry: registry.Object, jobQueue: queue);
        return (capability, feeds.ToArray());
    }

    private static ProviderOutcome<ProviderPlaylistTrackPage> ConcurrentFeedPage(ProviderPlaylistTracksRequest request) =>
        ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
            new(request.PlaylistId, "Service mix", new("spotify"), "revision", trackCount: 2),
            new("spotify", Enumerable.Range(0, 2).Select(index =>
            {
                var resource = new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, index.ToString());
                return new ProviderPlaylistTrack(index, resource, metadata: new(resource, index == 0 ? "First song" : "Second song", [new("Artist")]));
            }))));

    private static string FeedSnapshotHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void FeedMaximum(ref int maximum, int current)
    {
        int previous;
        do { previous = Volatile.Read(ref maximum); if (previous >= current) return; }
        while (Interlocked.CompareExchange(ref maximum, current, previous) != previous);
    }
}
