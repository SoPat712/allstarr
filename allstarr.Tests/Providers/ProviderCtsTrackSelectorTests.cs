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
    public async Task Select_UsesGlobalVerifiedCatalogAndExcludesPrivateAccountIdentities()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var user = Guid.CreateVersion7();
        var otherUser = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.Users.AddRange(
                User(user, "CTS", now),
                User(otherUser, "Other", now));
            var selectedRecording = Guid.CreateVersion7();
            var otherRecording = Guid.CreateVersion7();
            db.CanonicalRecordings.AddRange(
                new CanonicalRecordingRecord { Id = selectedRecording, CreatedByUserId = user, CreatedAt = now, UpdatedAt = now, Revision = 1 },
                new CanonicalRecordingRecord { Id = otherRecording, CreatedByUserId = otherUser, CreatedAt = now, UpdatedAt = now, Revision = 1 });
            db.ProviderTrackIdentities.AddRange(
                Identity(selectedRecording, "selected", now),
                Identity(otherRecording, "other-contributor", now.AddMinutes(1)));
            var account = Guid.CreateVersion7();
            db.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = account,
                ProviderId = "deezer",
                OwnerUserId = otherUser,
                DisplayName = "Private account",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            var privateIdentity = Identity(otherRecording, "private-track", now);
            privateIdentity.Scope = ProviderIdentityScope.Account;
            privateIdentity.ProviderAccountId = account;
            db.ProviderTrackIdentities.Add(privateIdentity);
            await db.SaveChangesAsync();
        }

        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var result = await selector.SelectAsync(
            "deezer", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.CorpusSize);
        var next = await selector.SelectAsync("deezer", null, CancellationToken.None);
        Assert.Equal(new[] { "other-contributor", "selected" },
            new[] { result.TrackId, next!.TrackId }.OrderBy(item => item, StringComparer.Ordinal));
    }

    private static UserRecord User(
        Guid id,
        string name,
        DateTimeOffset now) => new()
        {
            Id = id,
            DisplayName = name,
            Enabled = true,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = id.ToString("N"),
            CreatedAt = now,
            UpdatedAt = now
        };

    private static ProviderTrackIdentityRecord Identity(
        Guid recordingId,
        string externalId,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
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
