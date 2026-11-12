using System.Globalization;
using allstarr.Core.ClientApiV2;
using allstarr.Core.ClientEvents;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ClientApiV2EventTests
{
    [Fact]
    public async Task ClientApiV2_EventsReplayMissedPayloadsInOrderAndIsolateListeners()
    {
        using var fixture = new EventsFixture();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var first = fixture.Subscribe(owner);
        using var foreign = fixture.Subscribe(other);
        fixture.Events.Publish(owner, ClientEvent.Releases(1));
        var cursor = await Read(first);
        first.Dispose();
        fixture.Events.Publish(owner, ClientEvent.Releases(2));
        fixture.Events.Publish(other, ClientEvent.Releases(99));
        fixture.Events.Publish(owner, ClientEvent.Releases(3));
        using var resumed = fixture.Subscribe(owner, cursor.Id);
        var second = await Read(resumed);
        var third = await Read(resumed);
        Assert.Contains("\"count\":2", second.Value.Json);
        Assert.Contains("\"count\":3", third.Value.Json);
        Assert.True(long.Parse(cursor.Id, CultureInfo.InvariantCulture) < long.Parse(second.Id, CultureInfo.InvariantCulture));
        Assert.True(long.Parse(second.Id, CultureInfo.InvariantCulture) < long.Parse(third.Id, CultureInfo.InvariantCulture));
        Assert.Contains("99", (await Read(foreign)).Value.Json);
        using var wrongCursor = fixture.Subscribe(other, cursor.Id);
        Assert.Equal("resync", (await Read(wrongCursor)).Value.Type);
        fixture.Events.Publish(other, ClientEvent.Releases(4));
        Assert.Contains("4", (await Read(wrongCursor)).Value.Json);
        fixture.Events.Publish(owner, ClientEvent.Releases(5));
        Assert.Contains("5", (await Read(resumed)).Value.Json);
    }

    [Fact]
    public async Task ClientApiV2_EventsBoundReplayByCountAndAgeAndExpireIdleListeners()
    {
        using var fixture = new EventsFixture();
        var owner = Guid.NewGuid();
        var subscription = fixture.Subscribe(owner);
        fixture.Events.Publish(owner, ClientEvent.Releases(-1));
        var oldest = await Read(subscription);
        subscription.Dispose();
        var observing = fixture.Subscribe(owner);
        fixture.Events.Publish(owner, ClientEvent.Releases(0));
        var retained = await Read(observing);
        for (var index = 1; index < 1000; index++) fixture.Events.Publish(owner, ClientEvent.Releases(index));
        observing.Dispose();
        using (var bounded = fixture.Subscribe(owner, retained.Id))
            for (var index = 1; index < 1000; index++)
                Assert.Equal($"{{\"count\":{index}}}", (await Read(bounded)).Value.Json);
        using (var replay = fixture.Subscribe(owner, oldest.Id))
            Assert.Equal("resync", (await Read(replay)).Value.Type);
        var current = fixture.Subscribe(owner);
        fixture.Events.Publish(owner, ClientEvent.Releases(1001));
        var recent = await Read(current);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        using (var expired = fixture.Subscribe(owner, recent.Id))
            Assert.Equal("resync", (await Read(expired)).Value.Type);
        current.Dispose();
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        fixture.Events.RemoveExpired();
        Assert.DoesNotContain(owner, fixture.Hub.Listeners);
        using var restarted = fixture.Subscribe(owner, recent.Id);
        Assert.Equal("resync", (await Read(restarted)).Value.Type);
        fixture.Events.Publish(owner, ClientEvent.Releases(1002));
        Assert.Contains("1002", (await Read(restarted)).Value.Json);
    }

    [Fact]
    public void ClientApiV2_EventsShareBothConnectionCapsWithV1()
    {
        using var fixture = new EventsFixture(new() { ConnectionsPerUser = 1, MaximumConnections = 2 });
        var owner = Guid.NewGuid();
        using var old = fixture.Hub.Subscribe(owner, null)!;
        Assert.Null(fixture.TrySubscribe(owner));
        using var next = fixture.Subscribe(Guid.NewGuid());
        Assert.Null(fixture.Hub.Subscribe(Guid.NewGuid(), null));
        old.Dispose();
        using var replacement = fixture.Subscribe(owner);
        Assert.Null(fixture.Hub.Subscribe(owner, null));
    }

    private static async Task<V2StreamEvent> Read(V2EventSubscription subscription)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return (await subscription.ReadAsync(timeout.Token))!;
    }

    private sealed class EventsFixture : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        public ManualTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
        public ClientEventHub Hub { get; }
        public V2EventStream Events { get; }
        public EventsFixture(ClientEventOptions? options = null)
        {
            Hub = new(options ?? new());
            Events = new(Hub, services.GetRequiredService<IServiceScopeFactory>(), Clock, NullLogger<V2EventStream>.Instance);
        }
        public V2EventSubscription Subscribe(Guid owner, string? cursor = null) => TrySubscribe(owner, cursor)!;
        public V2EventSubscription? TrySubscribe(Guid owner, string? cursor = null)
        {
            var principal = new AllstarrPrincipal(owner, "jellyfin", "primary", "listener", "Listener", false);
            var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener", principal,
                "events", Clock.GetUtcNow().AddMinutes(1), default);
            return Events.Subscribe(new DefaultHttpContext(), execution, cursor);
        }
        public void Dispose() { Events.Dispose(); Hub.Dispose(); services.Dispose(); }
    }
}
