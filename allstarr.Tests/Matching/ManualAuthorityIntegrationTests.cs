using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ManualAuthorityIntegrationTests
{
    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task Layers_FollowSourceAcrossOwnersAndSnapshotVersions_WithoutChangingCatalog()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Resolve(fixture.Admin, fixture.SourceA, "household", "household-track")).Succeeded);
        Assert.True((await fixture.Resolve(fixture.Listener, fixture.SourceB, "personal", "personal-track")).Succeeded);
        var admin = await fixture.Read(fixture.Admin, fixture.SourceA);
        var listener = await fixture.Read(fixture.Listener, fixture.SourceB);
        Assert.Null(admin.Personal);
        Assert.Equal("household-track", admin.Effective!.TargetExternalId);
        Assert.Equal("personal-track", listener.Effective!.TargetExternalId);
        Assert.Equal(admin.Household!.Id, listener.Household!.Id);

        var newer = await fixture.AddSnapshot(fixture.Listener.UserId, 2);
        var refreshed = await fixture.Read(fixture.Listener, newer);
        Assert.Equal(listener.Personal!.Id, refreshed.Personal!.Id);
        Assert.Equal(listener.Household.Id, refreshed.Household!.Id);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(0, await db.CanonicalRecordings.CountAsync());
        Assert.Equal(0, await db.ProviderTrackIdentities.CountAsync());
        Assert.Equal(2, await db.ManualTrackOverrides.CountAsync());
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task Clear_RevealsNextLayer_UsesExactScopeAndRevision_AndPreservesHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Resolve(fixture.Admin, fixture.SourceA, "household", "household-track");
        await fixture.Resolve(fixture.Listener, fixture.SourceB, "personal", "personal-track");
        var layers = await fixture.Read(fixture.Listener, fixture.SourceB);
        var personal = layers.Personal!;
        var household = layers.Household!;
        Assert.Equal(TrackMatchCommandFailure.Conflict, (await fixture.Service.ClearManualAuthorityAsync(
            fixture.Listener, fixture.SourceB, ManualTrackAuthorityKind.ProviderMatch, personal.Id,
            personal.Revision + 1, "stale")).Failure);
        Assert.True((await fixture.Service.ClearManualAuthorityAsync(fixture.Listener, fixture.SourceB,
            ManualTrackAuthorityKind.ProviderMatch, personal.Id, personal.Revision, "clear")).Succeeded);
        Assert.Equal(household.Id, (await fixture.Read(fixture.Listener, fixture.SourceB)).Effective!.Id);
        Assert.Equal(TrackMatchCommandFailure.Conflict, (await fixture.Service.ClearManualAuthorityAsync(
            fixture.Listener, fixture.SourceB, ManualTrackAuthorityKind.ProviderMatch, personal.Id,
            personal.Revision, "repeat")).Failure);
        // The administrator can clear household authority through a newer, different owner's source snapshot.
        Assert.True((await fixture.Service.ClearManualAuthorityAsync(fixture.Admin, fixture.SourceB,
            ManualTrackAuthorityKind.ProviderMatch, household.Id, household.Revision, "clear-household",
            authorityScope: "household")).Succeeded);
        Assert.Null((await fixture.Read(fixture.Listener, fixture.SourceB)).Effective);
        await using var db = fixture.Factory.CreateDbContext();
        var history = await db.ManualTrackOverrides.ToArrayAsync();
        Assert.Equal(2, history.Length);
        Assert.All(history, item => { Assert.NotNull(item.RevokedAt); Assert.Equal(1, item.Revision); });
        Assert.Equal(2, await db.AuditEvents.CountAsync(item => item.Action == "manual-authority.delete"));
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task Mutations_DenyOtherPersonalLayersAndListenerHouseholdWrites()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(TrackMatchCommandFailure.Forbidden,
            (await fixture.Resolve(fixture.Listener, fixture.SourceB, "household", "denied")).Failure);
        Assert.Equal(TrackMatchCommandFailure.Forbidden,
            (await fixture.Resolve(fixture.Listener, fixture.SourceA, "personal", "denied")).Failure);
        await fixture.Resolve(fixture.Listener, fixture.SourceB, "personal", "listener-track");
        var personal = (await fixture.Read(fixture.Listener, fixture.SourceB)).Personal!;
        Assert.Equal(TrackMatchCommandFailure.NotFound, (await fixture.Service.ClearManualAuthorityAsync(
            fixture.Admin, fixture.SourceB, ManualTrackAuthorityKind.ProviderMatch, personal.Id,
            personal.Revision, "admin-cannot-edit-personal")).Failure);
        await fixture.Resolve(fixture.Admin, fixture.SourceA, "household", "shared-track");
        var household = (await fixture.Read(fixture.Listener, fixture.SourceB)).Household!;
        Assert.Equal(TrackMatchCommandFailure.Forbidden, (await fixture.Service.ClearManualAuthorityAsync(
            fixture.Listener, fixture.SourceB, ManualTrackAuthorityKind.ProviderMatch, household.Id,
            household.Revision, "listener-cannot-edit-household", authorityScope: "household")).Failure);
        Assert.True((await fixture.Resolve(fixture.Admin, fixture.SourceB, "personal", "admin-only")).Succeeded);
        Assert.Equal("listener-track", (await fixture.Read(fixture.Listener, fixture.SourceB)).Personal!.TargetExternalId);
        Assert.Equal("admin-only", (await fixture.Read(fixture.Admin, fixture.SourceA)).Personal!.TargetExternalId);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task Replace_UsesSelectedLayerRevision_AndInvalidOrStaleRequestsAreAtomic()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Resolve(fixture.Admin, fixture.SourceA, "household", "shared-first");
        await fixture.Resolve(fixture.Admin, fixture.SourceA, "personal", "personal-first");
        var layers = await fixture.Read(fixture.Admin, fixture.SourceA);
        Assert.Equal(TrackMatchCommandFailure.Conflict, (await fixture.Resolve(fixture.Admin, fixture.SourceA,
            "household", "wrong-revision", Revision(layers.Personal!))).Failure);
        Assert.Equal(TrackMatchCommandFailure.Conflict, (await fixture.Resolve(fixture.Admin, fixture.SourceA,
            "household", "missing-revision")).Failure);
        Assert.True((await fixture.Resolve(fixture.Admin, fixture.SourceA, "household", "shared-next",
            Revision(layers.Household!))).Succeeded);
        Assert.Equal(TrackMatchCommandFailure.Conflict, (await fixture.Resolve(fixture.Admin, fixture.SourceA,
            "household", "stale", Revision(layers.Household!))).Failure);
        var invalid = await fixture.Service.ResolveSnapshotAsync(fixture.Admin, fixture.SourceA,
            new("local", LibraryTrackId: Guid.CreateVersion7(), AuthorityScope: "personal",
                ExpectedAuthority: Revision(layers.Personal!)), "invalid-target");
        Assert.Equal(TrackMatchCommandFailure.NotFound, invalid.Failure);
        var invalidProvider = await fixture.Resolve(fixture.Admin, fixture.SourceA, "personal", "ext-tidal-unplayable",
            Revision(layers.Personal!));
        Assert.Equal(TrackMatchCommandFailure.Invalid, invalidProvider.Failure);
        var next = await fixture.Read(fixture.Admin, fixture.SourceA);
        Assert.Equal(layers.Personal!.Id, next.Personal!.Id);
        Assert.Equal("shared-next", next.Household!.TargetExternalId);
        Assert.Equal(2, next.Household.DecisionVersion);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task ConcurrentCreates_LeaveExactlyOneActivePersonalChoice()
    {
        await using var fixture = await Fixture.CreateAsync();
        var results = await Task.WhenAll(
            fixture.Resolve(fixture.Listener, fixture.SourceB, "personal", "first"),
            fixture.Resolve(fixture.Listener, fixture.SourceB, "personal", "second"));
        Assert.Single(results, item => item.Succeeded);
        Assert.Single(results, item => item.Failure == TrackMatchCommandFailure.Conflict);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.ManualTrackOverrides.Where(item => item.RevokedAt == null).ToArrayAsync());
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task OrdinaryRematchAndRestart_PreserveBothLayers()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Resolve(fixture.Admin, fixture.SourceA, "household", "shared");
        await fixture.Resolve(fixture.Listener, fixture.SourceB, "personal", "personal");
        var before = await fixture.Read(fixture.Listener, fixture.SourceB);
        Assert.True((await fixture.Service.RematchSnapshotAsync(fixture.Listener, fixture.SourceB, "rematch")).Succeeded);
        fixture.Service = fixture.NewService();
        var after = await fixture.Read(fixture.Listener, fixture.SourceB);
        Assert.Equal(before.Personal!.Id, after.Personal!.Id);
        Assert.Equal(before.Household!.Id, after.Household!.Id);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task ClearAndRematch_PreservesRemainingHouseholdChoice()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Resolve(fixture.Admin, fixture.SourceA, "household", "shared");
        await fixture.Resolve(fixture.Listener, fixture.SourceB, "personal", "personal");
        var before = await fixture.Read(fixture.Listener, fixture.SourceB);
        var actor = fixture.Listener;
        var principal = actor.UserId.ToString("N");
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", principal,
            new AllstarrPrincipal(actor.TenantId, actor.UserId, "jellyfin", "backend", principal, "Listener", false),
            "clear-rematch", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var result = await fixture.Service.RematchManualAuthorityAsync(context, fixture.SourceB,
            ManualTrackAuthorityKind.ProviderMatch, before.Personal!.Id, before.Personal.Revision, "clear-rematch");
        Assert.True(result.Succeeded);
        var after = await fixture.Read(fixture.Listener, fixture.SourceB);
        Assert.Null(after.Personal);
        Assert.Equal(before.Household!.Id, after.Household!.Id);
    }

    private static ManualAuthorityRevision Revision(ManualTrackOverrideRecord record) => new(record.Id, record.Revision);

    private sealed class Fixture(SqliteTestDatabase database) : IAsyncDisposable
    {
        public DbFactory Factory { get; } = new(database.Options);
        public TrackMatchActor Admin { get; private set; } = null!;
        public TrackMatchActor Listener { get; private set; } = null!;
        public Guid SourceA { get; private set; }
        public Guid SourceB { get; private set; }
        public TrackMatchCommandService Service { get; set; } = null!;
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly Dictionary<Guid, Guid> _accounts = [];

        public static async Task<Fixture> CreateAsync()
        {
            var result = new Fixture(await SqliteTestDatabase.CreateAsync());
            var tenant = Guid.CreateVersion7();
            result.Admin = new(tenant, Guid.CreateVersion7(), true);
            result.Listener = new(tenant, Guid.CreateVersion7(), false);
            await using var db = result.Factory.CreateDbContext();
            db.Tenants.Add(new TenantRecord { Id = tenant, Slug = "household", Name = "Household", CreatedAt = result._now });
            foreach (var actor in new[] { result.Admin, result.Listener })
            {
                db.Users.Add(new PlatformUserRecord
                {
                    Id = actor.UserId,
                    TenantId = tenant,
                    DisplayName = "Listener",
                    Status = PlatformUserStatus.Active,
                    CreatedAt = result._now,
                    UpdatedAt = result._now
                });
                var accountId = Guid.CreateVersion7();
                result._accounts[actor.UserId] = accountId;
                db.ProviderAccounts.Add(new ProviderAccountRecord
                {
                    Id = accountId,
                    TenantId = tenant,
                    OwnerUserId = actor.UserId,
                    ProviderId = "spotify",
                    DisplayName = "Source",
                    Enabled = true,
                    CreatedAt = result._now,
                    UpdatedAt = result._now
                });
            }
            await db.SaveChangesAsync();
            result.SourceA = await result.AddSnapshot(result.Admin.UserId, 1);
            result.SourceB = await result.AddSnapshot(result.Listener.UserId, 1);
            result.Service = result.NewService();
            return result;
        }

        public TrackMatchCommandService NewService() => new(Factory, new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(Factory), new Clock(_now), new TestBackendLibraryAccess(Factory, "music"));

        public async Task<Guid> AddSnapshot(Guid ownerId, int version)
        {
            await using var db = Factory.CreateDbContext();
            var snapshot = new ExternalMetadataSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = Admin.TenantId,
                OwnerUserId = ownerId,
                ProviderAccountId = _accounts[ownerId],
                ProviderId = "spotify",
                LibraryScopeId = "music",
                BackendInstanceId = "backend",
                BackendPrincipalId = ownerId.ToString("N"),
                Protocol = "jellyfin",
                ResourceKind = "track",
                ExternalIdHash = Hash("source"),
                SnapshotVersion = version,
                ProviderRevision = version.ToString(),
                PayloadJson = "{\"title\":\"Track\",\"artist\":\"Artist\"}",
                PayloadSha256 = Hash("payload"),
                CorrelationId = "fixture",
                RetrievedAt = _now.AddSeconds(version)
            };
            db.ExternalMetadataSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
            return snapshot.Id;
        }

        public Task<TrackMatchCommandResult> Resolve(TrackMatchActor actor, Guid snapshot, string scope,
            string target, ManualAuthorityRevision? expected = null) => Service.ResolveSnapshotAsync(actor, snapshot,
            new("provider", ExternalProvider: "deezer", ExternalId: target, AuthorityScope: scope, ExpectedAuthority: expected), "test");

        public async Task<ManualTrackOverrideLayers> Read(TrackMatchActor actor, Guid snapshot)
        {
            var data = await Service.GetReviewDataAsync(actor, externalSnapshotId: snapshot);
            Assert.Single(data.Snapshots);
            return new(data.ActiveOverrides.SingleOrDefault(item => item.OwnerUserId == actor.UserId),
                data.ActiveOverrides.SingleOrDefault(item => item.OwnerUserId == null));
        }

        public ValueTask DisposeAsync() => database.DisposeAsync();
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class Clock(DateTimeOffset now) : IPlatformClock { public DateTimeOffset UtcNow => now; }
}
