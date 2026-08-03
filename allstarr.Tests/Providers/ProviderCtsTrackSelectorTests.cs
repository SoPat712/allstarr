using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ProviderCtsTrackSelectorTests
{
    [Fact]
    public async Task Select_UsesOnlyVerifiedCatalogTracksFromTheActorTenant()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var tenant = Guid.CreateVersion7();
        var otherTenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var otherUser = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.Tenants.AddRange(
                new TenantRecord { Id = tenant, Slug = "cts", Name = "CTS", CreatedAt = now },
                new TenantRecord { Id = otherTenant, Slug = "other", Name = "Other", CreatedAt = now });
            db.Users.AddRange(
                User(user, tenant, "CTS", now),
                User(otherUser, otherTenant, "Other", now));
            var selectedRecording = Guid.CreateVersion7();
            var otherRecording = Guid.CreateVersion7();
            db.CanonicalRecordings.AddRange(
                new CanonicalRecordingRecord { Id = selectedRecording, TenantId = tenant, CreatedByUserId = user, CreatedAt = now, UpdatedAt = now, Revision = 1 },
                new CanonicalRecordingRecord { Id = otherRecording, TenantId = otherTenant, CreatedByUserId = otherUser, CreatedAt = now, UpdatedAt = now, Revision = 1 });
            db.ProviderTrackIdentities.AddRange(
                Identity(tenant, selectedRecording, "selected", now),
                Identity(otherTenant, otherRecording, "wrong-tenant", now.AddMinutes(1)));
            await db.SaveChangesAsync();
        }

        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var result = await selector.SelectAsync(
            tenant, "deezer", Guid.CreateVersion7(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("selected", result.TrackId);
        Assert.Equal(1, result.CorpusSize);
    }

    private static PlatformUserRecord User(
        Guid id,
        Guid tenantId,
        string name,
        DateTimeOffset now) => new()
        {
            Id = id,
            TenantId = tenantId,
            DisplayName = name,
            Status = PlatformUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static ProviderTrackIdentityRecord Identity(
        Guid tenantId,
        Guid recordingId,
        string externalId,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CanonicalRecordingId = recordingId,
            ProviderId = "deezer",
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Catalog,
            ExternalId = externalId,
            ExternalIdHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(externalId))).ToLowerInvariant(),
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "fixture",
            DecisionVersion = 1,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        };

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
