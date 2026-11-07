using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryIndexServiceTests
{
    [Fact]
    public async Task JellyfinCleanupRotatesPastDeferredGroupsOnLaterMaintenancePasses()
    {
        _clock.UtcNow = DateTimeOffset.UnixEpoch;
        await using (var db = _factory.CreateDbContext())
        {
            for (var index = 0; index < 51; index++)
            {
                foreach (var owner in new[] { _userA, _userB })
                {
                    var row = DuplicateTrack(Guid.NewGuid(), owner, "/music/song.flac", _clock.UtcNow, "{}");
                    row.BackendItemId = $"item-{index:000}";
                    if (index < 50)
                    {
                        row.CanonicalRecordingId = Guid.NewGuid();
                        db.CanonicalRecordings.Add(new() { Id = row.CanonicalRecordingId.Value, CreatedByUserId = owner });
                    }
                    db.LibraryTracks.Add(row);
                }
            }
            await db.SaveChangesAsync();
        }

        Assert.Equal(new LibraryIndexDuplicateSummary(0, 0, 50), await _service.ConsolidateJellyfinDuplicatesAsync());
        _clock.UtcNow = _clock.UtcNow.AddMinutes(15);
        Assert.Equal(new LibraryIndexDuplicateSummary(1, 1, 49), await _service.ConsolidateJellyfinDuplicatesAsync());
    }

    [Fact]
    public async Task JellyfinSelfLoveDuplicateFixtureConsolidatesThreeObservationsIntoOneItem()
    {
        var thirdOwner = Guid.NewGuid();
        DateTimeOffset[] observations =
        [
            new(2026, 10, 9, 1, 59, 0, TimeSpan.Zero),
            new(2026, 10, 9, 2, 13, 0, TimeSpan.Zero),
            new(2026, 11, 2, 8, 44, 0, TimeSpan.Zero)
        ];
        var rows = new[] { _userA, _userB, thirdOwner }.Select((owner, index) =>
        {
            var row = SelfLoveMatchFixture.Local(owner, "backend");
            row.IndexedAt = observations[index];
            row.SourceModifiedAt = observations[index];
            row.UpdatedAt = observations[index];
            return row;
        }).ToArray();
        await using (var db = _factory.CreateDbContext())
        {
            db.Users.Add(User(thirdOwner, "C", _clock.UtcNow));
            db.LibraryTracks.AddRange(rows);
            await db.SaveChangesAsync();
            Assert.Single(await db.LibraryTracks.GroupBy(item => new { item.Protocol, item.BackendInstanceId, item.BackendItemId })
                .Where(group => group.Count() > 1).Select(group => group.Key).ToArrayAsync());
        }

        Assert.Equal(new LibraryIndexDuplicateSummary(1, 2, 0), await _service.ConsolidateJellyfinDuplicatesAsync());

        await using var saved = _factory.CreateDbContext();
        var survivor = Assert.Single(await saved.LibraryTracks.ToArrayAsync());
        Assert.Equal(SelfLoveMatchFixture.BackendItemId, survivor.BackendItemId);
        Assert.Equal(observations[2], survivor.IndexedAt);
        Assert.Equal(2, await saved.AuditEvents.CountAsync(item => item.Action == "track.consolidated"));
        Assert.Equal(new LibraryIndexDuplicateSummary(0, 0, 0), await _service.ConsolidateJellyfinDuplicatesAsync());
    }

    [Fact]
    public async Task JellyfinConcurrentFirstObservationsCreateOnlyOneSharedRow()
    {
        _libraryAccess.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));

        var results = await Task.WhenAll(
            _service.UpsertAsync(Context(_userA, "principal-a"), Input()),
            _service.UpsertAsync(Context(_userB, "principal-b"), Input()));

        Assert.Equal(results[0].Id, results[1].Id);
        await using var saved = _factory.CreateDbContext();
        Assert.Single(await saved.LibraryTracks.ToArrayAsync());
    }

    [Fact]
    public async Task JellyfinFreshObservationRepairsAnOlderLibraryLocationWithoutChangingTheItem()
    {
        var original = await _service.UpsertAsync(Context(_userA, "principal-a"), Input());
        _libraryAccess.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["other"]));

        var moved = await _service.UpsertAsync(Context(_userB, "principal-b"), Input() with
        { BackendLibraryId = "other", FilePath = "/other/song.flac" });

        Assert.Equal(original.Id, moved.Id);
        Assert.Empty(await _service.ListAsync(Context(_userA, "principal-a"), "music"));
        Assert.Single(await _service.ListAsync(Context(_userB, "principal-b"), "other"));
        await using var saved = _factory.CreateDbContext();
        Assert.Single(await saved.LibraryTracks.ToArrayAsync());
    }

    [Fact]
    public async Task JellyfinUpsert_ReusesOneIdentityAcrossUsersAndCatalogChanges()
    {
        _libraryAccess.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));

        var first = await _service.UpsertAsync(Context(_userA, "principal-a"), Input());
        var second = await _service.UpsertAsync(Context(_userB, "principal-b"), Input() with
        {
            FilePath = "/moved/song.flac",
            ProviderTrackIds = new Dictionary<string, string> { ["spotify"] = "new-provider-id" }
        });

        Assert.Equal(first.Id, second.Id);
        await using var db = await _factory.CreateDbContextAsync();
        var row = Assert.Single(await db.LibraryTracks.ToListAsync());
        Assert.Equal(_userA, row.OwnerUserId);
        Assert.Equal("/moved/song.flac", row.FilePath);
        Assert.Equal("new-provider-id", JsonSerializer.Deserialize<Dictionary<string, string>>(row.ProviderIdsJson)!["spotify"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinDuplicateCleanup_RepointsReferencesAndIsIdempotent(bool unknownReference)
    {
        var winnerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var loserId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var snapshotId = Guid.CreateVersion7();
        var now = _clock.UtcNow;
        var hash = new string('a', 64);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.LibraryTracks.AddRange(
                DuplicateTrack(winnerId, _userA, "/music/winner.flac", now, "{\"spotify\":\"winner\"}"),
                DuplicateTrack(loserId, _userB, "/music/loser.flac", now.AddMinutes(1), "{\"deezer\":\"loser\"}"));
            db.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
            {
                Id = snapshotId,
                OwnerUserId = _userB,
                BackendInstanceId = "backend",
                BackendPrincipalId = "principal-b",
                Protocol = "jellyfin",
                ProviderId = "deezer",
                ResourceKind = "track",
                ExternalIdHash = hash,
                SnapshotVersion = 1,
                ProviderRevision = "1",
                PayloadJson = "{}",
                PayloadSha256 = hash,
                CorrelationId = "fixture",
                RetrievedAt = now
            });
            db.TrackMatches.Add(new TrackMatchRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userB,
                ExternalSnapshotId = snapshotId,
                LibraryTrackId = loserId,
                State = TrackMatchState.Pinned,
                Confidence = 1,
                Threshold = 0.5,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                LibraryIndexRevision = 1,
                PolicyVersion = "fixture",
                CandidateResultsJson = JsonSerializer.Serialize(new[]
                {
                    new { libraryTrackId = loserId, confidence = 0.9, isLocal = true, backendItemId = "local-item" },
                    new { libraryTrackId = winnerId, confidence = 0.8, isLocal = true, backendItemId = "local-item" },
                    new { libraryTrackId = Guid.Empty, confidence = 0.9, isLocal = false, backendItemId = "first-fallback" },
                    new { libraryTrackId = Guid.Empty, confidence = 0.9, isLocal = false, backendItemId = "second-fallback" }
                }),
                ReasonsJson = "[]",
                WarningsJson = "[]",
                CorrelationId = "fixture",
                DecidedAt = now
            });
            db.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userB,
                ExternalSnapshotId = snapshotId,
                SourceProviderId = "deezer",
                SourceExternalIdHash = hash,
                LibraryTrackId = loserId,
                Decision = ManualOverrideDecision.Pin,
                Reason = "fixture",
                DecisionVersion = 1,
                MatcherVersion = "fixture",
                CreatedAt = now,
                Revision = 9
            });
            db.ListeningEvents.Add(new ListeningEventRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userB,
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                OccurrenceKey = "fixture-occurrence",
                State = ListeningEventState.Completed,
                TrackReference = "local-item",
                LibraryTrackId = loserId,
                UpdatedAt = now,
                ListenedAt = now
            });
            db.ProblemReports.Add(new ProblemReportRecord
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = _userB,
                RequestId = Guid.CreateVersion7(),
                RequestFingerprint = hash,
                Kind = "wrong-match",
                Note = "fixture",
                ReporterName = "fixture",
                ContextJson = unknownReference
                    ? JsonSerializer.Serialize(new { schemaVersion = 1, song = new { savedReference = loserId } })
                    : JsonSerializer.Serialize(new { schemaVersion = 1, song = new { libraryTrackId = loserId } }),
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var first = await _service.ConsolidateJellyfinDuplicatesAsync();
        if (unknownReference)
        {
            Assert.Equal(new LibraryIndexDuplicateSummary(0, 0, 1), first);
            await using (var db = _factory.CreateDbContext())
            {
                Assert.Equal(2, await db.LibraryTracks.CountAsync());
                Assert.Equal(loserId, (await db.ManualTrackOverrides.SingleAsync()).LibraryTrackId);
                Assert.Equal(loserId, (await db.TrackMatches.SingleAsync()).LibraryTrackId);
                Assert.Contains(loserId.ToString("D"), (await db.TrackMatches.SingleAsync()).CandidateResultsJson);
                var pendingReport = await db.ProblemReports.SingleAsync();
                pendingReport.ContextJson = pendingReport.ContextJson.Replace("savedReference", "libraryTrackId");
                await db.SaveChangesAsync();
            }
            first = await _service.ConsolidateJellyfinDuplicatesAsync();
        }
        var second = await _service.ConsolidateJellyfinDuplicatesAsync();

        Assert.Equal(new LibraryIndexDuplicateSummary(1, 1, 0), first);
        Assert.Equal(new LibraryIndexDuplicateSummary(0, 0, 0), second);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Single(await verification.LibraryTracks.ToListAsync());
        Assert.Equal(winnerId, (await verification.TrackMatches.SingleAsync()).LibraryTrackId);
        var manual = await verification.ManualTrackOverrides.SingleAsync();
        Assert.Equal(winnerId, manual.LibraryTrackId);
        Assert.Equal(ManualOverrideDecision.Pin, manual.Decision);
        Assert.Equal("fixture", manual.Reason);
        Assert.Equal(9, manual.Revision);
        Assert.Equal(1, manual.DecisionVersion);
        Assert.Equal(winnerId, (await verification.ListeningEvents.SingleAsync()).LibraryTrackId);
        var match = await verification.TrackMatches.SingleAsync();
        Assert.Contains(winnerId.ToString("D"), match.CandidateResultsJson);
        Assert.DoesNotContain(loserId.ToString("D"), match.CandidateResultsJson);
        using var candidates = JsonDocument.Parse(match.CandidateResultsJson);
        Assert.Equal(3, candidates.RootElement.GetArrayLength());
        Assert.Single(candidates.RootElement.EnumerateArray(), item => item.GetProperty("isLocal").GetBoolean());
        var report = await verification.ProblemReports.SingleAsync();
        Assert.Contains(winnerId.ToString("D"), report.ContextJson);
        Assert.DoesNotContain(loserId.ToString("D"), report.ContextJson);
        Assert.True(await verification.AuditEvents.AnyAsync(item => item.Action == "track.consolidated"));
    }

    [Fact]
    public async Task JellyfinCleanup_DefersItemsObservedInMultipleLibraries()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.LibraryTracks.AddRange(
                DuplicateTrack(Guid.CreateVersion7(), _userA, "/music/song.flac", _clock.UtcNow, "{}", "music"),
                DuplicateTrack(Guid.CreateVersion7(), _userB, "/other/song.flac", _clock.UtcNow, "{}", "other"));
            await db.SaveChangesAsync();
        }

        var summary = await _service.ConsolidateJellyfinDuplicatesAsync();

        Assert.Equal(1, summary.GroupsDeferred);
        Assert.Equal(0, summary.GroupsConsolidated);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verification.LibraryTracks.CountAsync());
    }

    [Fact]
    public async Task DuplicateCleanup_DoesNotConsolidateSubsonicUserScopedItems()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.LibraryTracks.AddRange(
                DuplicateTrack(Guid.CreateVersion7(), _userA, "/music/a.mp3", _clock.UtcNow, "{}", protocol: "subsonic"),
                DuplicateTrack(Guid.CreateVersion7(), _userB, "/music/b.mp3", _clock.UtcNow, "{}", protocol: "subsonic"));
            await db.SaveChangesAsync();
        }

        var summary = await _service.ConsolidateJellyfinDuplicatesAsync();

        Assert.Equal(new LibraryIndexDuplicateSummary(0, 0, 0), summary);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verification.LibraryTracks.CountAsync());
    }

    private LibraryTrackRecord DuplicateTrack(Guid id, Guid owner, string path, DateTimeOffset indexedAt,
        string providerIds, string library = "music", string protocol = "jellyfin") => new()
        {
            Id = id,
            OwnerUserId = owner,
            BackendLibraryId = library,
            Protocol = protocol,
            BackendInstanceId = "backend",
            BackendItemId = "local-item",
            FilePath = path,
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            AlbumArtist = "Artist",
            DurationMilliseconds = 240_000,
            DurationProvenance = "jellyfin",
            DurationRetrievedAt = indexedAt,
            ProviderIdsJson = providerIds,
            IndexedAt = indexedAt,
            SourceModifiedAt = indexedAt,
            UpdatedAt = indexedAt
        };
}

