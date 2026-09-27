using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class SourceIdentityReconciliationTests
{
    [Theory]
    [InlineData("provisional", false, true)]
    [InlineData("provisional", true, true)]
    [InlineData("raw", false, true)]
    [InlineData("isrc", false, false)]
    [InlineData("mbid", false, false)]
    [InlineData("confirmed", false, false)]
    [InlineData("manual", false, false)]
    public async Task Reconciliation_IsAtomicIdempotentAndProtectsStrongEvidence(
        string evidence, bool alreadyMoved, bool expected)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var seed = await SeedAsync(db, evidence, alreadyMoved);
        var before = seed.Identity.CanonicalRecordingId;
        var result = await CanonicalCatalogEvidenceStore.ReconcileSourceIdentityAsync(
            db, seed.Actor, seed.Identity, seed.Target, DateTimeOffset.UtcNow, default);
        Assert.Equal(expected, result);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var identity = await db.ProviderTrackIdentities.SingleAsync();
        var alias = await db.CanonicalCatalogAliases.SingleAsync();
        Assert.Equal(expected ? seed.Target : before, identity.CanonicalRecordingId);
        Assert.Equal(expected ? seed.Target : seed.Origin, alias.CanonicalEntityId);
        Assert.Equal(expected ? 1 : 0, await db.AuditEvents.CountAsync(item => item.Action == "source-identity.reconcile"));
        Assert.Equal(2, await db.CanonicalRecordings.CountAsync());
        if (!expected) return;

        var revision = identity.Revision;
        Assert.True(await CanonicalCatalogEvidenceStore.ReconcileSourceIdentityAsync(
            db, seed.Actor, identity, seed.Target, DateTimeOffset.UtcNow, default));
        await db.SaveChangesAsync();
        Assert.Equal(revision, identity.Revision);
        Assert.Single(await db.CanonicalCatalogAliases.ToListAsync());
        Assert.Single(await db.AuditEvents.Where(item => item.Action == "source-identity.reconcile").ToListAsync());
    }

    [Fact]
    public async Task Reconciliation_DeniesForeignTenantWithoutWrites()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var seed = await SeedAsync(db, "provisional", false);
        var foreign = new ProviderActorContext(Guid.CreateVersion7(), ProviderActorKind.User,
            Guid.CreateVersion7(), new ProviderBackendPrincipal("jellyfin", "backend", "other"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CanonicalCatalogEvidenceStore.ReconcileSourceIdentityAsync(
                db, foreign, seed.Identity, seed.Target, DateTimeOffset.UtcNow, default));
        Assert.Equal(seed.Origin, seed.Identity.CanonicalRecordingId);
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
    }

    [Theory]
    [InlineData("provisional", true, true)]
    [InlineData("provisional", true, false)]
    [InlineData("isrc", false, true)]
    [InlineData("mbid", false, true)]
    [InlineData("manual", false, true)]
    public async Task Migration_RepairsOnlyUnanchoredSourceAliasesAndNormalizesLegacyHashes(
        string evidence, bool repair, bool hasNormalizedAlias)
    {
        await using var database = await PostgresTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260927214131_AddAdminOidcLinks");
        var seed = await SeedAsync(db, evidence, true);
        var current = await db.CanonicalCatalogAliases.SingleAsync();
        if (!hasNormalizedAlias)
            db.CanonicalCatalogAliases.Remove(current);
        var legacyId = Guid.CreateVersion7();
        db.CanonicalCatalogAliases.Add(new CanonicalCatalogAliasRecord
        {
            Id = legacyId,
            TenantId = current.TenantId,
            EntityKind = current.EntityKind,
            CanonicalEntityId = current.CanonicalEntityId,
            Namespace = current.Namespace,
            ExternalId = current.ExternalId,
            ExternalIdHash = current.ExternalId,
            CreatedAt = current.CreatedAt,
            LastSeenAt = current.LastSeenAt
        });
        await db.SaveChangesAsync();
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        var alias = Assert.Single(await db.CanonicalCatalogAliases.ToArrayAsync());
        Assert.Equal(repair ? seed.Target : seed.Origin, alias.CanonicalEntityId);
        Assert.Equal(CanonicalCatalogKeys.Hash(alias.ExternalId), alias.ExternalIdHash);
        Assert.Equal(repair ? hasNormalizedAlias ? 2 : 1 : 0,
            await db.AuditEvents.CountAsync(item => item.Action == "source-alias.reconcile"));
        Assert.Equal(hasNormalizedAlias ? 1 : 0, await db.AuditEvents.CountAsync(item => item.Action == "alias.deduplicate"));
        Assert.Equal(seed.Target, (await db.ProviderTrackIdentities.SingleAsync()).CanonicalRecordingId);
        Assert.Equal(2, await db.CanonicalRecordings.CountAsync());
        await migrator.MigrateAsync();
        Assert.Single(await db.CanonicalCatalogAliases.ToArrayAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Migration_PreservesConflictingAliasesForReview(bool hasNormalizedHash)
    {
        await using var database = await PostgresTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260927214131_AddAdminOidcLinks");
        var seed = await SeedAsync(db, "isrc", true);
        var current = await db.CanonicalCatalogAliases.SingleAsync();
        if (!hasNormalizedHash)
            current.ExternalIdHash = CanonicalCatalogKeys.Hash("another-legacy-hash");
        db.CanonicalCatalogAliases.Add(new CanonicalCatalogAliasRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = current.TenantId,
            EntityKind = current.EntityKind,
            CanonicalEntityId = seed.Target,
            Namespace = current.Namespace,
            ExternalId = current.ExternalId,
            ExternalIdHash = current.ExternalId,
            CreatedAt = current.CreatedAt,
            LastSeenAt = current.LastSeenAt
        });
        await db.SaveChangesAsync();
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(2, await db.CanonicalCatalogAliases.CountAsync());
        Assert.Equal(seed.Origin, (await db.CanonicalCatalogAliases.SingleAsync(item => item.Id == current.Id)).CanonicalEntityId);
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    private static async Task<(ProviderActorContext Actor, ProviderTrackIdentityRecord Identity, Guid Origin, Guid Target)>
        SeedAsync(AllstarrDbContext db, string evidence, bool alreadyMoved)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var origin = Guid.CreateVersion7();
        var target = Guid.CreateVersion7();
        db.Tenants.Add(new TenantRecord { Id = tenant, Slug = $"reconcile-{tenant:N}", Name = "Reconcile", CreatedAt = now });
        db.Users.Add(new PlatformUserRecord
        {
            Id = user,
            TenantId = tenant,
            DisplayName = "Owner",
            Status = PlatformUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.CanonicalRecordings.AddRange(new CanonicalRecordingRecord
        {
            Id = origin,
            TenantId = tenant,
            CreatedByUserId = user,
            IsProvisional = evidence != "confirmed",
            Isrc = evidence == "isrc" ? "USRC17607839" : null,
            MusicBrainzRecordingId = evidence == "mbid" ? Guid.CreateVersion7().ToString() : null,
            CreatedAt = now,
            UpdatedAt = now
        }, new CanonicalRecordingRecord
        {
            Id = target,
            TenantId = tenant,
            CreatedByUserId = user,
            CreatedAt = now,
            UpdatedAt = now
        });
        var identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            CanonicalRecordingId = alreadyMoved ? target : origin,
            ProviderId = "fixture",
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Catalog,
            ExternalId = evidence == "raw" ? "source" : CanonicalCatalogKeys.Hash("source"),
            ExternalIdHash = CanonicalCatalogKeys.Hash("source"),
            Verification = evidence == "manual" ? ProviderIdentityVerification.Pinned : ProviderIdentityVerification.Verified,
            VerificationMethod = evidence switch
            {
                "manual" => "manual-review",
                "raw" => "source-snapshot",
                _ => "source-snapshot-hash"
            },
            DecisionVersion = 1,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ProviderTrackIdentities.Add(identity);
        db.CanonicalCatalogAliases.Add(new CanonicalCatalogAliasRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            EntityKind = CanonicalCatalogEntityKind.Recording,
            CanonicalEntityId = origin,
            Namespace = CanonicalCatalogKeys.ProviderTrackNamespace("fixture", ProviderResourceKind.Track, "default", ProviderIdentityScope.Catalog, null),
            ExternalId = identity.ExternalId,
            ExternalIdHash = CanonicalCatalogKeys.Hash(identity.ExternalId),
            CreatedAt = now,
            LastSeenAt = now
        });
        await db.SaveChangesAsync();
        return (new ProviderActorContext(tenant, ProviderActorKind.User, user,
            new ProviderBackendPrincipal("jellyfin", "backend", "owner")), identity, origin, target);
    }
}
