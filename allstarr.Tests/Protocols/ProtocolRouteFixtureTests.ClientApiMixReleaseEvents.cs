using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientEvents;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_EventsMixRefreshJobPublishesCommittedMixOnlyToOwner()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns([]);
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid owner, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, "Lossless", .07)
            { UserId = owner });
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
        });
        using var client = CreateClientApiClient(factory);
        var identities = factory.Services.GetRequiredService<BackendIdentityResolver>();
        var principal = (await identities.ResolveAsync(new("Jellyfin", "listener")))!;
        var other = (await identities.ResolveAsync(new("Jellyfin", "other")))!;
        var now = factory.Services.GetRequiredService<IPlatformClock>().UtcNow;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var track = new LibraryTrackRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = principal.UserId,
                Protocol = principal.BackendType,
                BackendInstanceId = principal.BackendInstanceId,
                BackendLibraryId = "music",
                BackendItemId = "mix-song",
                Title = "Fixture song",
                Artist = "Fixture artist"
            };
            db.LibraryTracks.Add(track);
            for (var index = 0; index < 5; index++) db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = principal.UserId,
                Protocol = principal.BackendType,
                BackendInstanceId = principal.BackendInstanceId,
                OccurrenceKey = "mix-play-" + index,
                TrackReference = track.BackendItemId,
                LibraryTrackId = track.Id,
                Title = track.Title,
                Artist = track.Artist,
                State = ListeningEventState.Completed,
                SourceKind = "protocol",
                ListenedAt = now.AddMinutes(-5),
                UpdatedAt = now.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
        }
        var mixes = factory.Services.GetRequiredService<PersonalMixService>();
        var mix = (await mixes.ListAsync(principal.UserId)).Single(item => item.Kind == "on-repeat");
        using var stream = await OpenClientEventsAsync(client);
        using var foreign = factory.Services.GetRequiredService<ClientEventHub>().Subscribe(other.UserId, null)!;
        var queue = factory.Services.GetRequiredService<DurableJobQueue>();
        var queued = await queue.EnqueueAsync(new DurableJobEnqueueRequest<Guid>(PersonalMixService.JobType,
            "mix-refresh:client-events", mix.Id, principal.UserId));
        var claim = await queue.ClaimNextAsync("fixture-mix-events", [PersonalMixService.JobType]);
        Assert.NotNull(claim);
        Assert.Equal(queued.JobId, claim.JobId);
        var handler = factory.Services.GetServices<IDurableJobHandler>().Single(item => item.JobType == PersonalMixService.JobType);
        var completion = await handler.ExecuteAsync(new(claim, factory.Services), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        await queue.CompleteAsync(claim, completion);

        var changed = await stream.EventAsync("mix.updated");
        Assert.Equal(["mixId"], changed.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.Equal(mix.Id, changed.GetProperty("mixId").GetGuid());
        var committed = await mixes.GetAsync(principal.UserId, mix.Id);
        Assert.Equal(1, committed.Generation);
        Assert.Equal("mix-song", Assert.Single(committed.Songs).Song.Id);
        Assert.Equal(0, foreign.PendingCount);
    }

    [Fact]
    public async Task ClientApi_EventsReleasePollAndDismissPublishCommittedInboxCountsOnlyToOwner()
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        var album = new ProviderAlbumMetadata(new("deezer", ProviderResourceKind.Album, "release"), "Fixture release",
            [new("Fixture artist", new("deezer", ProviderResourceKind.Artist, "artist"))],
            releaseDate: DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"), isExplicit: false,
            upc: "0123456789012", releaseType: "album");
        metadata.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer", [album])));
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
            {
                var descriptor = new ProviderDescriptor("deezer", "Catalog", "Fixture catalog", ProviderOrigin.BuiltIn, "1", "1",
                    [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                        "1", ["getArtistAlbums"])], new());
                var context = new ProviderExecutionContext(request.Actor, "deezer", null, request.Policy,
                    request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken);
                return new ProviderRoutePlan<IProviderMetadataCapability>(request,
                    [new(0, descriptor, descriptor.Capabilities.Single(), metadata.Object, context, null)],
                    new(request.CorrelationId, request.Capability, "deezer", null, []));
            });
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"), services =>
        {
            services.RemoveAll<IProviderRouter>();
            services.AddSingleton(router.Object);
        });
        using var client = CreateClientApiClient(factory);
        var identities = factory.Services.GetRequiredService<BackendIdentityResolver>();
        var owner = (await identities.ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var other = (await identities.ResolveAsync(new("Jellyfin", "other")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        await releases.FollowAsync(owner, new("deezer", "artist", "Fixture artist"));
        using var stream = await OpenClientEventsAsync(client);
        using var foreign = factory.Services.GetRequiredService<ClientEventHub>().Subscribe(other, null)!;

        Assert.Equal(DurableJobCompletionKind.Succeeded, (await releases.PollAsync(owner, Guid.NewGuid(), default)).Kind);
        var added = await stream.EventAsync("releases.updated");
        Assert.Equal(["count"], added.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.Equal(1, added.GetProperty("count").GetInt32());
        var entry = Assert.Single((await releases.ReadAsync(owner)).Entries);
        Assert.True(await releases.DismissAsync(owner, entry.Id));
        Assert.Equal(0, (await stream.EventAsync("releases.updated")).GetProperty("count").GetInt32());
        Assert.Empty((await releases.ReadAsync(owner)).Entries);
        Assert.Equal(0, foreign.PendingCount);
        metadata.Verify(item => item.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()), Times.Once);
    }
}
