using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class FavoriteLinkTests
{
    [Fact]
    public async Task ExactPairRequestsReplayAndMemberReservationsRejectCollisions()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.CreateRequest();
        var first = await f.Service.CreateAsync(f.Admin, request, default);
        var replay = await f.Service.CreateAsync(f.Admin, request, default);
        var pairReplay = await f.Service.CreateAsync(f.Admin, request with { RequestId = Guid.NewGuid() }, default);
        Assert.True(first.Created);
        Assert.True(replay.Replayed);
        Assert.False(pairReplay.Created);
        Assert.Equal(first.LinkId, replay.LinkId);
        Assert.Equal(first.JobId, pairReplay.JobId);
        var other = Fixture.Copy(f.Subsonic, Guid.NewGuid(), "another");
        await using (var db = f.Factory.CreateDbContext()) { db.Users.Add(other); await db.SaveChangesAsync(); }
        var changed = request with { SubsonicUserId = other.Id, SubsonicFingerprint = FavoriteLinkScope.Fingerprint(other) };
        Assert.Equal("favorite_link_request_conflict", (await Assert.ThrowsAsync<FavoriteLinkException>(() => f.Service.CreateAsync(f.Admin, changed, default))).Code);
        Assert.Equal("favorite_link_collision", (await Assert.ThrowsAsync<FavoriteLinkException>(() => f.Service.CreateAsync(f.Admin, changed with { RequestId = Guid.NewGuid() }, default))).Code);
        await using var saved = f.Factory.CreateDbContext();
        Assert.Single(await saved.Set<ProviderFavoriteLinkRecord>().ToArrayAsync());
        Assert.Equal(2, await saved.Set<ProviderFavoriteLinkMemberRecord>().CountAsync());
        Assert.Equal(3, await saved.Users.CountAsync());
        Assert.Single(await saved.Jobs.ToArrayAsync());
        Assert.Equal(2, await saved.Set<ProviderFavoriteLinkRequestRecord>().CountAsync());
    }

    [Fact]
    public async Task ConcurrentCreateAndReplayHaveOneLinkAndOneUnion()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.CreateRequest();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Service.CreateAsync(f.Admin, request, default)));
        Assert.Single(results.Select(item => item.LinkId).Distinct());
        Assert.Single(results, item => item.Created);
    }

    [Theory]
    [InlineData("listener")]
    [InlineData("dialect")]
    [InlineData("disabled")]
    [InlineData("principal")]
    public async Task CurrentAdministratorValidationPrecedesReplay(string changed)
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.CreateRequest();
        await f.Service.CreateAsync(f.Admin, request, default);
        var session = f.Admin;
        if (changed == "listener") session = Fixture.Session(f.Subsonic, false);
        if (changed == "dialect") f.Configuration["Backend:Type"] = "Subsonic";
        await using (var db = f.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(item => item.Id == f.Jellyfin.Id);
            if (changed == "disabled") user.Enabled = false;
            if (changed == "principal") user.BackendPrincipalId = "replacement";
            await db.SaveChangesAsync();
        }
        Assert.Equal(403, (await Assert.ThrowsAsync<FavoriteLinkException>(() => f.Service.CreateAsync(session, request, default))).StatusCode);
    }

    [Theory]
    [InlineData("synthetic")]
    [InlineData("disabled")]
    [InlineData("unseen")]
    [InlineData("fingerprint")]
    public async Task PreviewAndCreateRevalidateThePreviouslySignedInMember(string change)
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.CreateRequest();
        await using (var db = f.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(item => item.Id == f.Subsonic.Id);
            if (change == "synthetic") user.IsSynthetic = true;
            if (change == "disabled") user.Enabled = false;
            if (change == "unseen") user.LastSeenAt = default;
            if (change == "fingerprint") user.BackendPrincipalId = "replacement";
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<FavoriteLinkException>(() => f.Service.PreviewAsync(f.Admin,
            new(request.JellyfinUserId, request.JellyfinFingerprint, request.SubsonicUserId, request.SubsonicFingerprint), default));
        await Assert.ThrowsAsync<FavoriteLinkException>(() => f.Service.CreateAsync(f.Admin, request, default));
        await using var saved = f.Factory.CreateDbContext();
        Assert.Empty(await saved.Set<ProviderFavoriteLinkRecord>().ToArrayAsync());
        Assert.Empty(await saved.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task PendingFalseZeroTombstoneAndUnchangedOwnRevisionCannotResurrectOldIntent()
    {
        await using var f = await Fixture.CreateAsync();
        var old = await f.Heart(f.Jellyfin, true, "same");
        var linked = await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        await f.Heart(f.Subsonic, false, "peer");
        Assert.Empty(await f.Reader.ReadAsync(Fixture.Context(f.Jellyfin)));
        await f.RunUnion(linked.JobId);
        Assert.Empty(await f.Reader.ReadAsync(Fixture.Context(f.Subsonic)));
        var fresh = await f.Heart(f.Jellyfin, true, "same");
        Assert.NotEqual(old.EventId, fresh.EventId);
        Assert.False((await f.Heart(f.Jellyfin, true, "same")).Created);
        Assert.Single(await f.Reader.ReadAsync(Fixture.Context(f.Subsonic)));
        await using var db = f.Factory.CreateDbContext();
        var shared = Assert.Single(await db.Set<LinkedProviderFavoriteStateRecord>().ToArrayAsync());
        Assert.True(shared.IsFavorite);
        Assert.Equal(1, shared.LifecycleVersion);
        Assert.Equal(3, await db.FavoriteEvents.CountAsync());
        Assert.False((await db.FavoriteStates.SingleAsync(item => item.OwnerUserId == f.Subsonic.Id)).IsFavorite);
    }

    [Fact]
    public async Task SameValuePeerHeartPreservesTheOriginalCurrentDownloadAndSharedTimestamp()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        var first = await f.Heart(f.Jellyfin, true, "first");
        var claim = (await f.Jobs.ClaimNextAsync("first", [FavoriteActionPipeline.JobType]))!;
        f.Clock.UtcNow = f.Clock.UtcNow.AddMinutes(1);
        await f.Heart(f.Subsonic, true, "second");
        await using (var db = f.Factory.CreateDbContext())
        {
            var state = await db.Set<LinkedProviderFavoriteStateRecord>().SingleAsync();
            Assert.Equal(0, state.LifecycleVersion);
            Assert.Equal(f.Start, state.EffectiveAt);
            Assert.Null((await db.Jobs.SingleAsync(item => item.Id == first.JobId)).CancellationRequestedAt);
        }
        var calls = new List<Guid>();
        var handler = new FavoriteActionJobHandler(f.Factory, [new Executor(calls)], f.Clock);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default)).Kind);
        Assert.Equal(new[] { f.Jellyfin.Id }, calls);
        await f.Heart(f.Subsonic, false, "second");
        var again = await f.Heart(f.Jellyfin, true, "first");
        Assert.NotEqual(first.EventId, again.EventId);
        await using var verify = f.Factory.CreateDbContext();
        Assert.Equal(2, (await verify.Set<LinkedProviderFavoriteStateRecord>().SingleAsync()).LifecycleVersion);
    }

    [Fact]
    public async Task SyntheticTransitionCannotTaintPendingUnionOrCancelTheRealPeer()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Heart(f.Jellyfin, true, "initial");
        var linked = await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        var peer = await f.Heart(f.Subsonic, true, "peer");
        await using (var db = f.Factory.CreateDbContext()) { (await db.Users.SingleAsync(item => item.Id == f.Jellyfin.Id)).IsSynthetic = true; await db.SaveChangesAsync(); }
        await f.Heart(f.Jellyfin, false, "test");
        await using (var db = f.Factory.CreateDbContext())
        {
            Assert.Null((await db.Jobs.SingleAsync(item => item.Id == peer.JobId)).CancellationRequestedAt);
            Assert.True((await db.Set<LinkedProviderFavoriteStateRecord>().SingleAsync()).IsFavorite);
        }
        await f.RunUnion(linked.JobId);
        await f.Heart(f.Subsonic, false, "peer");
        await f.Heart(f.Jellyfin, true, "test");
        Assert.Empty(await f.Reader.ReadAsync(Fixture.Context(f.Subsonic)));
        await using var saved = f.Factory.CreateDbContext();
        var synthetic = await saved.FavoriteEvents.Where(item => item.OwnerUserId == f.Jellyfin.Id).OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).FirstAsync();
        Assert.False(synthetic.IsRealProviderIntent);
        Assert.Null(synthetic.SharedLinkId);
        Assert.False((await saved.Set<LinkedProviderFavoriteStateRecord>().SingleAsync()).IsFavorite);
    }

    [Fact]
    public async Task FrozenPrincipalAndBoundsExcludeReplacementIdentityEvenWithAnOlderClock()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Heart(f.Subsonic, true, "old", "ext-deezer-song-Original");
        var linked = await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        await using (var db = f.Factory.CreateDbContext()) { (await db.Users.SingleAsync(item => item.Id == f.Subsonic.Id)).BackendPrincipalId = "replacement"; await db.SaveChangesAsync(); }
        f.Clock.UtcNow = f.Start.AddDays(-1);
        await f.Heart(Fixture.Copy(f.Subsonic, principal: "replacement"), true, "replacement", "ext-deezer-song-New");
        f.Clock.UtcNow = f.Start;
        await f.RunUnion(linked.JobId);
        var view = await f.Reader.ReadAsync(Fixture.Context(f.Jellyfin));
        Assert.Equal("Original", Assert.Single(view).Resource.Value);
        Assert.Equal("needs_attention", (await f.Service.DetailAsync(f.Admin, linked.LinkId, default)).State);
    }

    [Fact]
    public async Task UnionUsesOnlyProvenAliasesKeepsCaseAndDoesNotCopyPartnerDisplay()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Heart(f.Jellyfin, true, "a", "ext-apple-download-song-AbC");
        await f.Heart(f.Subsonic, true, "b", "ext-applemusic-song-AbC");
        await f.Heart(f.Subsonic, true, "c", "ext-apple-musickit-song-abc");
        await f.Heart(f.Subsonic, true, "native", "native-track");
        await using (var db = f.Factory.CreateDbContext())
        {
            var state = await db.FavoriteStates.SingleAsync(item => item.OwnerUserId == f.Subsonic.Id && item.ItemId == "ext-apple-musickit-song-abc");
            state.DisplayMetadataJson = JsonSerializer.Serialize(new FavoriteDisplaySnapshot(1, state.ItemId, "song", "Private partner display", []), FavoriteDisplaySnapshot.JsonOptions);
            await db.SaveChangesAsync();
        }
        var link = await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        await f.RunUnion(link.JobId);
        var view = await f.Reader.ReadAsync(Fixture.Context(f.Jellyfin));
        Assert.Equal(2, view.Count);
        Assert.Contains(view, item => item.Resource.Value == "AbC");
        Assert.Contains(view, item => item.Resource.Value == "abc");
        Assert.DoesNotContain("Private partner display", JsonSerializer.Serialize(view));
        await using var saved = f.Factory.CreateDbContext();
        Assert.Equal(2, await saved.Set<LinkedProviderFavoriteStateRecord>().CountAsync());
        Assert.Equal(4, await saved.FavoriteEvents.CountAsync());
        Assert.Equal(1, await saved.Jobs.CountAsync(item => item.Type == FavoriteLinkUnionJobHandler.Type));
    }

    [Fact]
    public async Task UnionCheckpointsResumeUnderANewAdministratorWithoutResettingPendingTombstones()
    {
        await using var f = await Fixture.CreateAsync();
        for (var i = 0; i < 105; i++) await f.Heart(f.Jellyfin, true, i.ToString(), $"ext-deezer-song-{i}");
        var linked = await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        var claim = (await f.Jobs.ClaimNextAsync("batch", [FavoriteLinkUnionJobHandler.Type]))!;
        var handler = new FavoriteLinkUnionJobHandler(f.Factory, f.Clock);
        var first = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Deferred, first.Kind);
        await f.Jobs.CompleteAsync(claim, first);
        var detail = await f.Service.DetailAsync(f.Admin, linked.LinkId, default);
        Assert.Equal(100, detail.Union.ProcessedRows);
        Assert.Equal(100, detail.Union.SeededItems);
        Assert.Null(detail.Union.CompletedAt);
        await f.Heart(f.Subsonic, false, "pending-unheart", "ext-deezer-song-104");
        var replacementAdmin = Fixture.Copy(f.Jellyfin, Guid.NewGuid(), "new-administrator");
        await using (var db = f.Factory.CreateDbContext())
        {
            (await db.Users.SingleAsync(item => item.Id == f.Jellyfin.Id)).IsAdmin = false;
            db.Users.Add(replacementAdmin);
            await db.SaveChangesAsync();
        }
        f.Clock.UtcNow = f.Clock.UtcNow.AddMinutes(1);
        var next = (await f.Jobs.ClaimNextAsync("changed-role", [FavoriteLinkUnionJobHandler.Type]))!;
        var denied = await handler.ExecuteAsync(new(next, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Failed, denied.Kind);
        await f.Jobs.CompleteAsync(next, denied);
        var admin = Fixture.Session(replacementAdmin, true);
        var failed = await f.Service.DetailAsync(admin, linked.LinkId, default);
        Assert.True(failed.Union.CanRetry);
        Assert.Equal(100, failed.Union.ProcessedRows);
        var retry = new RetryFavoriteLinkRequest(Guid.NewGuid(), failed.Revision);
        var receipt = await f.Service.RetryAsync(admin, linked.LinkId, retry, default);
        Assert.NotEqual(linked.JobId, receipt.JobId);
        Assert.True((await f.Service.RetryAsync(admin, linked.LinkId, retry, default)).Replayed);
        Assert.Equal("favorite_link_changed", (await Assert.ThrowsAsync<FavoriteLinkException>(() =>
            f.Service.RetryAsync(admin, linked.LinkId, retry with { RequestId = Guid.NewGuid() }, default))).Code);
        Assert.Equal(DurableJobCompletionKind.Cancelled, (await handler.ExecuteAsync(new(next, EmptyServices.Instance), default)).Kind);
        await f.RunUnion(receipt.JobId);
        var complete = await f.Service.DetailAsync(admin, linked.LinkId, default);
        Assert.Equal("shared", complete.State);
        Assert.Equal(105, complete.Union.ProcessedRows);
        Assert.False(complete.Union.CanRetry);
        Assert.NotNull(complete.Union.CompletedAt);
        await using var saved = f.Factory.CreateDbContext();
        var states = await saved.Set<LinkedProviderFavoriteStateRecord>().ToArrayAsync();
        Assert.Equal(105, states.Length);
        var tombstone = Assert.Single(states, item => item.ExternalId == "104");
        Assert.False(tombstone.IsFavorite);
        Assert.All(states, item => Assert.Equal(0, item.LifecycleVersion));
        Assert.Equal(complete.Union.SeededItems, states.Count(item => item.IsFavorite));
        Assert.Equal(106, await saved.FavoriteEvents.CountAsync());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("principal")]
    [InlineData("synthetic")]
    public async Task CurrentRealPeerKeepsSharedTombstonesWhenTheOtherBindingIsUnavailable(string change)
    {
        await using var f = await Fixture.CreateAsync();
        await f.Heart(f.Subsonic, true, "before");
        var linked = await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        await f.Heart(f.Jellyfin, false, "false-zero");
        await using (var db = f.Factory.CreateDbContext())
        {
            var peer = await db.Users.SingleAsync(item => item.Id == f.Subsonic.Id);
            if (change == "disabled") peer.Enabled = false;
            if (change == "principal") peer.BackendPrincipalId = "replacement";
            if (change == "synthetic") peer.IsSynthetic = true;
            await db.SaveChangesAsync();
        }
        Assert.Empty(await f.Reader.ReadAsync(Fixture.Context(f.Jellyfin)));
        await f.RunUnion(linked.JobId);
        Assert.Empty(await f.Reader.ReadAsync(Fixture.Context(f.Jellyfin)));
        Assert.Equal("needs_attention", (await f.Service.DetailAsync(f.Admin, linked.LinkId, default)).State);
        await using var saved = f.Factory.CreateDbContext();
        var state = await saved.Set<LinkedProviderFavoriteStateRecord>().SingleAsync();
        Assert.False(state.IsFavorite);
        Assert.Equal(0, state.LifecycleVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedWorkerRechecksPeerIntentAndSyntheticTransitionsAfterItsOwnDownload(bool synthetic)
    {
        await using var f = await Fixture.CreateAsync();
        await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        var receipt = await f.Heart(f.Jellyfin, true, "download");
        var claim = (await f.Jobs.ClaimNextAsync("download", [FavoriteActionPipeline.JobType]))!;
        var calls = 0;
        var handler = new FavoriteActionJobHandler(f.Factory, [new CallbackExecutor(async () =>
        {
            calls++;
            if (synthetic)
            {
                await using var db = f.Factory.CreateDbContext();
                (await db.Users.SingleAsync(item => item.Id == f.Jellyfin.Id)).IsSynthetic = true;
                await db.SaveChangesAsync();
            }
            else await f.Heart(f.Subsonic, false, "peer-unheart");
        })], f.Clock);
        Assert.Equal(DurableJobCompletionKind.Cancelled, (await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default)).Kind);
        Assert.Equal(1, calls);
        await using var saved = f.Factory.CreateDbContext();
        Assert.DoesNotContain(await saved.FavoriteActions.Where(item => item.EventId == receipt.EventId && item.ActionType == "download").ToArrayAsync(),
            item => item.State == FavoriteActionState.Succeeded);
        Assert.Equal(synthetic, (await saved.Set<LinkedProviderFavoriteStateRecord>().SingleAsync()).IsFavorite);
    }

    [Fact]
    public async Task LegacyLinkedWorkerCannotCompleteAfterBecomingSyntheticDuringDownload()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Heart(f.Jellyfin, true, "legacy");
        await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        var claim = (await f.Jobs.ClaimNextAsync("legacy", [FavoriteActionPipeline.JobType]))!;
        var handler = new FavoriteActionJobHandler(f.Factory, [new CallbackExecutor(async () =>
        {
            await using var db = f.Factory.CreateDbContext();
            (await db.Users.SingleAsync(item => item.Id == f.Jellyfin.Id)).IsSynthetic = true;
            await db.SaveChangesAsync();
        })], f.Clock);
        Assert.Equal(DurableJobCompletionKind.Cancelled, (await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default)).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturningExactRealIdentityAcceptsANewOwnEventWithoutChangingSharedLifecycle(bool replacement)
    {
        await using var f = await Fixture.CreateAsync();
        var link = await f.Service.CreateAsync(f.Admin, f.CreateRequest(), default);
        var original = await f.Heart(f.Jellyfin, true, "unchanged");
        var peer = await f.Heart(f.Subsonic, true, "peer");
        await using (var db = f.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(item => item.Id == f.Jellyfin.Id);
            if (replacement) user.BackendPrincipalId = "temporary-replacement";
            else user.IsSynthetic = true;
            await db.SaveChangesAsync();
        }
        await f.Heart(replacement ? Fixture.Copy(f.Jellyfin, principal: "temporary-replacement") : f.Jellyfin, true, "unchanged");
        if (replacement)
        {
            await using var db = f.Factory.CreateDbContext();
            var state = await db.FavoriteStates.SingleAsync(item => item.OwnerUserId == f.Jellyfin.Id);
            state.DisplayMetadataJson = JsonSerializer.Serialize(new FavoriteDisplaySnapshot(1, state.ItemId, "song", "Replacement private title", []), FavoriteDisplaySnapshot.JsonOptions);
            await db.SaveChangesAsync();
        }
        await using (var db = f.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(item => item.Id == f.Jellyfin.Id);
            user.BackendPrincipalId = f.Jellyfin.BackendPrincipalId;
            user.IsSynthetic = false;
            await db.SaveChangesAsync();
        }
        var restored = await f.Heart(f.Jellyfin, true, "unchanged");
        Assert.True(restored.Created);
        Assert.NotEqual(original.EventId, restored.EventId);
        Assert.False((await f.Heart(f.Jellyfin, true, "unchanged")).Created);
        await using var saved = f.Factory.CreateDbContext();
        var accepted = await saved.FavoriteEvents.SingleAsync(item => item.Id == restored.EventId);
        Assert.Equal(link.LinkId, accepted.SharedLinkId);
        Assert.True(accepted.IsRealProviderIntent);
        Assert.Equal(0, accepted.SharedLifecycleVersion);
        Assert.Equal(restored.EventId, (await saved.FavoriteStates.SingleAsync(item => item.OwnerUserId == f.Jellyfin.Id)).LastEventId);
        if (replacement)
        {
            Assert.Null((await saved.FavoriteStates.SingleAsync(item => item.OwnerUserId == f.Jellyfin.Id)).DisplayMetadataJson);
            Assert.DoesNotContain("Replacement private title", JsonSerializer.Serialize(await f.Reader.ReadAsync(Fixture.Context(f.Jellyfin))));
        }
        Assert.Null((await saved.Jobs.SingleAsync(item => item.Id == peer.JobId)).CancellationRequestedAt);
        var shared = await saved.Set<LinkedProviderFavoriteStateRecord>().SingleAsync();
        Assert.Equal(0, shared.LifecycleVersion);
        Assert.Equal(f.Start, shared.EffectiveAt);
    }

    internal sealed class Fixture(SqliteTestDatabase database) : IAsyncDisposable
    {
        public Factory Factory { get; } = new(database.Options);
        public Clock Clock { get; } = new();
        public DateTimeOffset Start => new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public UserRecord Jellyfin { get; private set; } = null!;
        public UserRecord Subsonic { get; private set; } = null!;
        public AdminAuthSession Admin => Session(Jellyfin, true);
        public IConfigurationRoot Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:Type"] = "Jellyfin" }).Build();
        public DurableJobQueue Jobs => new(Factory, new(), new JobPayloadPolicy(new()), Clock, new DurableJobContextAuthorizer(Factory));
        public FavoriteLinkService Service => new(Factory, Jobs, Clock, Configuration);
        public FavoriteActionPipeline Pipeline => new(Factory, Jobs, Clock);
        public FavoriteViewReader Reader => new(Factory);
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(await SqliteTestDatabase.CreateAsync());
            f.Jellyfin = new UserRecord { Id = Guid.NewGuid(), BackendType = "jellyfin", BackendInstanceId = "home", BackendPrincipalId = "private-jellyfin-principal", DisplayName = "Same name", Enabled = true, IsAdmin = true, LastSeenAt = f.Start, CreatedAt = f.Start, UpdatedAt = f.Start };
            f.Subsonic = Copy(f.Jellyfin, Guid.NewGuid(), "private-subsonic-principal", "subsonic", false);
            await using var db = f.Factory.CreateDbContext();
            db.Users.AddRange(f.Jellyfin, f.Subsonic);
            await db.SaveChangesAsync();
            return f;
        }
        public CreateFavoriteLinkRequest CreateRequest() => new(Guid.NewGuid(), Jellyfin.Id, FavoriteLinkScope.Fingerprint(Jellyfin), Subsonic.Id, FavoriteLinkScope.Fingerprint(Subsonic), true, 1);
        internal static UserRecord Copy(UserRecord user, Guid? id = null, string? principal = null, string? protocol = null, bool? admin = null) => new()
        {
            Id = id ?? user.Id,
            BackendType = protocol ?? user.BackendType,
            BackendInstanceId = user.BackendInstanceId,
            BackendPrincipalId = principal ?? user.BackendPrincipalId,
            DisplayName = user.DisplayName,
            IsAdmin = admin ?? user.IsAdmin,
            Enabled = user.Enabled,
            IsSynthetic = user.IsSynthetic,
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt,
            LastSeenAt = user.LastSeenAt
        };
        public Task<FavoriteEventReceipt> Heart(UserRecord user, bool heart, string revision, string item = "ext-deezer-song-42") => Pipeline.RecordAsync(new(Context(user), item, heart ? FavoriteOperation.Favorite : FavoriteOperation.Unfavorite, revision));
        public async Task RunUnion(Guid jobId)
        {
            for (var i = 0; i < 20; i++)
            {
                var claim = await Jobs.ClaimNextAsync("union", [FavoriteLinkUnionJobHandler.Type]);
                Assert.NotNull(claim);
                Assert.Equal(jobId, claim.JobId);
                var result = await new FavoriteLinkUnionJobHandler(Factory, Clock).ExecuteAsync(new(claim, EmptyServices.Instance), default);
                await Jobs.CompleteAsync(claim, result);
                if (result.Kind == DurableJobCompletionKind.Succeeded) return;
                Assert.Equal(DurableJobCompletionKind.Deferred, result.Kind);
                Clock.UtcNow = Clock.UtcNow.AddMinutes(1);
            }
            Assert.Fail("Union did not finish within its bounded fixture.");
        }
        public static ProtocolExecutionContext Context(UserRecord user) => new(user.BackendType == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic,
            user.BackendInstanceId, user.BackendPrincipalId, new(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin), "fixture", DateTimeOffset.UtcNow.AddHours(1), default);
        public static AdminAuthSession Session(UserRecord user, bool admin) => new() { SessionId = "fixture", JellyfinAccessToken = "fixture", UserId = user.BackendPrincipalId, UserName = user.DisplayName, AllstarrUserId = user.Id, BackendType = user.BackendType, BackendInstanceId = user.BackendInstanceId, IsAdministrator = admin, ExpiresAtUtc = new DateTime(2030, 1, 1) };
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
    internal sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    internal sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero); }
    private sealed class Executor(List<Guid> calls) : IFavoriteActionExecutor
    {
        public string ActionType => "download";
        public Task<FavoriteActionExecutionResult> ExecuteAsync(FavoriteEventRecord favoriteEvent, FavoriteActionRecord action, int executionAttempt, CancellationToken cancellationToken)
        { calls.Add(favoriteEvent.OwnerUserId); return Task.FromResult(FavoriteActionExecutionResult.Success()); }
    }
    private sealed class CallbackExecutor(Func<Task> callback) : IFavoriteActionExecutor
    {
        public string ActionType => "download";
        public async Task<FavoriteActionExecutionResult> ExecuteAsync(FavoriteEventRecord favoriteEvent, FavoriteActionRecord action, int executionAttempt, CancellationToken cancellationToken)
        { await callback(); return FavoriteActionExecutionResult.Success(); }
    }
    private sealed class EmptyServices : IServiceProvider { public static readonly EmptyServices Instance = new(); public object? GetService(Type serviceType) => null; }
}
