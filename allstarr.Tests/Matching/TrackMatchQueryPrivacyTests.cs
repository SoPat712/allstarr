using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class TrackMatchQueryPrivacyTests
{
    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task Detail_HidesForeignAccountIdentitiesBeforeCanonicalAndLibraryExpansion()
    {
        await using var fixture = await Fixture.CreateAsync();

        var detail = await fixture.Service.GetDetailAsync(
            new(fixture.Owner, false), "spotify", fixture.SharedExternalId);

        var identities = detail.ProviderIdentities.ToArray();
        Assert.Contains(identities, item => item.ExternalId == fixture.SharedExternalId && item.ProviderAccountId == fixture.OwnerAccountId);
        Assert.Contains(identities, item => item.ExternalId == fixture.SharedExternalId && item.ProviderAccountId == fixture.SharedAccountId);
        Assert.Contains(identities, item => item.ExternalId == fixture.PublicAlias);
        Assert.Contains(identities, item => item.ExternalId == fixture.OwnAlias);
        Assert.DoesNotContain(identities, item => item.ProviderAccountId == fixture.ForeignAccountId);
        Assert.DoesNotContain(identities, item => item.ExternalId == fixture.ForeignAlias);
        Assert.DoesNotContain(identities, item => item.ExternalId == fixture.DisabledAlias);
        Assert.DoesNotContain(identities, item => item.ExternalId == fixture.RemovedAlias);
        Assert.DoesNotContain(identities, item => item.CanonicalRecordingId == fixture.ForeignRecording);
        Assert.Contains(detail.LocalTracks, item => item.Id == fixture.OwnLocalTrack);
        Assert.DoesNotContain(detail.LocalTracks, item => item.Id == fixture.ForeignDerivedLocalTrack);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task ReviewData_HidesForeignAccountIdentitiesBeforeCanonicalAndLibraryExpansion()
    {
        await using var fixture = await Fixture.CreateAsync();

        var review = await fixture.Service.GetReviewDataAsync(new(fixture.Owner, false));

        var identities = review.ProviderIdentities.ToArray();
        Assert.Contains(identities, item => item.ExternalId == fixture.SharedExternalId && item.ProviderAccountId == fixture.OwnerAccountId);
        Assert.Contains(identities, item => item.ExternalId == fixture.SharedExternalId && item.ProviderAccountId == fixture.SharedAccountId);
        Assert.Contains(identities, item => item.ExternalId == fixture.PublicAlias);
        Assert.Contains(identities, item => item.ExternalId == fixture.OwnAlias);
        Assert.DoesNotContain(identities, item => item.ProviderAccountId == fixture.ForeignAccountId);
        Assert.DoesNotContain(identities, item => item.ExternalId == fixture.ForeignAlias);
        Assert.DoesNotContain(identities, item => item.ExternalId == fixture.DisabledAlias);
        Assert.DoesNotContain(identities, item => item.ExternalId == fixture.RemovedAlias);
        Assert.DoesNotContain(identities, item => item.CanonicalRecordingId == fixture.ForeignRecording);
        Assert.Contains(review.LibraryTracks, item => item.Id == fixture.OwnLocalTrack);
        Assert.DoesNotContain(review.LibraryTracks, item => item.Id == fixture.ForeignDerivedLocalTrack);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task AdministratorDetailRetainsCrossAccountIdentityDiagnostics()
    {
        await using var fixture = await Fixture.CreateAsync();

        var detail = await fixture.Service.GetDetailAsync(
            new(fixture.Administrator, true), "spotify", fixture.SharedExternalId);

        Assert.Contains(detail.ProviderIdentities, item => item.ProviderAccountId == fixture.ForeignAccountId);
        Assert.Contains(detail.ProviderIdentities, item => item.ExternalId == fixture.ForeignAlias);
        Assert.Contains(detail.ProviderIdentities, item => item.ExternalId == fixture.DisabledAlias);
        Assert.Contains(detail.ProviderIdentities, item => item.ExternalId == fixture.RemovedAlias);
        Assert.Contains(detail.ProviderIdentities, item => item.CanonicalRecordingId == fixture.ForeignRecording);
        Assert.Contains(detail.LocalTracks, item => item.Id == fixture.ForeignDerivedLocalTrack);
    }

    [Theory]
    [InlineData("revoked-credential")]
    [InlineData("disabled-account")]
    [Trait("Category", "Sqlite")]
    public async Task ListenerQueriesHideAccountIdentitiesAfterRevocationOrDisable(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var account = await db.ProviderAccounts.SingleAsync(item => item.Id == fixture.OwnerAccountId);
            if (change == "disabled-account")
                account.Enabled = false;
            else
            {
                var secretId = Guid.CreateVersion7();
                db.SecretReferences.Add(new()
                {
                    Id = secretId,
                    UserId = fixture.Owner,
                    Purpose = "fixture-provider-account",
                    ActiveVersion = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    RevokedAt = DateTimeOffset.UtcNow
                });
                account.SecretReferenceId = secretId;
            }
            await db.SaveChangesAsync();
        }

        var detail = await fixture.Service.GetDetailAsync(new(fixture.Owner, false), "spotify", fixture.SharedExternalId);
        var review = await fixture.Service.GetReviewDataAsync(new(fixture.Owner, false));
        foreach (var identities in new[] { detail.ProviderIdentities, review.ProviderIdentities })
        {
            Assert.DoesNotContain(identities, item => item.ProviderAccountId == fixture.OwnerAccountId);
            Assert.DoesNotContain(identities, item => item.ProviderAccountId == fixture.ForeignAccountId);
            Assert.Contains(identities, item => item.ExternalId == fixture.PublicAlias);
            Assert.Contains(identities, item => item.ProviderAccountId == fixture.SharedAccountId);
        }
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task AdministratorReviewRetainsCrossAccountIdentityDiagnostics()
    {
        await using var fixture = await Fixture.CreateAsync();
        var review = await fixture.Service.GetReviewDataAsync(new(fixture.Administrator, true));
        Assert.Contains(review.ProviderIdentities, item => item.ProviderAccountId == fixture.ForeignAccountId);
        Assert.Contains(review.ProviderIdentities, item => item.ExternalId == fixture.DisabledAlias);
        Assert.Contains(review.ProviderIdentities, item => item.ExternalId == fixture.RemovedAlias);
        Assert.Contains(review.LibraryTracks, item => item.Id == fixture.ForeignDerivedLocalTrack);
    }

    private sealed class Fixture(SqliteTestDatabase database) : IAsyncDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 25, 15, 0, 0, TimeSpan.Zero);

        public DbFactory Factory { get; } = new(database.Options);
        public TrackMatchCommandService Service { get; private set; } = null!;
        public Guid Owner { get; } = Guid.CreateVersion7();
        public Guid Administrator { get; } = Guid.CreateVersion7();
        public Guid ForeignUser { get; } = Guid.CreateVersion7();
        public Guid OwnerAccountId { get; } = Guid.CreateVersion7();
        public Guid ForeignAccountId { get; } = Guid.CreateVersion7();
        public Guid SharedAccountId { get; } = Guid.CreateVersion7();
        private Guid DisabledAccountId { get; } = Guid.CreateVersion7();
        private Guid RemovedAccountId { get; } = Guid.CreateVersion7();
        public Guid OwnerRecording { get; } = Guid.CreateVersion7();
        public Guid ForeignRecording { get; } = Guid.CreateVersion7();
        public Guid OwnLocalTrack { get; } = Guid.CreateVersion7();
        public Guid ForeignDerivedLocalTrack { get; } = Guid.CreateVersion7();
        public string SharedExternalId { get; } = "same-external-id";
        public string PublicAlias { get; } = "public-alias";
        public string OwnAlias { get; } = "own-alias";
        public string ForeignAlias { get; } = "foreign-alias";
        public string DisabledAlias { get; } = "disabled-alias";
        public string RemovedAlias { get; } = "removed-alias";

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await SqliteTestDatabase.CreateAsync());
            await using (var db = fixture.Factory.CreateDbContext())
            {
                db.Users.AddRange(
                    User(fixture.Owner, "owner", false),
                    User(fixture.Administrator, "administrator", true),
                    User(fixture.ForeignUser, "foreign", false));
                db.ProviderAccounts.AddRange(
                    Account(fixture.OwnerAccountId, fixture.Owner, enabled: true),
                    Account(fixture.ForeignAccountId, fixture.ForeignUser, enabled: true),
                    Account(fixture.SharedAccountId, null, enabled: true),
                    Account(fixture.DisabledAccountId, fixture.Owner, enabled: false),
                    Account(fixture.RemovedAccountId, null, enabled: true, settings: """{"__removed":true}"""));
                db.CanonicalRecordings.AddRange(
                    Recording(fixture.OwnerRecording, fixture.Owner),
                    Recording(fixture.ForeignRecording, fixture.ForeignUser));
                var ownerSource = Identity(fixture.OwnerRecording, fixture.OwnerAccountId, fixture.SharedExternalId);
                db.ProviderTrackIdentities.AddRange(
                    ownerSource,
                    Identity(fixture.ForeignRecording, fixture.ForeignAccountId, fixture.SharedExternalId),
                    Identity(fixture.OwnerRecording, fixture.SharedAccountId, fixture.SharedExternalId),
                    Identity(fixture.OwnerRecording, null, fixture.PublicAlias),
                    Identity(fixture.OwnerRecording, fixture.OwnerAccountId, fixture.OwnAlias),
                    Identity(fixture.OwnerRecording, fixture.ForeignAccountId, fixture.ForeignAlias),
                    Identity(fixture.OwnerRecording, fixture.DisabledAccountId, fixture.DisabledAlias),
                    Identity(fixture.OwnerRecording, fixture.RemovedAccountId, fixture.RemovedAlias));
                db.LibraryTracks.AddRange(
                    LibraryTrack(fixture.OwnLocalTrack, fixture.Owner, fixture.OwnerRecording, "own-local"),
                    LibraryTrack(fixture.ForeignDerivedLocalTrack, fixture.Owner, fixture.ForeignRecording, "foreign-derived-local"));
                db.ExternalMetadataSnapshots.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = fixture.Owner,
                    ProviderAccountId = fixture.OwnerAccountId,
                    ProviderTrackIdentityId = ownerSource.Id,
                    BackendInstanceId = "backend",
                    BackendPrincipalId = "owner",
                    Protocol = "jellyfin",
                    ProviderId = "spotify",
                    ResourceKind = "track",
                    ExternalIdHash = Hash(fixture.SharedExternalId),
                    SnapshotVersion = 1,
                    ProviderRevision = "fixture",
                    PayloadJson = "{\"title\":\"Fixture\",\"artist\":\"Artist\"}",
                    PayloadSha256 = Hash("{\"title\":\"Fixture\",\"artist\":\"Artist\"}"),
                    CorrelationId = "fixture",
                    RetrievedAt = Now
                });
                await db.SaveChangesAsync();
            }

            fixture.Service = new(
                fixture.Factory,
                new TrackMatchDecisionEngine(),
                new ProviderAccountResolver(fixture.Factory),
                new Clock(),
                new TestBackendLibraryAccess(fixture.Factory, "music"));
            return fixture;
        }

        public ValueTask DisposeAsync() => database.DisposeAsync();

        private static UserRecord User(Guid id, string principal, bool admin) => new()
        {
            Id = id,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = principal,
            DisplayName = principal,
            IsAdmin = admin,
            Enabled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
            LastSeenAt = Now
        };

        private static ProviderAccountRecord Account(Guid id, Guid? owner, bool enabled, string settings = "{}") => new()
        {
            Id = id,
            OwnerUserId = owner,
            ProviderId = "spotify",
            DisplayName = id.ToString("N"),
            Enabled = enabled,
            SettingsJson = settings,
            CreatedAt = Now,
            UpdatedAt = Now,
            Revision = 1
        };

        private static CanonicalRecordingRecord Recording(Guid id, Guid owner) => new()
        {
            Id = id,
            CreatedByUserId = owner,
            Title = id.ToString("N"),
            PublicId = CanonicalRecordingRecord.DefaultPublicId(id),
            CreatedAt = Now,
            UpdatedAt = Now,
            Revision = 1
        };

        private static ProviderTrackIdentityRecord Identity(Guid recording, Guid? account, string externalId) => new()
        {
            Id = Guid.CreateVersion7(),
            CanonicalRecordingId = recording,
            ProviderAccountId = account,
            ProviderId = "spotify",
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = account.HasValue ? ProviderIdentityScope.Account : ProviderIdentityScope.Catalog,
            ExternalId = externalId,
            ExternalIdHash = Hash(externalId),
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "fixture",
            DecisionVersion = 1,
            VerifiedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now,
            Revision = 1
        };

        private static LibraryTrackRecord LibraryTrack(Guid id, Guid owner, Guid recording, string backendItemId) => new()
        {
            Id = id,
            OwnerUserId = owner,
            CanonicalRecordingId = recording,
            BackendLibraryId = "music",
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            BackendItemId = backendItemId,
            FilePath = $"/music/{backendItemId}.flac",
            Title = backendItemId,
            Artist = "Artist",
            ProviderIdsJson = "{}",
            IndexedAt = Now,
            SourceModifiedAt = Now,
            UpdatedAt = Now,
            Revision = 1
        };

        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 25, 15, 0, 0, TimeSpan.Zero);
    }
}
