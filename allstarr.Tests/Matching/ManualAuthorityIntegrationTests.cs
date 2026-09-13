using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ManualAuthorityIntegrationTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ClearManualAuthority_UsesExactRevisionAndPreservesHistory()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var canonicalId = Guid.CreateVersion7();
        var sourceCanonicalId = Guid.CreateVersion7();
        var sourceIdentityId = Guid.CreateVersion7();
        var providerAuthorityId = Guid.CreateVersion7();
        var unrelatedIdentityId = Guid.CreateVersion7();
        var snapshotId = Guid.CreateVersion7();
        var rejectionId = Guid.CreateVersion7();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = $"manual-{tenantId:N}",
                Name = "Manual authority tenant",
                CreatedAt = now
            });
            db.Users.Add(new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "Owner",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = accountId,
                TenantId = tenantId,
                OwnerUserId = userId,
                ProviderId = "spotify",
                DisplayName = "Spotify",
                Scope = ProviderAccountScope.User,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.CanonicalRecordings.AddRange(
                new CanonicalRecordingRecord
                {
                    Id = canonicalId,
                    TenantId = tenantId,
                    CreatedByUserId = userId,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new CanonicalRecordingRecord
                {
                    Id = sourceCanonicalId,
                    TenantId = tenantId,
                    CreatedByUserId = userId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            db.ProviderTrackIdentities.AddRange(
                Identity(sourceIdentityId, tenantId, sourceCanonicalId, "spotify", Hash("source"), now,
                    ProviderIdentityVerification.Verified, "source-snapshot", accountId,
                    ProviderIdentityScope.Account),
                Identity(providerAuthorityId, tenantId, canonicalId, "qobuz", Hash("authority"), now,
                    ProviderIdentityVerification.Pinned,
                    ManualTrackAuthorityPolicy.ProviderVerificationMethod),
                Identity(unrelatedIdentityId, tenantId, canonicalId, "deezer", Hash("unrelated"), now,
                    ProviderIdentityVerification.Verified, "automatic-match"));
            db.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
            {
                Id = snapshotId,
                TenantId = tenantId,
                OwnerUserId = userId,
                ProviderAccountId = accountId,
                ProviderTrackIdentityId = sourceIdentityId,
                LibraryScopeId = "music",
                BackendInstanceId = "backend",
                BackendPrincipalId = "principal",
                Protocol = "jellyfin",
                ProviderId = "spotify",
                ResourceKind = "track",
                ExternalIdHash = Hash("source"),
                SnapshotVersion = 1,
                ProviderRevision = "1",
                PayloadJson = "{}",
                PayloadSha256 = Hash("payload"),
                CorrelationId = "setup",
                RetrievedAt = now
            });
            db.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
            {
                Id = rejectionId,
                TenantId = tenantId,
                OwnerUserId = userId,
                ExternalSnapshotId = snapshotId,
                LibraryScopeId = "music",
                Decision = ManualOverrideDecision.Reject,
                Reason = "Wrong candidate",
                DecisionVersion = 1,
                MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion,
                CreatedAt = now
            });
            db.TrackMatches.Add(new TrackMatchRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OwnerUserId = userId,
                ExternalSnapshotId = snapshotId,
                CanonicalRecordingId = canonicalId,
                LibraryScopeId = "music",
                State = TrackMatchState.Accepted,
                Confidence = 1,
                Threshold = .88,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion,
                PolicyVersion = "test",
                CorrelationId = "setup",
                DecidedAt = now
            });
            await db.SaveChangesAsync();
        }

        var service = new TrackMatchCommandService(
            factory,
            new TrackMatchDecisionEngine(),
            new ProviderAccountResolver(factory, new ProviderPolicyOptions()),
            new Clock(now.AddMinutes(1)));
        var actor = new TrackMatchActor(tenantId, userId, true);

        var rejection = await service.ClearManualAuthorityAsync(
            actor,
            snapshotId,
            ManualTrackAuthorityKind.Rejection,
            rejectionId,
            0,
            "clear-rejection");
        var stale = await service.ClearManualAuthorityAsync(
            actor,
            snapshotId,
            ManualTrackAuthorityKind.Rejection,
            rejectionId,
            0,
            "stale-rejection");
        var provider = await service.ClearManualAuthorityAsync(
            actor,
            snapshotId,
            ManualTrackAuthorityKind.ProviderMatch,
            providerAuthorityId,
            0,
            "clear-provider");

        Assert.True(rejection.Succeeded);
        Assert.Equal(TrackMatchCommandFailure.Conflict, stale.Failure);
        Assert.True(provider.Succeeded);
        Assert.Equal(providerAuthorityId, provider.ReleasedProviderIdentityId);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.NotNull((await verify.ManualTrackOverrides.SingleAsync()).RevokedAt);
        var released = await verify.ProviderTrackIdentities.SingleAsync(item => item.Id == providerAuthorityId);
        Assert.Equal(ProviderIdentityVerification.Verified, released.Verification);
        Assert.Equal(ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod, released.VerificationMethod);
        Assert.Equal(1, released.Revision);
        Assert.Equal("automatic-match", (await verify.ProviderTrackIdentities
            .SingleAsync(item => item.Id == unrelatedIdentityId)).VerificationMethod);
        Assert.Equal(2, await verify.AuditEvents.CountAsync(item =>
            item.Category == "track-match" && item.Action == "manual-authority.delete"));
    }

    private static ProviderTrackIdentityRecord Identity(
        Guid id,
        Guid tenantId,
        Guid canonicalId,
        string providerId,
        string hash,
        DateTimeOffset now,
        ProviderIdentityVerification verification,
        string method,
        Guid? accountId = null,
        ProviderIdentityScope scope = ProviderIdentityScope.Catalog) => new()
        {
            Id = id,
            TenantId = tenantId,
            CanonicalRecordingId = canonicalId,
            ProviderAccountId = accountId,
            ProviderId = providerId,
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = scope,
            ExternalId = $"{providerId}-track",
            ExternalIdHash = hash,
            Verification = verification,
            VerificationMethod = method,
            DecisionVersion = 1,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class Clock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
