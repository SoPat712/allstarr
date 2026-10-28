using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ProviderTargetProjectionTests
{
    [Fact]
    public void Target_serializes_with_the_names_the_web_ui_reads()
    {
        var target = new TrackMatchTarget("apple-download", "6792960885") { Title = "Broken chair", Artist = "Rachel Chinouriri" };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(target));

        Assert.Equal("apple-musickit", json.RootElement.GetProperty("providerId").GetString());
        Assert.Equal("Broken chair", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("Rachel Chinouriri", json.RootElement.GetProperty("artist").GetString());
        Assert.StartsWith("/api/admin/downloads/artwork/", json.RootElement.GetProperty("artworkUrl").GetString());
    }

    [Fact]
    public void Accepted_provider_target_uses_stored_evidence_when_candidates_are_not_displayed()
    {
        var recording = Guid.NewGuid();
        var source = Identity(recording, "spotify", "source");
        source.VerificationMethod = "source-snapshot-hash";
        var apple = Identity(recording, "apple-download", "6792960885");
        var alias = Identity(recording, "apple-musickit", apple.ExternalId);
        var decision = new TrackMatchRecord
        {
            CanonicalRecordingId = recording,
            State = TrackMatchState.Accepted,
            Confidence = .99,
            Threshold = .88,
            CandidateResultsJson = """
                [{"IsLocal":true,"ProviderTrackIds":{"apple-download":"6792960885"},
                  "Title":"Selected title","Artist":"Selected artist","Album":"Selected album",
                  "DurationMilliseconds":192123,"CandidateIsrc":"USRC17607839"}]
                """
        };
        var row = Row(source, decision, [source, apple, alias]);

        Assert.Empty(row.GetProperty("candidates").EnumerateArray());
        var target = row.GetProperty("target");
        Assert.Equal("apple-musickit", target.GetProperty("providerId").GetString());
        Assert.Equal(apple.ExternalId, target.GetProperty("externalId").GetString());
        Assert.Equal("Selected title", target.GetProperty("title").GetString());
        Assert.Equal("Selected artist", target.GetProperty("artist").GetString());
        Assert.Equal("Selected album", target.GetProperty("album").GetString());
        Assert.Equal(192123, target.GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal("USRC17607839", target.GetProperty("isrc").GetString());
        Assert.Equal("/api/admin/downloads/artwork/ext-apple-musickit-song-6792960885",
            target.GetProperty("artworkUrl").GetString());
    }

    [Theory]
    [InlineData(TrackMatchState.Accepted)]
    [InlineData(TrackMatchState.Unresolved)]
    public void Existing_provider_route_keeps_target_when_new_local_decision_has_no_candidates(TrackMatchState state)
    {
        var recording = Guid.NewGuid();
        var source = Identity(recording, "spotify", "source");
        source.VerificationMethod = "source-snapshot-hash";
        var apple = Identity(recording, "apple-download", "6792960885");
        var row = Row(source, new TrackMatchRecord
        {
            CanonicalRecordingId = recording,
            State = state,
            CandidateResultsJson = "[]"
        }, [source, apple, Identity(recording, "apple-musickit", apple.ExternalId)]);

        Assert.Empty(row.GetProperty("candidates").EnumerateArray());
        Assert.Equal("accepted", row.GetProperty("state").GetString());
        Assert.Single(row.GetProperty("providerIdentities").EnumerateArray(),
            item => item.GetProperty("providerId").GetString() == "apple-musickit");
        Assert.Equal("apple-musickit", row.GetProperty("target").GetProperty("providerId").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("target").GetProperty("title").ValueKind);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task Empty_candidates_use_canonical_metadata_before_cached_provider_metadata()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var reads = new QueryCounter();
        var factory = new DbFactory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .AddInterceptors(reads).Options);
        var owner = Guid.NewGuid();
        var recording = Guid.NewGuid();
        var previousRecording = Guid.NewGuid();
        var source = Identity(recording, "spotify", "source");
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new UserRecord
            {
                Id = owner,
                DisplayName = "Owner",
                Enabled = true,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "owner"
            });
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = recording,
                CreatedByUserId = owner,
                Title = "Canonical title",
                DurationMilliseconds = 190123,
                Identifiers = [new() { Id = Guid.NewGuid(), RecordingId = recording,
                    Kind = RecordingIdentifierKinds.Isrc, Value = "USRC17607839", Source = "fixture" }]
            });
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = previousRecording,
                CreatedByUserId = owner,
                Title = "Previous target"
            });
            db.ProviderTrackIdentities.Add(source);
            db.ProviderTrackIdentities.Add(Identity(recording, "apple-musickit", "6792960885"));
            db.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                ProviderTrackIdentityId = source.Id,
                ProviderId = "spotify",
                ResourceKind = "track",
                ExternalIdHash = source.ExternalIdHash,
                SnapshotVersion = 1,
                ProviderRevision = "1",
                PayloadSha256 = new string('a', 64),
                PayloadJson = """{"Title":"Snapshot title","Artists":["Stored artist"],"Album":"Stored album"}"""
            });
            await db.SaveChangesAsync();
        }
        var cache = new TestMemoryApplicationCache();
        await cache.SetAsync(CacheKeyBuilder.BuildPlaybackMetadataKey("apple-download", "6792960885"),
            new
            {
                Metadata = new PlaybackTrackMetadata("Cached title", "Cached artist", "Cached album", null, 200),
                FreshUntil = DateTimeOffset.UtcNow.AddMinutes(1)
            });
        var targets = Enumerable.Range(0, 50).Select(_ => new TrackMatchTarget("apple-musickit", "6792960885")).ToArray();
        targets[0].Title = "Selected title";
        reads.Count = 0;
        await new TrackMatchTargetResolver(factory, cache).EnrichAsync(new(owner, false),
            targets.Select((target, index) => new TrackMatchTargetLookup(index == 2 ? previousRecording : recording, owner, target))
                .ToArray(), default);

        Assert.Equal("Selected title", targets[0].Title);
        Assert.Equal("Canonical title", targets[2].Title);
        var target = targets[1];
        Assert.Equal("Canonical title", target.Title);
        Assert.Equal("Stored artist", target.Artist);
        Assert.Equal("Cached album", target.Album);
        Assert.Equal(190123, target.DurationMilliseconds);
        Assert.Equal("USRC17607839", target.Isrc);
        Assert.InRange(reads.Count, 1, 3);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task Entirely_missing_metadata_queues_existing_enrichment_once_and_returns_known_identity()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new DbFactory(database.Options);
        var owner = Guid.NewGuid();
        var recording = Guid.NewGuid();
        var identity = Identity(recording, "apple-musickit", "6792960885");
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new UserRecord
            {
                Id = owner,
                DisplayName = "Owner",
                Enabled = true,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "owner"
            });
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord { Id = recording, CreatedByUserId = owner });
            db.ProviderTrackIdentities.Add(identity);
            await db.SaveChangesAsync();
        }
        var clock = new SystemPlatformClock();
        var options = new DurableJobOptions();
        var jobs = new DurableJobQueue(factory, options, new JobPayloadPolicy(options), clock,
            new DurableJobContextAuthorizer(factory));
        var enrichment = new RecordingOdesliEnrichmentQueue(jobs, clock, NullLogger<RecordingOdesliEnrichmentQueue>.Instance);
        var target = new TrackMatchTarget(identity.ProviderId, identity.ExternalId);
        var resolver = new TrackMatchTargetResolver(factory, enrichment: enrichment);
        var lookups = new[] { new TrackMatchTargetLookup(recording, owner, target), new(recording, owner, target) };

        await resolver.EnrichAsync(new(owner, false), lookups, default);
        await resolver.EnrichAsync(new(owner, false), lookups, default);

        await using var saved = factory.CreateDbContext();
        var job = Assert.Single(await saved.Jobs.ToArrayAsync());
        Assert.Equal(RecordingOdesliEnrichmentQueue.JobType, job.Type);
        Assert.Equal(owner, job.OwnerUserId);
        Assert.Equal(identity.Id, JsonSerializer.Deserialize<RecordingOdesliPayload>(job.PayloadJson)!.SourceIdentityId);
        Assert.Equal(identity.ExternalId, target.ExternalId);
        Assert.Null(target.Title);
        Assert.Null(target.Artist);
    }

    [Fact]
    public async Task Missing_canonical_metadata_uses_cached_Apple_alias_without_provider_calls()
    {
        var cache = new TestMemoryApplicationCache();
        await cache.SetAsync(CacheKeyBuilder.BuildPlaybackMetadataKey("apple-download", "6792960885"),
            new
            {
                Metadata = new PlaybackTrackMetadata("Cached title", "Cached artist", "Cached album", null, 201),
                FreshUntil = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
        var factory = new Moq.Mock<IDbContextFactory<AllstarrDbContext>>(Moq.MockBehavior.Strict);
        var owner = Guid.NewGuid();
        var target = new TrackMatchTarget("apple-musickit", "6792960885");

        await new TrackMatchTargetResolver(factory.Object, cache).EnrichAsync(new(owner, false),
            [new(null, owner, target)], default);

        Assert.Equal("Cached title", target.Title);
        Assert.Equal("Cached artist", target.Artist);
        Assert.Equal("Cached album", target.Album);
        Assert.Equal(201000, target.DurationMilliseconds);
        factory.VerifyNoOtherCalls();
    }

    private static ProviderTrackIdentityRecord Identity(Guid recording, string provider, string externalId) => new()
    {
        Id = Guid.NewGuid(),
        CanonicalRecordingId = recording,
        ProviderId = provider,
        ExternalId = externalId,
        ExternalIdHash = new string(provider == "spotify" ? 'a' : 'b', 64),
        ResourceKind = ProviderResourceKind.Track,
        Scope = ProviderIdentityScope.Catalog,
        DecisionVersion = 1,
        Verification = ProviderIdentityVerification.Verified,
        VerificationMethod = "automatic-match"
    };

    private static JsonElement Row(ProviderTrackIdentityRecord source, TrackMatchRecord decision,
        ProviderTrackIdentityRecord[] identities)
    {
        var row = typeof(TrackMatchesController).GetMethod("Row", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new ExternalMetadataSnapshotRecord
            {
                Id = Guid.NewGuid(), ProviderId = "spotify", ProviderTrackIdentityId = source.Id,
                ExternalIdHash = source.ExternalIdHash, PayloadJson = "{}"
            }, decision, new ManualTrackOverrideLayers(null, null), source,
                new Dictionary<Guid, LibraryTrackRecord>(),
                new Dictionary<Guid, ProviderTrackIdentityRecord[]> { [source.CanonicalRecordingId] = identities },
                new[] { "apple-musickit" }, Guid.NewGuid(), true, null, null])!;
        return JsonSerializer.SerializeToElement(row.GetType().GetProperty("Value")!.GetValue(row),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
