using System.Diagnostics;
using allstarr.Core.ClientEvents;

namespace allstarr.Tests;

public sealed class ClientEventHubTests
{
    [Fact]
    public void ClientEvent_LyricsObservationsOnlyPublishEffectiveChangesToTheirOwner()
    {
        using var hub = new ClientEventHub(new());
        using var updates = new ClientLyricsUpdates(hub);
        var owner = Guid.NewGuid();
        using var subscription = hub.Subscribe(owner, null)!;
        using var other = hub.Subscribe(Guid.NewGuid(), null)!;
        updates.Observe(owner, "native", new() { Source = "backend", PlainLyrics = "First" });
        updates.Observe(owner, "native", new() { Source = "backend", PlainLyrics = "First" });
        Assert.Equal(0, subscription.PendingCount);
        updates.Observe(owner, "native", new() { Source = "backend", PlainLyrics = "Second" });
        Assert.Equal(1, subscription.PendingCount);
        Assert.Equal(0, other.PendingCount);
    }

    [Fact]
    public async Task ClientEvent_CoalescesLatestValueAndDropsOldestWithoutBlocking()
    {
        using var hub = new ClientEventHub(new() { Capacity = 3, CoalesceWindow = TimeSpan.FromMilliseconds(10), EventsPerSecond = 1000 });
        var owner = Guid.NewGuid();
        using var subscription = hub.Subscribe(owner, "phone")!;
        for (var index = 0; index < 10000; index++) hub.Publish(owner, ClientEvent.Keep("same", "inProgress", index / 10000d));
        Assert.Equal(1, subscription.PendingCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var latest = await subscription.ReadAsync(timeout.Token);
        Assert.Contains("0.9999", latest!.Json);
        for (var index = 0; index < 1000; index++) hub.Publish(owner, ClientEvent.Match(index.ToString(), "matched"));
        Assert.Equal(3, subscription.PendingCount);
        Assert.Equal("997", (await subscription.ReadAsync(timeout.Token))!.Key);
    }

    [Fact]
    public async Task ClientEvent_IsolatesUsersAndPlaybackDevices()
    {
        using var hub = new ClientEventHub(new() { CoalesceWindow = TimeSpan.Zero, EventsPerSecond = 1000 });
        var owner = Guid.NewGuid();
        using var phone = hub.Subscribe(owner, "phone")!;
        using var tablet = hub.Subscribe(owner, "tablet")!;
        using var other = hub.Subscribe(Guid.NewGuid(), "phone")!;
        hub.Publish(owner, ClientEvent.Playback("song"), "phone");
        hub.Publish(owner, ClientEvent.Playback("ignored"));
        Assert.Equal(1, phone.PendingCount);
        Assert.Equal(0, tablet.PendingCount);
        Assert.Equal(0, other.PendingCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("playback.updated", (await phone.ReadAsync(timeout.Token))!.Type);
    }

    [Fact]
    public void ClientEvent_ConnectionCapsAndDisposeReleaseSlots()
    {
        using var hub = new ClientEventHub(new() { ConnectionsPerUser = 1, MaximumConnections = 2 });
        var owner = Guid.NewGuid();
        var first = hub.Subscribe(owner, null)!;
        Assert.Null(hub.Subscribe(owner, "phone"));
        using var second = hub.Subscribe(Guid.NewGuid(), null)!;
        Assert.Null(hub.Subscribe(Guid.NewGuid(), null));
        first.Dispose();
        using var replacement = hub.Subscribe(owner, null);
        Assert.NotNull(replacement);
        Assert.Equal(2, hub.ConnectionCount);
    }

    [Fact]
    public async Task ClientEvent_RateLimitAndShutdownCompletePendingReads()
    {
        using var hub = new ClientEventHub(new() { CoalesceWindow = TimeSpan.Zero, EventsPerSecond = 10 });
        var owner = Guid.NewGuid();
        using var subscription = hub.Subscribe(owner, null)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 4; index++) hub.Publish(owner, ClientEvent.Match(index.ToString(), "matched"));
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 4; index++) Assert.NotNull(await subscription.ReadAsync(timeout.Token));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(280));
        var pending = subscription.ReadAsync(timeout.Token);
        hub.Dispose();
        Assert.Null(await pending);
        Assert.Equal(0, hub.ConnectionCount);
        Assert.Null(hub.Subscribe(owner, null));
    }
}
