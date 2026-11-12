using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ListeningStatsCpuTests
{
    [Fact]
    public async Task MetadataLookupsPreserveProviderAccountAndListenerScopeAndNewestVersion()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        foreach (var user in new[] { owner, other })
            db.Users.Add(new() { Id = user, BackendType = "subsonic", BackendInstanceId = "fixture", BackendPrincipalId = user.ToString(), Enabled = true });
        var accounts = new[]
        {
            new ProviderAccountRecord { Id = Guid.NewGuid(), ProviderId = "deezer", OwnerUserId = owner, Enabled = true },
            new ProviderAccountRecord { Id = Guid.NewGuid(), ProviderId = "deezer", Enabled = true },
            new ProviderAccountRecord { Id = Guid.NewGuid(), ProviderId = "deezer", OwnerUserId = other, Enabled = true },
            new ProviderAccountRecord { Id = Guid.NewGuid(), ProviderId = "deezer", OwnerUserId = owner, Enabled = false }
        };
        db.ProviderAccounts.AddRange(accounts);
        var recording = Guid.NewGuid();
        db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner, PublicId = CanonicalRecordingRecord.DefaultPublicId(recording) });
        var identities = new List<ProviderTrackIdentityRecord>();
        foreach (var provider in new[] { "deezer", "spotify" })
            foreach (var account in accounts.Where(item => item.ProviderId == provider).Select(item => (Guid?)item.Id).Prepend(null))
            {
                var identity = new ProviderTrackIdentityRecord
                {
                    Id = Guid.NewGuid(),
                    CanonicalRecordingId = recording,
                    ProviderId = provider,
                    ProviderAccountId = account,
                    Scope = account == null ? ProviderIdentityScope.Catalog : ProviderIdentityScope.Account,
                    ResourceKind = ProviderResourceKind.Track,
                    ExternalId = "shared",
                    ExternalIdHash = Hash("shared"),
                    Verification = ProviderIdentityVerification.Verified,
                    DecisionVersion = 1
                };
                identities.Add(identity);
                db.ProviderTrackIdentities.Add(identity);
                for (var version = 1; version <= 2; version++)
                    foreach (var kind in new[] { "track", "playback-metadata", "artist" })
                        db.ExternalMetadataSnapshots.Add(new()
                        {
                            Id = Guid.NewGuid(),
                            OwnerUserId = owner,
                            Protocol = "subsonic",
                            BackendInstanceId = "fixture",
                            BackendPrincipalId = owner.ToString(),
                            ProviderId = provider,
                            ProviderAccountId = account,
                            ExternalIdHash = identity.ExternalIdHash,
                            ResourceKind = kind,
                            SnapshotVersion = version,
                            PayloadSha256 = Hash("{}"),
                            RetrievedAt = DateTimeOffset.UnixEpoch.AddSeconds(version)
                        });
            }
        await db.SaveChangesAsync();
        var scope = new IntelligenceScope(owner, "subsonic", "fixture");
        var before = await OriginalMetadata(db, scope, [recording], identities.Select(item => item.Id).ToArray()).ToArrayAsync();
        var after = await ListeningStatsService.ArtistMetadataAsync(db, scope, [recording], identities.Select(item => item.Id).ToArray(), default);
        Assert.NotEmpty(before);
        Assert.Equal(before.Select(item => (item.Snapshot.Id, item.Identity.Id)).OrderBy(item => item.Item1).ThenBy(item => item.Item2),
            after.Select(item => (item.Snapshot.Id, item.Identity.Id)).OrderBy(item => item.Item1).ThenBy(item => item.Item2));
        Assert.Equal(2, after[0].Snapshot.SnapshotVersion);
        Assert.DoesNotContain(after, item => item.Snapshot.ResourceKind == "artist" ||
            item.Snapshot.ProviderAccountId == accounts[2].Id || item.Snapshot.ProviderAccountId == accounts[3].Id);
    }

    private static IQueryable<MetadataPair> OriginalMetadata(AllstarrDbContext db, IntelligenceScope scope, Guid[] recordings, Guid[] identities) =>
        from snapshot in db.ExternalMetadataSnapshots.AsNoTracking()
        join identity in db.ProviderTrackIdentities on new { snapshot.ProviderId, snapshot.ExternalIdHash } equals new { identity.ProviderId, identity.ExternalIdHash }
        where snapshot.OwnerUserId == scope.OwnerUserId && snapshot.Protocol == scope.Protocol && snapshot.BackendInstanceId == scope.BackendInstanceId &&
            (snapshot.ResourceKind == "track" || snapshot.ResourceKind == "playback-metadata") &&
            snapshot.BackendPrincipalId == db.Users.Where(user => user.Id == scope.OwnerUserId).Select(user => user.BackendPrincipalId).First() &&
            (recordings.Contains(identity.CanonicalRecordingId) || identities.Contains(identity.Id)) && identity.ResourceKind == ProviderResourceKind.Track &&
            (snapshot.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == snapshot.ProviderAccountId && account.Enabled &&
                (account.OwnerUserId == null || account.OwnerUserId == scope.OwnerUserId))) &&
            (identity.Scope == ProviderIdentityScope.Catalog || identity.Scope == ProviderIdentityScope.Account && identity.ProviderAccountId != null &&
                identity.ProviderAccountId == snapshot.ProviderAccountId)
        orderby snapshot.RetrievedAt descending
        select new MetadataPair(snapshot, identity);

    private sealed record MetadataPair(ExternalMetadataSnapshotRecord Snapshot, ProviderTrackIdentityRecord Identity);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
