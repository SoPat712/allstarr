using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class CanonicalAliasMigrationTests
{
    [Fact]
    public async Task Migration_ProjectsExistingSignalsAndScopedProviderIdentities()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(useTemplate: false);
        await using (var oldSchema = new AllstarrDbContext(database.Options))
        {
            var migrator = oldSchema.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260915000028_ExpandCanonicalCatalog");
        }

        var now = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var secondUserId = Guid.CreateVersion7();
        var backendIdentityId = Guid.CreateVersion7();
        var secondBackendIdentityId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var provisionalRecordingId = Guid.CreateVersion7();
        var musicBrainzRecordingId = Guid.CreateVersion7();
        const string externalId = "same-provider-track";
        const string isrc = "USRC17607839";
        const string mbid = "11111111-2222-3333-4444-555555555555";
        var externalIdHash = CanonicalCatalogKeys.Hash(externalId);
        await using (var seed = new AllstarrDbContext(database.Options))
        {
            seed.AddRange(
                new TenantRecord
                {
                    Id = tenantId,
                    Slug = "canonical-alias-migration",
                    Name = "Canonical alias migration",
                    CreatedAt = now
                },
                new PlatformUserRecord
                {
                    Id = userId,
                    TenantId = tenantId,
                    DisplayName = "Catalog owner",
                    Status = PlatformUserStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new PlatformUserRecord
                {
                    Id = secondUserId,
                    TenantId = tenantId,
                    DisplayName = "Second catalog user",
                    Status = PlatformUserStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new BackendIdentityRecord
                {
                    Id = backendIdentityId,
                    TenantId = tenantId,
                    UserId = userId,
                    BackendType = "jellyfin",
                    BackendInstanceId = "home",
                    PrincipalId = "catalog-owner",
                    DisplayName = "Catalog owner",
                    CreatedAt = now,
                    LastSeenAt = now
                },
                new BackendIdentityRecord
                {
                    Id = secondBackendIdentityId,
                    TenantId = tenantId,
                    UserId = secondUserId,
                    BackendType = "jellyfin",
                    BackendInstanceId = "home",
                    PrincipalId = "second-catalog-user",
                    DisplayName = "Second catalog user",
                    CreatedAt = now,
                    LastSeenAt = now
                },
                new ProviderAccountRecord
                {
                    Id = accountId,
                    TenantId = tenantId,
                    OwnerUserId = userId,
                    ProviderId = "fixture",
                    DisplayName = "Fixture account",
                    Scope = ProviderAccountScope.User,
                    Enabled = true,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new CanonicalRecordingRecord
                {
                    Id = provisionalRecordingId,
                    TenantId = tenantId,
                    CreatedByUserId = userId,
                    Title = "Provider-only recording",
                    Isrc = isrc,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new CanonicalRecordingRecord
                {
                    Id = musicBrainzRecordingId,
                    TenantId = tenantId,
                    CreatedByUserId = userId,
                    Title = "MusicBrainz recording",
                    MusicBrainzRecordingId = mbid,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                Identity(
                    provisionalRecordingId,
                    providerAccountId: null,
                    ProviderIdentityScope.Catalog),
                Identity(
                    musicBrainzRecordingId,
                    accountId,
                    ProviderIdentityScope.Account),
                LibraryTrack(userId, backendIdentityId, "owner.flac"),
                LibraryTrack(secondUserId, secondBackendIdentityId, "second.flac"));
            await seed.SaveChangesAsync();
        }

        await using (var migrate = new AllstarrDbContext(database.Options))
        {
            await migrate.Database.MigrateAsync();
        }

        await using var verification = new AllstarrDbContext(database.Options);
        var recordings = await verification.CanonicalRecordings
            .OrderBy(item => item.Id)
            .ToDictionaryAsync(item => item.Id);
        Assert.True(recordings[provisionalRecordingId].IsProvisional);
        Assert.False(recordings[musicBrainzRecordingId].IsProvisional);

        var aliases = await verification.CanonicalCatalogAliases
            .OrderBy(item => item.Namespace)
            .ToListAsync();
        Assert.Equal(5, aliases.Count);
        Assert.Contains(aliases, alias =>
            alias.Namespace == "isrc" &&
            alias.ExternalId == isrc &&
            alias.CanonicalEntityId == provisionalRecordingId);
        Assert.Contains(aliases, alias =>
            alias.Namespace == "musicbrainz" &&
            alias.ExternalId == mbid &&
            alias.CanonicalEntityId == musicBrainzRecordingId);

        var catalogNamespace = CanonicalCatalogKeys.ProviderTrackNamespace(
            "fixture", ProviderResourceKind.Track, "default", ProviderIdentityScope.Catalog, null);
        var accountNamespace = CanonicalCatalogKeys.ProviderTrackNamespace(
            "fixture", ProviderResourceKind.Track, "default", ProviderIdentityScope.Account, accountId);
        Assert.NotEqual(catalogNamespace, accountNamespace);
        Assert.Contains(aliases, alias =>
            alias.Namespace == catalogNamespace &&
            alias.ExternalId == externalId &&
            alias.CanonicalEntityId == provisionalRecordingId);
        Assert.Contains(aliases, alias =>
            alias.Namespace == accountNamespace &&
            alias.ExternalId == externalId &&
            alias.CanonicalEntityId == musicBrainzRecordingId);
        Assert.Contains(aliases, alias =>
            alias.Namespace == CanonicalCatalogKeys.NativeTrackNamespace("jellyfin", "home") &&
            alias.ExternalId == "native-item" &&
            alias.CanonicalEntityId == provisionalRecordingId);

        ProviderTrackIdentityRecord Identity(
            Guid canonicalRecordingId,
            Guid? providerAccountId,
            ProviderIdentityScope scope) => new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                CanonicalRecordingId = canonicalRecordingId,
                ProviderAccountId = providerAccountId,
                ProviderId = "fixture",
                ResourceKind = ProviderResourceKind.Track,
                CatalogNamespace = "default",
                Scope = scope,
                ExternalId = externalId,
                ExternalIdHash = externalIdHash,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "migration-fixture",
                DecisionVersion = 1,
                VerifiedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };

        LibraryTrackRecord LibraryTrack(
            Guid ownerUserId,
            Guid backendIdentity,
            string fileName) => new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OwnerUserId = ownerUserId,
                BackendIdentityId = backendIdentity,
                CanonicalRecordingId = provisionalRecordingId,
                LibraryScopeId = "music",
                Protocol = "jellyfin",
                BackendInstanceId = "home",
                BackendItemId = "native-item",
                FilePath = $"/media/{fileName}",
                Title = "Provider-only recording",
                Artist = "Fixture artist",
                ProviderIdsJson = "{}",
                IndexedAt = now,
                SourceModifiedAt = now,
                UpdatedAt = now
            };
    }
}
