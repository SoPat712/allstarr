using System.Data.Common;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Tests;

public sealed partial class LibraryIndexServiceTests
{
    [Fact]
    public async Task NativeLookupUsesBoundedSourceReadsAmongUnrelatedSnapshots()
    {
        var viewer = Context(_userA, "principal-a");
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "lookup", null, null);
        var identity = ReadIdentity(recording.Recording.Id, "spotify", "known-source");
        await using (var db = _factory.CreateDbContext())
        {
            db.ProviderTrackIdentities.Add(identity);
            db.ExternalMetadataSnapshots.Add(ReadSnapshot(identity));
            db.LibraryTracks.Add(SelfLoveMatchFixture.Local(_userA, "backend"));
            for (var index = 0; index < 750; index++)
            {
                var id = Guid.CreateVersion7();
                db.CanonicalRecordings.Add(new() { Id = id, CreatedByUserId = _userA });
                var unrelated = ReadIdentity(id, "spotify", $"unrelated-{index}");
                db.ProviderTrackIdentities.Add(unrelated);
                for (var version = 1; version <= 3; version++)
                {
                    var snapshot = ReadSnapshot(unrelated);
                    snapshot.SnapshotVersion = version;
                    snapshot.PayloadJson = "{}";
                    db.ExternalMetadataSnapshots.Add(snapshot);
                }
            }
            await db.SaveChangesAsync();
        }
        var reads = new RecordingReadCounter();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>(_database.Options)
            .AddInterceptors(reads).Options);
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        var service = new LibraryIndexService(factory, state, _clock, _identities, _libraryAccess.Object);
        var matches = await service.FindKnownRecordingTracksAsync(viewer,
            [new("external-alias", recording.Recording.Id, null, "apple-musickit", "alias")]);

        Assert.Equal(SelfLoveMatchFixture.BackendItemId, Assert.Single(matches).Value);
        var snapshotRead = Assert.Single(reads.Commands, command =>
            command.Contains("FROM \"external_metadata_snapshots\"", StringComparison.Ordinal));
        Assert.DoesNotContain("provider_track_identities", snapshotRead, StringComparison.Ordinal);
        Assert.Equal(6, reads.Commands.Count);
    }

    [Theory]
    [InlineData("current", true)]
    [InlineData("unlinked", true)]
    [InlineData("direct", true)]
    [InlineData("shared", true)]
    [InlineData("account", false)]
    [InlineData("foreign", false)]
    [InlineData("disabled", false)]
    [InlineData("owner", false)]
    [InlineData("protocol", false)]
    [InlineData("backend", false)]
    [InlineData("principal", false)]
    [InlineData("identity", false)]
    [InlineData("ambiguous", false)]
    [InlineData("source-pair", false)]
    [InlineData("superseded", false)]
    [InlineData("latest", false)]
    public async Task NativeCanonicalSourceReadsPreserveSnapshotAuthority(string variation, bool expected)
    {
        var viewer = Context(_userA, "principal-a");
        var recording = await _identities.CreateRecordingAsync(viewer.RequireActor(), "lookup", null, null);
        var identity = ReadIdentity(recording.Recording.Id, "spotify", "known-source");
        var snapshot = ReadSnapshot(identity);
        await using (var db = _factory.CreateDbContext())
        {
            db.ProviderTrackIdentities.Add(identity);
            db.ExternalMetadataSnapshots.Add(snapshot);
            db.LibraryTracks.Add(SelfLoveMatchFixture.Local(_userA, "backend"));
            if (variation == "unlinked") snapshot.ProviderTrackIdentityId = null;
            if (variation == "direct")
            {
                snapshot.ProviderTrackIdentityId = null;
                snapshot.ProviderId = "apple-musickit";
                snapshot.ExternalIdHash = CanonicalCatalogKeys.Hash("alias");
            }
            if (variation is "account" or "foreign" or "disabled" or "shared")
            {
                var account = ReadAccount(variation == "foreign" ? _userB : variation == "shared" ? null : _userA);
                account.Enabled = variation != "disabled";
                db.ProviderAccounts.Add(account);
                identity.Scope = ProviderIdentityScope.Account;
                identity.ProviderAccountId = account.Id;
                snapshot.ProviderAccountId = account.Id;
                if (variation == "account")
                {
                    var other = ReadAccount(_userA);
                    db.ProviderAccounts.Add(other);
                    snapshot.ProviderAccountId = other.Id;
                }
            }
            if (variation == "owner") snapshot.OwnerUserId = _userB;
            if (variation == "protocol") snapshot.Protocol = "subsonic";
            if (variation == "backend") snapshot.BackendInstanceId = "other";
            if (variation == "principal") snapshot.BackendPrincipalId = "principal-b";
            if (variation is "identity" or "ambiguous")
            {
                var otherId = Guid.CreateVersion7();
                db.CanonicalRecordings.Add(new() { Id = otherId, CreatedByUserId = _userA });
                var other = ReadIdentity(otherId, identity.ProviderId, identity.ExternalId);
                other.CatalogNamespace = "other";
                db.ProviderTrackIdentities.Add(other);
                snapshot.ProviderTrackIdentityId = variation == "identity" ? other.Id : null;
            }
            if (variation == "source-pair")
            {
                var other = ReadIdentity(recording.Recording.Id, "deezer", "other-source");
                db.ProviderTrackIdentities.Add(other);
                snapshot.ProviderTrackIdentityId = null;
                snapshot.ExternalIdHash = other.ExternalIdHash;
            }
            if (variation == "superseded") identity.VerificationMethod = CatalogIdentityPolicy.SupersededVerificationMethod;
            if (variation == "latest")
            {
                var newer = ReadSnapshot(identity);
                newer.SnapshotVersion = 2;
                newer.RetrievedAt = snapshot.RetrievedAt.AddSeconds(1);
                newer.PayloadJson = SelfLoveMatchFixture.Payload.Replace("189466", "240000", StringComparison.Ordinal);
                db.ExternalMetadataSnapshots.Add(newer);
            }
            await db.SaveChangesAsync();
        }

        var matches = await _service.FindKnownRecordingTracksAsync(viewer,
            [new("external-alias", recording.Recording.Id, null, "apple-musickit", "alias")]);
        Assert.Equal(expected ? SelfLoveMatchFixture.BackendItemId : null, matches.GetValueOrDefault("external-alias"));
        await using var saved = _factory.CreateDbContext();
        Assert.Empty(await saved.TrackMatches.ToArrayAsync());
    }

    private ProviderTrackIdentityRecord ReadIdentity(Guid recordingId, string provider, string externalId) => new()
    {
        Id = Guid.CreateVersion7(),
        CanonicalRecordingId = recordingId,
        ProviderId = provider,
        ExternalId = externalId,
        ExternalIdHash = CanonicalCatalogKeys.Hash(externalId),
        ResourceKind = ProviderResourceKind.Track,
        Scope = ProviderIdentityScope.Catalog,
        Verification = ProviderIdentityVerification.Verified,
        VerificationMethod = "fixture",
        DecisionVersion = 1
    };

    private ExternalMetadataSnapshotRecord ReadSnapshot(ProviderTrackIdentityRecord identity) => new()
    {
        Id = Guid.CreateVersion7(),
        OwnerUserId = _userA,
        ProviderId = identity.ProviderId,
        ProviderTrackIdentityId = identity.Id,
        ResourceKind = "track",
        Protocol = "jellyfin",
        BackendInstanceId = "backend",
        BackendPrincipalId = "principal-a",
        ExternalIdHash = identity.ExternalIdHash,
        SnapshotVersion = 1,
        RetrievedAt = _clock.UtcNow,
        PayloadSha256 = new string('a', 64),
        PayloadJson = SelfLoveMatchFixture.Payload
    };

    private ProviderAccountRecord ReadAccount(Guid? owner) => new()
    {
        Id = Guid.CreateVersion7(),
        ProviderId = "spotify",
        OwnerUserId = owner,
        DisplayName = "Fixture",
        Enabled = true,
        CreatedAt = _clock.UtcNow,
        UpdatedAt = _clock.UtcNow
    };

    private sealed class RecordingReadCounter(int delayMilliseconds = 0) : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken);
            return result;
        }
    }
}
