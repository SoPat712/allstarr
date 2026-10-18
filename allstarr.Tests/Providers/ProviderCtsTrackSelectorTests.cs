using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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

    [Fact]
    public async Task RecordingSamples_UseVerifiedPublicIdentitiesAndMetadata()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var recording = Guid.CreateVersion7();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.Users.Add(User(user, "Contributor", now));
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = recording,
                CreatedByUserId = user,
                Title = "Song",
                DurationMilliseconds = 123000,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1,
                Identifiers = [new() { Id = Guid.CreateVersion7(), RecordingId = recording,
                    Kind = RecordingIdentifierKinds.Isrc, Value = "USABC1234567", Source = "fixture", CreatedAt = now }]
            });
            var identity = Identity(recording, "123", now);
            db.ProviderTrackIdentities.Add(identity);
            var unsupported = Identity(recording, "unsupported", now);
            unsupported.ProviderId = "other";
            db.ProviderTrackIdentities.Add(unsupported);
            var account = Guid.CreateVersion7();
            db.ProviderAccounts.Add(new ProviderAccountRecord { Id = account, ProviderId = "spotify", OwnerUserId = user, DisplayName = "Private" });
            var privateIdentity = Identity(recording, "private", now);
            privateIdentity.ProviderId = "spotify";
            privateIdentity.Scope = ProviderIdentityScope.Account;
            privateIdentity.ProviderAccountId = account;
            db.ProviderTrackIdentities.Add(privateIdentity);
            db.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = user,
                ProviderTrackIdentityId = identity.Id,
                ProviderId = "deezer",
                ResourceKind = "track",
                RetrievedAt = now,
                SnapshotVersion = 1,
                ExternalIdHash = identity.ExternalIdHash,
                PayloadSha256 = new string('a', 64),
                PayloadJson = """{"Title":"Song","Artist":"Artist","durationMilliseconds":123000,"Isrc":"USABC1234567"}"""
            });
            await db.SaveChangesAsync();
        }
        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var samples = await selector.SelectRecordingSamplesAsync(CancellationToken.None);
        var sample = Assert.Single(samples);
        Assert.Equal("Song", sample.Title);
        Assert.Equal(new[] { "Artist" }, sample.ArtistNames);
        Assert.Equal("USABC1234567", sample.Isrc);
        Assert.Equal(123000, sample.DurationMilliseconds);
        Assert.Equal("123", sample.ProviderIds["deezer"]);
        Assert.Single(sample.ProviderIds);
    }

    [Theory]
    [InlineData(TrackMatchState.Accepted)]
    [InlineData(TrackMatchState.Unresolved)]
    public async Task RecordingSamples_UseCanonicalMetadataAndMatchingCandidateWithoutTargetSnapshot(TrackMatchState state)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var recording = Guid.CreateVersion7();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.Users.Add(User(user, "Contributor", now));
            db.CanonicalRecordings.Add(Recording(recording, user, now));
            var identity = Identity(recording, "123", now);
            identity.VerificationMethod = "automatic-match";
            db.ProviderTrackIdentities.Add(identity);
            var source = Identity(recording, "source", now);
            source.ProviderId = "spotify";
            source.Scope = ProviderIdentityScope.Account;
            var account = Account(user, now);
            source.ProviderAccountId = account.Id;
            db.ProviderAccounts.Add(account);
            db.ProviderTrackIdentities.Add(source);
            var snapshot = Snapshot(user, source, now, "{}", account.Id);
            db.ExternalMetadataSnapshots.Add(snapshot);
            db.TrackMatches.Add(new TrackMatchRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = user,
                ExternalSnapshotId = snapshot.Id,
                CanonicalRecordingId = recording,
                State = state,
                Confidence = .99,
                Threshold = .88,
                DecisionVersion = 1,
                DecidedAt = now,
                CandidateResultsJson = """
                    [{"ProviderTrackIds":{"deezer":"unrelated"},"Artist":"Wrong artist"},
                     {"ProviderTrackIds":{"deezer":"123"},"Title":"Candidate title",
                      "Artists":["Selected artist","Guest"],"CandidateIsrc":"USXYZ1234567",
                      "DurationMilliseconds":234000}]
                    """
            });
            await db.SaveChangesAsync();
        }

        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var sample = Assert.Single(await selector.SelectRecordingSamplesAsync(CancellationToken.None));

        Assert.Equal("Canonical title", sample.Title);
        Assert.Equal(new[] { "Selected artist", "Guest" }, sample.ArtistNames);
        Assert.Equal("USABC1234567", sample.Isrc);
        Assert.Equal(123000, sample.DurationMilliseconds);
        Assert.Equal("123", Assert.Single(sample.ProviderIds).Value);
        Assert.False(sample.ProviderIds.ContainsKey("spotify"));
    }

    [Theory]
    [InlineData("spotiflac-tidal-web", "tidal:456", "tidal", "456")]
    [InlineData("spotiflac-deezer", "deezer:123", "deezer", "123")]
    [InlineData("spotiflac-qobuz-web", "qobuz:789", "qobuz", "789")]
    [InlineData("deezer", "123", "deezer", "123")]
    [InlineData("qobuz", "789", "qobuz", "789")]
    public async Task RecordingSamples_MapProviderIdentitiesToAvailabilityIds(
        string provider, string externalId, string expectedProvider, string expectedId)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var recording = Guid.CreateVersion7();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.Users.Add(User(user, "Contributor", now));
            db.CanonicalRecordings.Add(Recording(recording, user, now));
            var identity = Identity(recording, externalId, now);
            identity.ProviderId = provider;
            db.ProviderTrackIdentities.Add(identity);
            db.ExternalMetadataSnapshots.Add(Snapshot(user, identity, now, """{"Artist":"Artist"}"""));
            await db.SaveChangesAsync();
        }

        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var sample = Assert.Single(await selector.SelectRecordingSamplesAsync(CancellationToken.None));

        var id = Assert.Single(sample.ProviderIds);
        Assert.Equal(expectedProvider, id.Key);
        Assert.Equal(expectedId, id.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingSamples_ExcludeOtherUsersAccountSnapshots(bool catalogIdentity)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var otherUser = Guid.CreateVersion7();
        var recording = Guid.CreateVersion7();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.Users.AddRange(User(user, "Administrator", now), User(otherUser, "Other", now));
            db.CanonicalRecordings.Add(Recording(recording, user, now));
            var identity = Identity(recording, "123", now);
            db.ProviderTrackIdentities.Add(identity);
            var account = Account(otherUser, now);
            account.ProviderId = "deezer";
            db.ProviderAccounts.Add(account);
            if (!catalogIdentity)
            {
                identity = Identity(recording, "private", now);
                identity.Scope = ProviderIdentityScope.Account;
                identity.ProviderAccountId = account.Id;
                db.ProviderTrackIdentities.Add(identity);
            }
            db.ExternalMetadataSnapshots.Add(Snapshot(otherUser, identity, now,
                """{"Artist":"Private artist"}""", account.Id));
            await db.SaveChangesAsync();
        }

        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        Assert.Empty(await selector.SelectRecordingSamplesAsync(CancellationToken.None, user));
    }

    [Fact]
    public async Task RecordingSamples_UseCatalogMetadataFromAnotherIdentityOfTheRecording()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var recording = Guid.CreateVersion7();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.Users.Add(User(user, "Contributor", now));
            db.CanonicalRecordings.Add(Recording(recording, user, now));
            db.ProviderTrackIdentities.Add(Identity(recording, "123", now));
            var metadataIdentity = Identity(recording, "metadata", now);
            metadataIdentity.ProviderId = "other";
            db.ProviderTrackIdentities.Add(metadataIdentity);
            db.ExternalMetadataSnapshots.Add(Snapshot(user, metadataIdentity, now,
                """{"Title":"Snapshot title","Artists":[{"Name":"Catalog artist"}],"Isrc":"USXYZ1234567","durationMs":234000}"""));
            await db.SaveChangesAsync();
        }

        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var sample = Assert.Single(await selector.SelectRecordingSamplesAsync(CancellationToken.None));

        Assert.Equal("Canonical title", sample.Title);
        Assert.Equal(new[] { "Catalog artist" }, sample.ArtistNames);
        Assert.Equal("USABC1234567", sample.Isrc);
        Assert.Equal(123000, sample.DurationMilliseconds);
        Assert.Equal("deezer", Assert.Single(sample.ProviderIds).Key);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RecordingSamples_UseOwnAccountMetadataOnlyAfterPublicEvidence(
        bool catalogMetadata, bool candidateMetadata)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var recording = Guid.CreateVersion7();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.Users.Add(User(user, "Administrator", now));
            var canonical = Recording(recording, user, now);
            canonical.Title = "";
            canonical.DurationMilliseconds = null;
            canonical.Identifiers.Clear();
            db.CanonicalRecordings.Add(canonical);
            var identity = Identity(recording, "123", now);
            db.ProviderTrackIdentities.Add(identity);
            if (catalogMetadata)
                db.ExternalMetadataSnapshots.Add(Snapshot(user, identity, now,
                    """{"Title":"Catalog title","Artists":["Catalog artist"],"Isrc":"USCAT1234567","durationMs":234000}"""));
            var source = Identity(recording, "source", now);
            source.ProviderId = "spotify";
            source.Scope = ProviderIdentityScope.Account;
            var account = Account(user, now);
            source.ProviderAccountId = account.Id;
            db.ProviderAccounts.Add(account);
            db.ProviderTrackIdentities.Add(source);
            var snapshot = Snapshot(user, source, now.AddMinutes(1),
                """{"Title":"Own title","Artist":"Own artist","Isrc":"USOWN1234567","durationSeconds":345}""", account.Id);
            db.ExternalMetadataSnapshots.Add(snapshot);
            if (candidateMetadata)
                db.TrackMatches.Add(new TrackMatchRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = user,
                    ExternalSnapshotId = snapshot.Id,
                    CanonicalRecordingId = recording,
                    State = TrackMatchState.Accepted,
                    Confidence = .99,
                    Threshold = .88,
                    DecisionVersion = 1,
                    DecidedAt = now,
                    CandidateResultsJson = """
                        [{"providerTrackIds":{"deezer":"123"},"title":"Candidate title",
                          "primaryArtist":"Candidate artist","candidateIsrc":"USCAN1234567","durationSeconds":456}]
                        """
                });
            await db.SaveChangesAsync();
        }

        using var selector = new ProviderCtsTrackSelector(new Factory(database.Options));
        var sample = Assert.Single(await selector.SelectRecordingSamplesAsync(CancellationToken.None, user));

        Assert.Equal(catalogMetadata ? "Catalog title" : candidateMetadata ? "Candidate title" : "Own title", sample.Title);
        Assert.Equal(new[] { catalogMetadata ? "Catalog artist" : candidateMetadata ? "Candidate artist" : "Own artist" }, sample.ArtistNames);
        Assert.Equal(catalogMetadata ? "USCAT1234567" : candidateMetadata ? "USCAN1234567" : "USOWN1234567", sample.Isrc);
        Assert.Equal(catalogMetadata ? 234000 : candidateMetadata ? 456000 : 345000, sample.DurationMilliseconds);
        Assert.False(sample.ProviderIds.ContainsKey("spotify"));
        if (!catalogMetadata && !candidateMetadata)
            Assert.Empty(await selector.SelectRecordingSamplesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RecordingSamples_BoundTheCorpusAndUseBatchedMetadataQueries()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.Users.Add(User(user, "Contributor", now));
            for (var index = 0; index <= ProviderCtsTrackSelector.CorpusLimit; index++)
            {
                var recording = Guid.CreateVersion7();
                var canonical = Recording(recording, user, now);
                canonical.Identifiers.Clear();
                db.CanonicalRecordings.Add(canonical);
                var identity = Identity(recording, index.ToString(), now);
                db.ProviderTrackIdentities.Add(identity);
                db.ExternalMetadataSnapshots.Add(Snapshot(user, identity, now, """{"Artist":"Artist"}"""));
            }
            await db.SaveChangesAsync();
        }
        var reads = new QueryCounter();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(reads).Options;
        using var selector = new ProviderCtsTrackSelector(new Factory(options));

        var samples = await selector.SelectRecordingSamplesAsync(CancellationToken.None, user);

        Assert.Equal(ProviderCtsTrackSelector.CorpusLimit, samples.Count);
        Assert.InRange(reads.Count, 1, 5);
    }

    private static CanonicalRecordingRecord Recording(Guid id, Guid user, DateTimeOffset now) => new()
    {
        Id = id,
        CreatedByUserId = user,
        Title = "Canonical title",
        DurationMilliseconds = 123000,
        CreatedAt = now,
        UpdatedAt = now,
        Revision = 1,
        Identifiers = [new() { Id = Guid.CreateVersion7(), RecordingId = id,
            Kind = RecordingIdentifierKinds.Isrc, Value = "USABC1234567", Source = "fixture", CreatedAt = now }]
    };

    private static ProviderAccountRecord Account(Guid user, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ProviderId = "spotify",
        OwnerUserId = user,
        DisplayName = "Private",
        CreatedAt = now,
        UpdatedAt = now
    };

    private static ExternalMetadataSnapshotRecord Snapshot(Guid user, ProviderTrackIdentityRecord identity,
        DateTimeOffset now, string json, Guid? account = null) => new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user,
            ProviderTrackIdentityId = identity.Id,
            ProviderAccountId = account,
            ProviderId = identity.ProviderId,
            ResourceKind = "track",
            RetrievedAt = now,
            SnapshotVersion = 1,
            ExternalIdHash = identity.ExternalIdHash,
            PayloadSha256 = new string('a', 64),
            PayloadJson = json
        };

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

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
