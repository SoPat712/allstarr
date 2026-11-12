using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryIndexServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReindexingReadsSourcePayloadsOnlyWhenTheTrackChanges(bool changed)
    {
        var viewer = Context(_userA, "principal-a");
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "source", null, null);
        var identity = ReadIdentity(recording.Recording.Id, "deezer", "unrelated-source");
        await using (var db = _factory.CreateDbContext())
        {
            db.ProviderTrackIdentities.Add(identity);
            db.ExternalMetadataSnapshots.Add(ReadSnapshot(identity));
            await db.SaveChangesAsync();
        }
        var reads = new RecordingReadCounter();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(reads).Options);
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        var identities = new TrackIdentityService(factory, state, _clock);
        var service = new LibraryIndexService(factory, state, _clock, identities, _libraryAccess.Object);
        var indexed = await service.UpsertAsync(viewer, Input());
        reads.Commands.Clear();
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);

        var repeated = await service.UpsertAsync(viewer, changed ? Input() with { Title = "Renamed" } : Input());

        Assert.Equal(indexed.Id, repeated.Id);
        Assert.Equal(_clock.UtcNow, repeated.IndexedAt);
        Assert.Equal(changed, reads.Commands.Any(command =>
            command.Contains("external_metadata_snapshots", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NewIdentityLinksAnUnchangedIndexedTrackWithoutAnotherScan()
    {
        var viewer = Context(_userA, "principal-a");
        var indexed = await _service.UpsertAsync(viewer, Input());
        Assert.Null((await _service.UpsertAsync(viewer, Input())).CanonicalRecordingId);
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "source", Input().Isrc, null);
        var execution = ReindexingSourceContext(viewer.RequireActor());

        var linked = await _identities.LinkAsync(execution, new(recording.Recording.Id,
            new("deezer", ProviderResourceKind.Track, "new-source"),
            ProviderIdentityScope.Catalog, ProviderIdentityVerification.Verified, "isrc-lookup", 1));

        Assert.Equal(TrackIdentityLinkStatus.Created, linked.Status);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(recording.Recording.Id,
            (await db.LibraryTracks.SingleAsync(track => track.Id == indexed.Id)).CanonicalRecordingId);
        Assert.Empty(await db.ExternalMetadataSnapshots.ToArrayAsync());
    }

    [Fact]
    public async Task NewMetadataLinksAnUnchangedIndexedTrackWithoutAnotherScan()
    {
        var viewer = Context(_userA, "principal-a");
        var input = Input() with { Isrc = null };
        var indexed = await _service.UpsertAsync(viewer, input);
        Assert.Null((await _service.UpsertAsync(viewer, input)).CanonicalRecordingId);
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "source", null, null);
        var execution = ReindexingSourceContext(viewer.RequireActor());
        var resource = new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "new-source");
        await _identities.LinkAsync(execution, new(recording.Recording.Id, resource,
            ProviderIdentityScope.Catalog, ProviderIdentityVerification.Verified, "exact-provider-id", 1));
        await using (var db = _factory.CreateDbContext())
        {
            Assert.Null((await db.LibraryTracks.SingleAsync(track => track.Id == indexed.Id)).CanonicalRecordingId);
        }

        await _identities.RememberPlaybackMetadataAsync(execution, resource,
            new Song { Title = input.Title, Artist = input.Artist, Duration = 240 });

        await using var saved = _factory.CreateDbContext();
        Assert.Equal(recording.Recording.Id,
            (await saved.LibraryTracks.SingleAsync(track => track.Id == indexed.Id)).CanonicalRecordingId);
        Assert.Contains(await saved.ExternalMetadataSnapshots.ToArrayAsync(), snapshot =>
            snapshot.ProviderId == "deezer" && snapshot.ResourceKind == "playback-metadata");
    }

    [Fact]
    public async Task MovingAnUnchangedTrackBetweenLibrariesStillLinksSources()
    {
        var viewer = Context(_userA, "principal-a");
        _libraryAccess.Setup(item => item.ResolveAsync(viewer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music", "other"]));
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.LinkLibraryRecordingsAsync(It.IsAny<ProviderActorContext>(),
                It.IsAny<string>(), null, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LibraryRecordingLinkResult(0, 0, 0));
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        var service = new LibraryIndexService(_factory, state, _clock, identities.Object, _libraryAccess.Object);
        var indexed = await service.UpsertAsync(viewer, Input());
        identities.Invocations.Clear();

        var moved = await service.UpsertAsync(viewer, Input() with { BackendLibraryId = "other" });

        Assert.Equal(indexed.Id, moved.Id);
        identities.Verify(item => item.LinkLibraryRecordingsAsync(It.IsAny<ProviderActorContext>(),
            viewer.CorrelationId, null, indexed.Id, It.IsAny<CancellationToken>()), Times.Once);
        await using var db = _factory.CreateDbContext();
        Assert.Equal("other", (await db.LibraryTracks.SingleAsync()).BackendLibraryId);
    }

    [Theory]
    [InlineData(false, "track")]
    [InlineData(true, "track")]
    [InlineData(false, "playback-metadata")]
    public async Task CapturedSourceMetadataLinksAnUnchangedIndexedTrack(bool explicitIdentity, string kind)
    {
        var viewer = Context(_userA, "principal-a");
        var input = Input() with { Isrc = null };
        var indexed = await _service.UpsertAsync(viewer, input);
        Assert.Null((await _service.UpsertAsync(viewer, input)).CanonicalRecordingId);
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "source", null, null);
        var account = ReadAccount(_userA);
        account.ProviderId = "deezer";
        var identity = ReadIdentity(recording.Recording.Id, "deezer", "captured-source");
        if (!explicitIdentity) identity.CatalogNamespace = "alternate";
        identity.Scope = ProviderIdentityScope.Account;
        identity.ProviderAccountId = account.Id;
        await using (var db = _factory.CreateDbContext())
        {
            db.ProviderAccounts.Add(account);
            db.ProviderTrackIdentities.Add(identity);
            await db.SaveChangesAsync();
        }
        var commands = new TrackMatchCommandService(_factory, new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(_factory), _clock, _libraryAccess.Object, trackIdentities: _identities);
        var payload = JsonSerializer.Serialize(new
        {
            input.Title,
            input.Artist,
            input.DurationMilliseconds
        });

        var captured = await commands.CaptureSnapshotAsync(viewer,
            new(account.Id, "deezer", kind, identity.ExternalIdHash, 1, "source", payload,
                CanonicalCatalogKeys.Hash(payload), explicitIdentity ? identity.Id : null));

        await using var saved = _factory.CreateDbContext();
        Assert.Equal(recording.Recording.Id,
            (await saved.LibraryTracks.SingleAsync(track => track.Id == indexed.Id)).CanonicalRecordingId);
        Assert.Equal(captured.Id, (await saved.ExternalMetadataSnapshots.SingleAsync()).Id);
    }

    private ProviderExecutionContext ReindexingSourceContext(ProviderActorContext actor) => new(
        actor,
        "deezer",
        null,
        new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, allowTranscode: true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: true,
            allowSharedAccount: true,
            allowManagedDownloads: false,
            allowedProviderIds: ["deezer"]),
        operationId: "source-link",
        correlationId: "source-link",
        deadline: _clock.UtcNow.AddMinutes(5),
        cancellationToken: CancellationToken.None);
}
