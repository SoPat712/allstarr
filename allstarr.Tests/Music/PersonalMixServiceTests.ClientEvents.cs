using System.Text.Json;
using allstarr.Core.ClientEvents;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    private static string EventJson(ClientEvent value) => JsonSerializer.Serialize(value);

    private PersonalMixService EventService(IClientEventPublisher publisher) => new(factory, clock, tags, taste,
        new MusicMixService(gateway.Object, timeProvider: clock), gateway.Object, NewReleaseService(),
        settings.Object, policies.Object, access.Object, events: publisher);

    [Fact]
    public async Task Refresh_job_publishes_each_committed_mix_and_dry_run_stays_silent()
    {
        await Seed();
        var published = new List<(Guid Owner, string Value)>();
        var publisher = new Mock<IClientEventPublisher>();
        publisher.Setup(item => item.Publish(It.IsAny<Guid>(), It.IsAny<ClientEvent>(), It.IsAny<string?>()))
            .Callback<Guid, ClientEvent, string?>((listener, value, device) =>
            {
                Assert.Null(device);
                using var db = factory.CreateDbContext();
                Assert.True(db.ExternalMetadataSnapshots.Any(item => item.OwnerUserId == listener && item.ResourceKind == "personal-mixes"));
                published.Add((listener, JsonSerializer.Serialize(value)));
            });
        service = EventService(publisher.Object);
        var id = (await service.ListAsync(owner)).Single(item => item.Kind == "on-repeat").Id;
        await RunRefreshJob("mix-refresh:events", id);
        Assert.Equal((owner, JsonSerializer.Serialize(ClientEvent.Mix(id))), Assert.Single(published));
        var committed = await service.GetAsync(owner, id);
        Assert.Equal(1, committed.Generation);

        published.Clear();
        clock.UtcNow += TimeSpan.FromMinutes(16);
        await service.GenerateAsync(owner, id, dryRun: true);
        Assert.Empty(published);
        Assert.Equal(committed.Generation, (await service.GetAsync(owner, id)).Generation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(owner, id, token: new CancellationToken(true)));
        Assert.Empty(published);

        await RunRefreshJob("schedule:mix-events");
        var generated = await service.ListAsync(owner);
        Assert.Equal(generated.Select(item => EventJson(ClientEvent.Mix(item.Id))).Order(),
            published.Select(item => item.Value).Order());
        Assert.All(published, item => Assert.Equal(owner, item.Owner));
    }

    [Fact]
    public async Task Custom_preview_is_silent_and_save_delete_publish_only_committed_changes()
    {
        await Seed();
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", It.IsAny<int>()))
            .ReturnsAsync(songs.Values.ToList());
        var publisher = new Mock<IClientEventPublisher>();
        service = EventService(publisher.Object);
        var preview = await service.StartAsync(owner, new("song", songs["0"].Id, 25));
        publisher.VerifyNoOtherCalls();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SaveCustomAsync(other, preview.Id, "Saved"));
        publisher.VerifyNoOtherCalls();

        var saved = await service.SaveCustomAsync(owner, preview.Id, "Saved");
        publisher.Verify(item => item.Publish(owner,
            It.Is<ClientEvent>(value => EventJson(value) == EventJson(ClientEvent.Mix(saved.Id))), null), Times.Once);
        publisher.Invocations.Clear();
        await service.SaveCustomAsync(owner, preview.Id, "Again");
        publisher.VerifyNoOtherCalls();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteCustomAsync(other, saved.Id));
        publisher.VerifyNoOtherCalls();

        await service.DeleteCustomAsync(owner, saved.Id);
        publisher.Verify(item => item.Publish(owner,
            It.Is<ClientEvent>(value => EventJson(value) == EventJson(ClientEvent.Mix(saved.Id))), null), Times.Once);
        Assert.DoesNotContain(await service.ListAsync(owner), item => item.Id == saved.Id);
    }

    [Fact]
    public async Task Mix_visibility_changes_publish_once_for_saved_and_external_mixes()
    {
        var publisher = new Mock<IClientEventPublisher>();
        service = EventService(publisher.Object);
        var mix = (await service.ListAsync(owner)).First();
        await service.SetInAppsAsync(owner, mix.Id, true, default);
        await service.SetInAppsAsync(owner, mix.Id, true, default);
        publisher.Verify(item => item.Publish(owner,
            It.Is<ClientEvent>(value => EventJson(value) == EventJson(ClientEvent.Mix(mix.Id))), null), Times.Once);

        publisher.Invocations.Clear();
        var link = await ExternalLink();
        await service.SetInAppsAsync(owner, link.Id, false, default);
        await service.SetInAppsAsync(owner, link.Id, false, default);
        publisher.Verify(item => item.Publish(owner,
            It.Is<ClientEvent>(value => EventJson(value) == EventJson(ClientEvent.Mix(link.Id))), null), Times.Once);
        await using var db = factory.CreateDbContext();
        Assert.False((await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id)).Enabled);
    }
}
