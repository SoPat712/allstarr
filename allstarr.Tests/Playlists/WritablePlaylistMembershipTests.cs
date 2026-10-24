using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class WritablePlaylistMembershipTests
{
    [Fact]
    public async Task SyntheticCleanupRemovesOnlyTheConfirmedPlaylistAndKeepsOtherMembership()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var context = Context();
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new()
            {
                Id = context.Principal!.UserId,
                BackendType = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "owner",
                DisplayName = "Check listener",
                Enabled = true,
                IsSynthetic = true
            });
            await db.SaveChangesAsync();
        }
        var store = new WritablePlaylistMembership(factory, Mock.Of<IProtocolProviderGateway>(), Mock.Of<ILibraryIndexService>());
        await store.SaveAsync(context, "temporary-playlist", [new("first", "native-track")], default);
        await store.SaveAsync(context, "temporary-playlist", [], default);
        await store.SaveAsync(context, "existing-playlist", [new("existing", "native-track")], default);
        await store.ForgetSyntheticDeletedPlaylistAsync(context, "temporary-playlist", default);
        Assert.Null(await store.ReadAsync(context, "temporary-playlist", default));
        Assert.Single((await store.ReadAsync(context, "existing-playlist", default))!.Members);
        await using var verify = factory.CreateDbContext();
        Assert.Single(await verify.ExternalMetadataSnapshots.ToArrayAsync());
        (await verify.Users.SingleAsync()).IsSynthetic = false;
        await verify.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ForgetSyntheticDeletedPlaylistAsync(context, "existing-playlist", default));
        Assert.NotNull(await store.ReadAsync(context, "existing-playlist", default));
    }

    [Fact]
    public async Task Resolve_KnownProviderCopiesUseBackendIdsWithoutLosingDuplicateOccurrences()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var library = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        var context = Context();
        library.Setup(service => service.ResolveKnownTrackAsync(context, "ext-deezer-song-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync("native-copy");
        var store = new WritablePlaylistMembership(Mock.Of<IDbContextFactory<AllstarrDbContext>>(), gateway.Object, library.Object);
        var members = await store.ResolveAsync(context, ["native", "ext-deezer-song-A", "ext-deezer-song-A"], default);
        Assert.Equal(["native", "native-copy", "native-copy"], members.Select(member => member.ItemId));
        Assert.Equal(3, members.Select(member => member.OccurrenceId).Distinct().Count());
        Assert.All(members, member => Assert.False(member.IsProvider));
        library.Verify(service => service.ResolveKnownTrackAsync(context, "ext-deezer-song-A", It.IsAny<CancellationToken>()), Times.Once);
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Read_ReloadsPersistedOrderWithoutProviderCallsOrDatabaseWritesAndScopesBackend()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var context = Context();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new UserRecord
            {
                Id = context.Principal!.UserId,
                BackendType = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "owner",
                DisplayName = "Owner",
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var library = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        var store = new WritablePlaylistMembership(factory, providers.Object, library.Object);
        var members = new WritablePlaylistMember[]
        {
            new("native-entry", "native", NativeEntryId: "native-entry"),
            new("first-copy", "ext-deezer-song-A", new() { Id = "ext-deezer-song-A", Title = "A" }),
            new("second-copy", "ext-deezer-song-A", new() { Id = "ext-deezer-song-A", Title = "A" })
        };
        using (await store.LockAsync(default)) await store.SaveAsync(context, "0198a537719c7ea89e5a17e1f2f963f0", members, default);
        var reloaded = new WritablePlaylistMembership(factory, providers.Object, library.Object);
        var state = await reloaded.ReadAsync(context, "0198A537-719C-7EA8-9E5A-17E1F2F963F0", default);
        Assert.NotNull(state);
        Assert.Equal(members.Select(member => member.OccurrenceId), state.Members.Select(member => member.OccurrenceId));
        Assert.Null(await reloaded.ReadAsync(Context("other-backend"), "0198a537719c7ea89e5a17e1f2f963f0", default));
        await using (var db = await factory.CreateDbContextAsync()) Assert.Equal(1, await db.ExternalMetadataSnapshots.CountAsync());
        providers.VerifyNoOtherCalls();
        library.VerifyNoOtherCalls();
    }

    [Fact]
    public void Reconcile_ManualNativeAdditionsCannotReuseAnExistingOccurrenceIdentifier()
    {
        var saved = new WritablePlaylistState([new("existing", "native-a")]);
        var merged = WritablePlaylistMembership.Reconcile(saved,
            [new("from-backend", "native-a"), new("existing", "native-b")]);
        Assert.Equal(["native-a", "native-b"], merged.Select(member => member.ItemId));
        Assert.Equal("existing", merged[0].OccurrenceId);
        Assert.Equal(2, merged.Select(member => member.OccurrenceId).Distinct().Count());
    }

    [Fact]
    public void Replace_PreservesSurvivingProviderOccurrencesWhenSongOrderChanges()
    {
        var song = new allstarr.Models.Domain.Song { Id = "ext-deezer-song-A", Title = "A" };
        var before = new WritablePlaylistMember[]
        { new("first", song.Id, song), new("second", song.Id, song), new("other", "ext-deezer-song-B", new() { Title = "B" }) };
        var requested = new WritablePlaylistMember[]
        { before[2] with { OccurrenceId = "new-other" }, before[0] with { OccurrenceId = "new-first" }, before[1] with { OccurrenceId = "new-second" } };
        var preserve = typeof(WritablePlaylistMembership).GetMethod("PreserveOccurrences");
        Assert.NotNull(preserve);
        var result = Assert.IsAssignableFrom<IReadOnlyList<WritablePlaylistMember>>(preserve.Invoke(null, [before, requested]));
        Assert.Equal(["other", "first", "second"], result.Select(member => member.OccurrenceId));
    }

    private static ProtocolExecutionContext Context(string backend = "primary") => new(ProtocolKind.Jellyfin, backend, "owner",
        new AllstarrPrincipal(Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd"), "jellyfin", backend, "owner", "Owner", false),
        "playlist-contract", DateTimeOffset.UtcNow.AddMinutes(1), default);

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
