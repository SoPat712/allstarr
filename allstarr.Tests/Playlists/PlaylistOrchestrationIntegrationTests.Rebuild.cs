using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task JellyfinRebuildSwitchesDestinationAndDeletesOldCopy(bool administrator, bool syncMetadata)
    {
        _source.Snapshot = Snapshot("rebuild-source", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 9001));
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("target-created", "Listener name", ["local-1"])
        { ExistingArtwork = [1, 2, 3], EmptyNewPlaylistArtwork = true };
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            link.SyncName = syncMetadata;
            link.SyncDescription = syncMetadata;
            link.SyncArtwork = syncMetadata;
            await db.SaveChangesAsync();
        }
        _source.Artwork = ProviderOutcome<ProviderPlaylistArtwork>.Success(new([4, 5, 6], "image/png"));
        var service = JellyfinService(backend);
        var context = Context();
        if (administrator)
        {
            var id = Guid.CreateVersion7();
            await using var adminDb = await _factory.CreateDbContextAsync();
            adminDb.Users.Add(new UserRecord
            {
                Id = id,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "administrator",
                DisplayName = "Administrator",
                IsAdmin = true,
                Enabled = true,
                CreatedAt = _now,
                UpdatedAt = _now,
                LastSeenAt = _now
            });
            await adminDb.SaveChangesAsync();
            context = new(ProtocolKind.Jellyfin, "backend", "administrator",
                new(id, "jellyfin", "backend", "administrator", "Administrator", true), "rebuild-admin",
                _now.AddMinutes(5), default);
        }
        var updated = await service.RebuildAsync(context, _link);
        Assert.Equal("p2", updated.TargetPlaylistId);
        Assert.False(backend.Playlists.ContainsKey("target-created"));
        Assert.Equal(syncMetadata ? "Provider Mix" : "Listener name", backend.Playlists["p2"].Name);
        Assert.Equal(syncMetadata ? "Description" : null, backend.Playlists["p2"].Description);
        Assert.Equal(["local-1"], backend.Playlists["p2"].Members);
        Assert.Equal(syncMetadata ? new byte[] { 4, 5, 6 } : [1, 2, 3], backend.ExistingArtwork);
        Assert.True(backend.ArtworkWritten);
        Assert.Contains(backend.Requests, item => item.Path == "Items/p2/Images/Primary");
        Assert.All(backend.Requests, item => Assert.Contains("server-key", item.Authorization));
        await using var verify = await _factory.CreateDbContextAsync();
        var membership = await verify.PlaylistTargetMemberships.SingleAsync();
        Assert.True(membership.Active);
        Assert.Null(membership.RemovalConfirmedAt);
        Assert.Equal("p2", (await verify.PlaylistLinks.SingleAsync()).TargetPlaylistId);
        var projection = await new DurablePlaylistProjectionReader(_factory, new TestBackendLibraryAccess(_factory, "music"))
            .ReadByLinkIdAsync(_user, _link);
        Assert.Equal(1, projection!.MaterializedCount);
        Assert.Null(projection.SyncFailureCode);
        Assert.Equal("rebuild", (await verify.AuditEvents.SingleAsync(item => item.Category == "playlist-link")).Action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinRebuildFailureDeletesStagedCopyAndLeavesLinkUntouched(bool timeout)
    {
        _source.Snapshot = Snapshot("rollback-source", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 9002));
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("target-created", "Mix", ["local-1"])
        { FailNextMetadata = !timeout, TimeoutNextMetadata = timeout };
        await using var before = await _factory.CreateDbContextAsync();
        var beforeLink = await before.PlaylistLinks.SingleAsync();
        beforeLink.SyncArtwork = false;
        await before.SaveChangesAsync();
        var revision = beforeLink.Revision;
        await Assert.ThrowsAsync<PlaylistRebuildException>(() => JellyfinService(backend).RebuildAsync(Context(), _link));
        Assert.Equal("target-created", Assert.Single(backend.Playlists).Key);
        await using var after = await _factory.CreateDbContextAsync();
        var link = await after.PlaylistLinks.SingleAsync();
        Assert.Equal("target-created", link.TargetPlaylistId);
        Assert.Equal(revision, link.Revision);
    }

    [Fact]
    public async Task JellyfinRebuildRefusesOneTimeImportsBeforeBackendAccess()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var link = await db.PlaylistLinks.SingleAsync();
        link.ImportMode = PlaylistImportMode.OneTime;
        await db.SaveChangesAsync();
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("old", "Mix", []);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JellyfinService(backend).RebuildAsync(Context(), _link));
        Assert.Equal("Imports keep your own order.", error.Message);
        Assert.Empty(backend.Requests);
    }

    [Fact]
    public async Task OneTimePlaylistReplayKeepsExistingOrderAndDoesNotRecreate()
    {
        await SetLink(importMode: PlaylistImportMode.OneTime, materialization: PlaylistMaterializationMode.Recreate);
        _source.Snapshot = Snapshot("own-copy", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 9003));
        _target.Seed("target-created", ["manual", "local-1", "stale"]);
        await _service.RunAsync(Context(), new(_link, 9004));
        Assert.Equal(BackendPlaylistWriteMode.Reconcile, _target.LastWrite!.Mode);
        Assert.True(_target.LastWrite.PreserveExistingOrder);
        Assert.False(_target.LastWrite.RemoveStaleSyncOwnedItems);
        Assert.Equal(["manual", "local-1", "stale"], _target.LastWrite.OrderedBackendItemIds);
    }

    private PlaylistOrchestrationService JellyfinService(HttpMessageHandler backend, allstarr.Core.Secrets.EncryptedSecretStore? secrets = null)
    {
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(backend, false));
        var target = new JellyfinPlaylistTarget(clients.Object, Options.Create(new JellyfinSettings
        { Url = "https://jellyfin.test", ApiKey = "server-key" }), secrets);
        var resolver = new Mock<IBackendPlaylistTargetResolver>();
        resolver.Setup(item => item.Resolve("jellyfin")).Returns(target);
        return new(_factory, _source, resolver.Object, new PlaylistMaterializationPlanner(),
            new allstarr.Core.Matching.TrackMatchDecisionEngine(), _trackMatches, new Clock(_now),
            new KeyedAsyncLock(), new TestBackendLibraryAccess(_factory, "music"));
    }
}
