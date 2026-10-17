using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class TrackMatchRefreshTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Silk & Cologne")]
    [InlineData("silk and cologne")]
    [InlineData("Spider Verse Remix")]
    [InlineData("beyonce")]
    [InlineData("Beyoncé")]
    [InlineData("EI8HT, Offset")]
    [Trait("Category", "Sqlite")]
    public async Task Review_search_normalizes_decoded_metadata_with_bounded_queries(string search)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Title = "Silk & Cologne (EI8HT & Offset) - Spider-Verse Remix",
            Artist = "EI8HT, Offset",
            Album = "Beyoncé"
        });
        await using var fixture = await Fixture.CreateAsync(payload);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            for (var index = 0; index < 799; index++)
                db.ExternalMetadataSnapshots.Add(Snapshot(fixture.Owner, null, $"other-{index}",
                    JsonSerializer.Serialize(new { Title = $"Other song {index}", Artist = "Other performer" }),
                    DateTimeOffset.UtcNow.AddMinutes(index + 1)));
            var otherOwner = Guid.NewGuid();
            db.ExternalMetadataSnapshots.Add(Snapshot(otherOwner, null, "private",
                payload, DateTimeOffset.UtcNow.AddDays(1)));
            db.Users.Add(new UserRecord
            {
                Id = otherOwner,
                DisplayName = "Other owner",
                Enabled = true
            });
            await db.SaveChangesAsync();
        }

        await fixture.Service.GetReviewDataAsync(new(fixture.Owner, false), search: search, scanLimit: 1);
        fixture.Reads.Count = 0;
        var elapsed = new List<double>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var timer = Stopwatch.StartNew();
            var review = await fixture.Service.GetReviewDataAsync(new(fixture.Owner, false), search: search, scanLimit: 1);
            timer.Stop();
            Assert.Equal(fixture.SnapshotId, Assert.Single(review.Snapshots).Id);
            elapsed.Add(timer.Elapsed.TotalMilliseconds);
        }
        var median = elapsed.Order().ElementAt(1);
        Assert.InRange(fixture.Reads.Count, 1, 36);
        Assert.InRange(median, 0, 200);
        output.WriteLine($"Search across 800 scoped rows: {median:F1} ms median; {fixture.Reads.Count / 3} reads");
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("missing")]
    [Trait("Category", "Sqlite")]
    public async Task Review_search_does_not_treat_punctuation_as_a_wildcard(string search)
    {
        await using var fixture = await Fixture.CreateAsync();
        var review = await fixture.Service.GetReviewDataAsync(new(fixture.Owner, false), search: search);
        Assert.Empty(review.Snapshots);
    }

    [Theory]
    [InlineData("none", false, true)]
    [InlineData("suggested", false, true)]
    [InlineData("accepted", false, true)]
    [InlineData("none", true, true)]
    [InlineData("none", false, false)]
    [Trait("Category", "Sqlite")]
    public async Task Local_refresh_preserves_provider_evidence_and_only_replaces_local_evidence(string localMatch, bool legacy, bool verified)
    {
        await using var fixture = await Fixture.CreateAsync();
        var providerScore = new TrackMatchCandidateScore(legacy ? Guid.Empty : Guid.NewGuid(), "external-deezer-target", .99,
            ["title_exact", "artist_exact"], [], Title: "Track Melodies", Artist: "Artist", Album: "Album",
            ProviderTrackIds: new Dictionary<string, string> { ["deezer"] = "target" }, IsLocal: legacy);
        var oldLocalScore = providerScore with
        {
            LibraryTrackId = Guid.NewGuid(),
            BackendItemId = "old-local",
            ProviderTrackIds = null,
            IsLocal = true
        };
        var localId = Guid.NewGuid();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            if (!verified)
                db.ProviderTrackIdentities.Remove(await db.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "deezer"));
            db.TrackMatches.Add(new TrackMatchRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = fixture.Owner,
                ExternalSnapshotId = fixture.SnapshotId,
                CanonicalRecordingId = fixture.Recording,
                State = TrackMatchState.Accepted,
                Confidence = .99,
                Threshold = .88,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                MatcherVersion = "previous",
                PolicyVersion = "provider-match",
                CandidateResultsJson = JsonSerializer.Serialize(new[] { providerScore, oldLocalScore }),
                ReasonsJson = "[\"provider_verified\"]",
                WarningsJson = "[]"
            });
            if (localMatch != "none")
                db.LibraryTracks.Add(new LibraryTrackRecord
                {
                    Id = localId,
                    OwnerUserId = fixture.Owner,
                    BackendInstanceId = "backend",
                    BackendLibraryId = "music",
                    BackendItemId = "new-local",
                    Title = localMatch == "suggested" ? "Track Memories" : "Track Melodies",
                    Artist = "Artist",
                    Album = localMatch == "accepted" ? "Album" : null,
                    DurationMilliseconds = localMatch == "accepted" ? 180000 : null,
                    Protocol = "jellyfin"
                });
            await db.SaveChangesAsync();
        }

        var result = Assert.Single(await fixture.Service.MatchSourceTracksAsync(
            [new("spotify", "source", "Track Melodies", "Artist", "Album", 180000, null, null, "1")], "refresh"));
        await using var saved = fixture.Factory.CreateDbContext();
        var latest = await saved.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        var evidence = JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(latest.CandidateResultsJson)!;
        Assert.Equal(JsonSerializer.Serialize(providerScore), JsonSerializer.Serialize(
            Assert.Single(evidence, item => item.BackendItemId == providerScore.BackendItemId)));
        Assert.DoesNotContain(evidence, item => item.BackendItemId == "old-local");
        Assert.Equal(localMatch == "none" ? 1 : 2, evidence.Length);
        Assert.Equal(verified ? TrackMatchState.Accepted : TrackMatchState.Unresolved, latest.State);
        Assert.Equal(verified ? TrackMatchReviewState.Accepted : TrackMatchReviewState.Unresolved, result.State);
        if (localMatch == "accepted")
        {
            Assert.Equal(localId, latest.LibraryTrackId);
            Assert.Equal("new-local", result.LocalBackendItemId);
        }
        else
        {
            Assert.Null(latest.LibraryTrackId);
            Assert.Null(result.LocalBackendItemId);
            if (localMatch == "suggested")
            {
                var suggestion = Assert.Single(evidence, item => item.IsLocal);
                Assert.InRange(suggestion.Confidence, .72, .8799);
                Assert.Equal(1, suggestion.Components!["artist"]);
                Assert.Null((await saved.LibraryTracks.SingleAsync(item => item.Id == localId)).CanonicalRecordingId);
                var identities = await saved.ProviderTrackIdentities.ToArrayAsync();
                var projection = TrackRouteProjector.Project(await saved.ExternalMetadataSnapshots.SingleAsync(),
                    latest, null, identities.Single(item => item.ProviderId == "spotify"),
                    await saved.LibraryTracks.ToArrayAsync(), identities, ["deezer"]);
                Assert.Null(projection.LibraryTrack);
                Assert.Equal("deezer", projection.PrimaryProviderRoute!.ProviderId);
            }
            if (verified)
            {
                Assert.Equal(.99, latest.Confidence);
                Assert.Equal("[\"provider_verified\"]", latest.ReasonsJson);
                Assert.Equal("[]", latest.WarningsJson);
            }
        }

        if (localMatch != "accepted")
        {
            var version = latest.DecisionVersion;
            await fixture.Service.MatchSourceTracksAsync(
                [new("spotify", "source", "Track Melodies", "Artist", "Album", 180000, null, null, "1")], "repeat");
            Assert.Equal(version, await saved.TrackMatches.MaxAsync(item => item.DecisionVersion));
        }
    }

    private static ExternalMetadataSnapshotRecord Snapshot(Guid owner, Guid? identity, string externalId,
        string payload, DateTimeOffset retrievedAt) => new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderTrackIdentityId = identity,
            ProviderId = "spotify",
            ResourceKind = "track",
            BackendInstanceId = "backend",
            ExternalIdHash = Hash(externalId),
            SnapshotVersion = 1,
            ProviderRevision = "1",
            PayloadJson = payload,
            PayloadSha256 = Hash(payload),
            RetrievedAt = retrievedAt
        };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class Fixture(SqliteTestDatabase database) : IAsyncDisposable
    {
        public QueryCounter Reads { get; } = new();
        public DbFactory Factory { get; private set; } = null!;
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid Recording { get; } = Guid.NewGuid();
        public Guid SnapshotId { get; private set; }
        public TrackMatchCommandService Service { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(string? payload = null)
        {
            var fixture = new Fixture(await SqliteTestDatabase.CreateAsync());
            fixture.Factory = new(new DbContextOptionsBuilder<AllstarrDbContext>(fixture.DatabaseOptions)
                .AddInterceptors(fixture.Reads).Options);
            await using var db = fixture.Factory.CreateDbContext();
            db.Users.Add(new UserRecord
            {
                Id = fixture.Owner,
                DisplayName = "Owner",
                Enabled = true,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "owner"
            });
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord { Id = fixture.Recording, CreatedByUserId = fixture.Owner });
            var source = new ProviderTrackIdentityRecord
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = fixture.Recording,
                ProviderId = "spotify",
                ExternalId = "source",
                ExternalIdHash = Hash("source"),
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "source-snapshot-hash",
                DecisionVersion = 1
            };
            db.ProviderTrackIdentities.Add(source);
            db.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = fixture.Recording,
                ProviderId = "deezer",
                ExternalId = "target",
                ExternalIdHash = Hash("target"),
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "automatic-match",
                DecisionVersion = 1
            });
            var snapshot = Snapshot(fixture.Owner, source.Id, "source",
                payload ?? "{\"Title\":\"Track Melodies\",\"Artist\":\"Artist\"}", DateTimeOffset.UtcNow);
            fixture.SnapshotId = snapshot.Id;
            db.ExternalMetadataSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
            fixture.Service = new(fixture.Factory, new TrackMatchDecisionEngine(),
                new ProviderAccountResolver(fixture.Factory), new Clock(), new TestBackendLibraryAccess(fixture.Factory, "music"));
            return fixture;
        }

        private DbContextOptions<AllstarrDbContext> DatabaseOptions => database.Options;
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

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
