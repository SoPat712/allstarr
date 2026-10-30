using System.Data.Common;
using System.Diagnostics;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

    [Theory]
    [InlineData(ProviderUseMode.Automatic, true)]
    [InlineData(ProviderUseMode.ManualOnly, false)]
    [InlineData(ProviderUseMode.Off, false)]
    [Trait("Category", "Sqlite")]
    public async Task Automatic_refresh_uses_only_cross_provider_aliases_allowed_by_owner_policy(
        ProviderUseMode mode, bool expectedMatch)
    {
        await using var fixture = await Fixture.CreateAsync(policyTargetMode: mode);
        var localId = await AddCrossProviderAliasCandidateAsync(fixture);

        var result = Assert.Single(await fixture.Service.MatchSourceTracksAsync(
            [new("spotify", "source", "Source title", "Source artist", null, 180000, null, null, "1")], "alias-policy"));

        Assert.Equal(expectedMatch ? TrackMatchReviewState.Accepted : TrackMatchReviewState.Unresolved, result.State);
        await using var saved = fixture.Factory.CreateDbContext();
        var latest = await saved.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        Assert.Equal(expectedMatch ? (Guid?)localId : null, latest.LibraryTrackId);
        if (!expectedMatch)
            Assert.DoesNotContain(JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(latest.CandidateResultsJson)!,
                item => item.Reasons.Contains("odesli_alias_exact"));
    }

    [Theory]
    [InlineData(ProviderUseMode.Automatic, true)]
    [InlineData(ProviderUseMode.ManualOnly, false)]
    [InlineData(ProviderUseMode.Off, false)]
    [Trait("Category", "Sqlite")]
    public async Task Rematch_uses_only_cross_provider_aliases_allowed_by_owner_policy(
        ProviderUseMode mode, bool expectedMatch)
    {
        await using var fixture = await Fixture.CreateAsync(policyTargetMode: mode);
        var localId = await AddCrossProviderAliasCandidateAsync(fixture);

        var result = await fixture.Service.RematchSnapshotAsync(
            new TrackMatchActor(fixture.Owner, false), fixture.SnapshotId, "alias-policy");

        Assert.True(result.Succeeded);
        Assert.Equal(expectedMatch ? TrackMatchReviewState.Accepted.ToString().ToLowerInvariant() :
            TrackMatchReviewState.Unresolved.ToString().ToLowerInvariant(), result.State);
        await using var saved = fixture.Factory.CreateDbContext();
        var latest = await saved.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        Assert.Equal(expectedMatch ? (Guid?)localId : null, latest.LibraryTrackId);
    }

    private static async Task<Guid> AddCrossProviderAliasCandidateAsync(Fixture fixture)
    {
        var localId = Guid.NewGuid();
        await using var db = fixture.Factory.CreateDbContext();
        db.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
        {
            Id = Guid.NewGuid(),
            CanonicalRecordingId = fixture.Recording,
            ProviderId = "qobuz",
            ExternalId = "qobuz-target",
            ExternalIdHash = Hash("qobuz-target"),
            ResourceKind = ProviderResourceKind.Track,
            Scope = ProviderIdentityScope.Catalog,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "cross-service-match",
            DecisionVersion = 1
        });
        db.LibraryTracks.Add(new LibraryTrackRecord
        {
            Id = localId,
            OwnerUserId = fixture.Owner,
            BackendInstanceId = "backend",
            BackendLibraryId = "music",
            BackendItemId = "qobuz-local",
            Title = "Unrelated title",
            Artist = "Unrelated artist",
            DurationMilliseconds = 180000,
            ProviderIdsJson = "{\"qobuz\":\"qobuz-target\"}",
            Protocol = "jellyfin"
        });
        await db.SaveChangesAsync();
        return localId;
    }

    public static IEnumerable<object?[]> StoredExplicitCases()
    {
        foreach (var branch in new[] { false, true })
            foreach (var (field, raw, expected) in new (string, string, bool?)[]
            {
                ("IsExplicit", "true", true), ("isExplicit", "true", true),
                ("IsExplicit", "false", false), ("isExplicit", "false", false),
                ("IsExplicit", "null", null), ("isExplicit", "\"true\"", null),
                ("IsExplicit", "1", true),
                ("ExplicitContentLyrics", "1", true), ("explicitContentLyrics", "3", false),
                ("ExplicitContentLyrics", "2", null), ("", "null", null)
            })
                yield return [branch, field, raw, expected];
    }

    [Theory]
    [InlineData(TrackMatchState.Unresolved, false)]
    [InlineData(TrackMatchState.Suggested, false)]
    [InlineData(TrackMatchState.Ambiguous, false)]
    [InlineData(TrackMatchState.Accepted, false)]
    [InlineData(TrackMatchState.Unresolved, true)]
    [Trait("Category", "Sqlite")]
    public async Task AlgorithmRolloutSearchesAgainForNonAcceptedDecisions(TrackMatchState state, bool failure)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var cached = new Song { ExternalProvider = "deezer", ExternalId = "target", Title = "Track Melodies", Artist = "Artist" };
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "target")).ReturnsAsync(cached);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult([cached], []));
        if (failure)
            gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
                .ThrowsAsync(new ProtocolProviderUnavailableException(new(ProviderErrorKind.TransientFailure)));
        await using var fixture = await Fixture.CreateAsync(gateway: gateway.Object);
        await using (var setup = fixture.Factory.CreateDbContext())
        {
            setup.TrackMatches.Add(new TrackMatchRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = fixture.Owner,
                ExternalSnapshotId = fixture.SnapshotId,
                CanonicalRecordingId = fixture.Recording,
                State = state,
                Confidence = .94,
                Threshold = .88,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                MatcherVersion = "accepted-routing-v25",
                PolicyVersion = "previous",
                CandidateResultsJson = "[]",
                ReasonsJson = "[]",
                WarningsJson = "[]",
                CorrelationId = "previous",
                DecidedAt = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "owner",
            new AllstarrPrincipal(fixture.Owner, "jellyfin", "backend", "owner", "Owner", false),
            "refresh-search", DateTimeOffset.UtcNow.AddMinutes(1), default);

        if (failure)
        {
            await Assert.ThrowsAsync<ProtocolProviderUnavailableException>(() => fixture.Service.RematchSnapshotAsync(
                context, fixture.SnapshotId, "refresh-search", TrackRematchAllService.RolloutPolicyVersion));
            await using var unchanged = fixture.Factory.CreateDbContext();
            var previous = Assert.Single(await unchanged.TrackMatches.ToArrayAsync());
            Assert.Equal("accepted-routing-v25", previous.MatcherVersion);
            Assert.Equal(state, previous.State);
            return;
        }
        var result = await fixture.Service.RematchSnapshotAsync(context, fixture.SnapshotId, "refresh-search",
            TrackRematchAllService.RolloutPolicyVersion);

        Assert.True(result.Succeeded, result.Error);
        gateway.Verify(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15),
            state == TrackMatchState.Accepted ? Times.Never() : Times.Once());
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "target"),
            state == TrackMatchState.Accepted ? Times.Once() : Times.Never());
        await using var saved = fixture.Factory.CreateDbContext();
        var decision = await saved.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        Assert.Equal(TrackMatchState.Accepted, decision.State);
        Assert.Equal(TrackMatchDecisionEngine.AlgorithmVersion, decision.MatcherVersion);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task ManualRematchSavesUnresolvedDecisionWhenLowerPriorityProviderIsLate()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-download", "deezer"]);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "target"))
            .ReturnsAsync((Song?)null);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult([], ["deezer"]));
        await using var fixture = await Fixture.CreateAsync(gateway: gateway.Object);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "owner",
            new AllstarrPrincipal(fixture.Owner, "jellyfin", "backend", "owner", "Owner", false),
            "manual-search", DateTimeOffset.UtcNow.AddMinutes(1), default);

        var result = await fixture.Service.RematchSnapshotAsync(
            context, fixture.SnapshotId, "manual-search", "manual-rematch-v3");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("unresolved", result.State);
        gateway.Verify(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15),
            Times.AtLeastOnce());
        await using var saved = fixture.Factory.CreateDbContext();
        var decision = Assert.Single(await saved.TrackMatches.ToArrayAsync());
        Assert.Equal(TrackMatchState.Unresolved, decision.State);
        Assert.Equal("manual-rematch-v3", decision.PolicyVersion);
        Assert.Equal(TrackMatchDecisionEngine.AlgorithmVersion, decision.MatcherVersion);
    }

    [Theory]
    [MemberData(nameof(StoredExplicitCases))]
    [Trait("Category", "Sqlite")]
    public async Task RoutineCommandsReadStoredBooleanAndNumericExplicitEvidence(bool refresh, string field, string raw, bool? expected)
    {
        var payload = ExplicitPayload(field, raw);
        await using var fixture = await Fixture.CreateAsync(payload);
        var local = Local(fixture.Owner, "clean-local", "Track Melodies (Clean)");
        await using (var setup = fixture.Factory.CreateDbContext())
        {
            setup.LibraryTracks.Add(local);
            await setup.SaveChangesAsync();
        }

        await RunRoutineAsync(fixture, refresh);

        await using var saved = fixture.Factory.CreateDbContext();
        var decision = await saved.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        var score = Assert.Single(JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(decision.CandidateResultsJson)!);
        Assert.Equal(expected == true, score.Reasons.Contains("clean_edit_of_explicit"));
        if (expected == true) Assert.Equal(0, score.Confidence);
        else Assert.True(score.Confidence > 0);
        Assert.Equal(payload, (await saved.ExternalMetadataSnapshots.SingleAsync()).PayloadJson);
    }

    public static IEnumerable<object[]> UnusablePayloadCases() =>
        from refresh in new[] { false, true }
        from payload in new[] { "[]", "\"text\"", "42", "null" }
        select new object[] { refresh, payload };

    [Theory]
    [MemberData(nameof(UnusablePayloadCases))]
    [Trait("Category", "Sqlite")]
    public async Task RoutineCommandsKeepNonObjectPayloadEvidenceUnknown(bool refresh, string payload)
    {
        await using var fixture = await Fixture.CreateAsync(payload);
        await using (var setup = fixture.Factory.CreateDbContext())
        {
            setup.LibraryTracks.Add(Local(fixture.Owner, "clean-local", "Track Melodies (Clean)"));
            await setup.SaveChangesAsync();
        }

        await RunRoutineAsync(fixture, refresh);

        await using var saved = fixture.Factory.CreateDbContext();
        var decision = await saved.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        var scores = JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(decision.CandidateResultsJson)!;
        Assert.All(scores, score => Assert.DoesNotContain("clean_edit_of_explicit", score.Reasons));
        if (refresh) Assert.True(Assert.Single(scores).Confidence > 0);
        else Assert.Equal(TrackMatchState.Unresolved, decision.State);
        Assert.Equal(payload, (await saved.ExternalMetadataSnapshots.SingleAsync()).PayloadJson);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"IsExplicit\":true")]
    [Trait("Category", "Sqlite")]
    public async Task SnapshotCaptureRejectsInvalidJsonBeforeAnyMatchingWrite(string payload)
    {
        await using var fixture = await Fixture.CreateAsync();
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "owner",
            new AllstarrPrincipal(fixture.Owner, "jellyfin", "backend", "owner", "Owner", false),
            "invalid-payload", DateTimeOffset.UtcNow.AddMinutes(1), default);

        await Assert.ThrowsAnyAsync<JsonException>(() => fixture.Service.CaptureSnapshotAsync(context,
            new(null, "spotify", "track", Hash("invalid"), 1, "1", payload, Hash(payload))));

        await using var saved = fixture.Factory.CreateDbContext();
        Assert.Single(await saved.ExternalMetadataSnapshots.ToArrayAsync());
        Assert.Empty(await saved.TrackMatches.ToArrayAsync());
        Assert.Equal(2, await saved.ProviderTrackIdentities.CountAsync());
    }

    [Theory]
    [MemberData(nameof(StoredExplicitCases))]
    [Trait("Category", "Sqlite")]
    public async Task StoredExplicitEvidenceReachesExternalSearchAndCachedReuse(bool cached, string field, string raw, bool? expected)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var clean = new Song { ExternalProvider = "deezer", ExternalId = expected == false ? "clean" : "target", Title = "Track Melodies", Artist = "Artist", Album = "Album", Duration = 180, ExplicitContentLyrics = 0 };
        var explicitTrack = new Song { ExternalProvider = "deezer", ExternalId = expected == false ? "target" : "explicit", Title = clean.Title, Artist = clean.Artist, Album = clean.Album, Duration = 180, ExplicitContentLyrics = 1 };
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "target"))
            .ReturnsAsync(cached ? expected == false ? explicitTrack : clean : null);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .ReturnsAsync(new MatchingSongSearchResult([clean, explicitTrack], []));
        await using var fixture = await Fixture.CreateAsync(ExplicitPayload(field, raw), gateway: gateway.Object);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "owner",
            new AllstarrPrincipal(fixture.Owner, "jellyfin", "backend", "owner", "Owner", false),
            "stored-explicit", DateTimeOffset.UtcNow.AddMinutes(1), default);

        var result = await fixture.Service.RematchSnapshotAsync(context, fixture.SnapshotId, "stored-explicit", "manual-rematch-v3");

        Assert.True(result.Succeeded, result.Error);
        await using var saved = fixture.Factory.CreateDbContext();
        var decision = await saved.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        var scores = JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(decision.CandidateResultsJson)!;
        var cleanScore = Assert.Single(scores, item => item.BackendItemId == clean.ExternalId);
        Assert.Equal(expected == true, cleanScore.Reasons.Contains("clean_edit_of_explicit"));
        if (expected == true)
        {
            Assert.True(cleanScore.Confidence > 0);
            Assert.Equal(0, cleanScore.Components!["explicitContent"]);
            Assert.Equal(1, Assert.Single(scores, item => item.BackendItemId == explicitTrack.ExternalId).Confidence);
        }
        else
        {
            Assert.True(cleanScore.Confidence > 0);
            if (expected == false)
                Assert.Contains("clean_version_available", Assert.Single(scores, item => item.BackendItemId == explicitTrack.ExternalId).Reasons);
            if (!cached && expected == null)
                Assert.All(scores, score => Assert.True(score.Confidence > 0));
        }
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "target"), Times.Once);
        gateway.Verify(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15),
            cached && expected == null ? Times.Never() : Times.Once());
        gateway.Verify(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming), Times.Exactly(cached && expected == null ? 1 : 2));
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [Trait("Category", "Sqlite")]
    public async Task OldCandidateRejectionRemainsAuthoritativeForRoutineCommands(bool refresh, bool household, bool alternate)
    {
        await using var fixture = await Fixture.CreateAsync(ExplicitPayload("", "null"));
        var rejected = Local(fixture.Owner, "rejected", "Track Melodies");
        var other = Local(fixture.Owner, "other", "Track Melodies");
        var authority = new ManualTrackOverrideRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = household ? null : fixture.Owner,
            ExternalSnapshotId = fixture.SnapshotId,
            SourceProviderId = "spotify",
            SourceExternalIdHash = Hash("source"),
            LibraryTrackId = rejected.Id,
            Decision = ManualOverrideDecision.Reject,
            Reason = "Keep this candidate rejected",
            MatcherVersion = "accepted-routing-v21",
            DecisionVersion = 1,
            Revision = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await using (var setup = fixture.Factory.CreateDbContext())
        {
            setup.LibraryTracks.Add(rejected);
            if (alternate) setup.LibraryTracks.Add(other);
            setup.ManualTrackOverrides.Add(authority);
            await setup.SaveChangesAsync();
        }

        await RunRoutineAsync(fixture, refresh);

        await using var saved = fixture.Factory.CreateDbContext();
        var decision = await saved.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        Assert.Equal(alternate ? TrackMatchState.Accepted : TrackMatchState.Rejected, decision.State);
        Assert.Equal(alternate ? (Guid?)other.Id : null, decision.LibraryTrackId);
        Assert.DoesNotContain(JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(decision.CandidateResultsJson)!, item => item.LibraryTrackId == rejected.Id);
        var retained = Assert.Single(await saved.ManualTrackOverrides.ToArrayAsync());
        Assert.Equal(authority.Id, retained.Id);
        Assert.Equal("accepted-routing-v21", retained.MatcherVersion);
        Assert.Null(retained.RevokedAt);
        Assert.Equal(1, retained.Revision);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Sqlite")]
    public async Task OldRejectionCannotCrossItsPersonalOwnerOrSource(bool refresh, bool otherSource)
    {
        await using var fixture = await Fixture.CreateAsync(ExplicitPayload("", "null"));
        var candidate = Local(fixture.Owner, "valid", "Track Melodies");
        var otherOwner = Guid.NewGuid();
        await using (var setup = fixture.Factory.CreateDbContext())
        {
            setup.Users.Add(new UserRecord { Id = otherOwner, BackendType = "jellyfin", BackendInstanceId = "backend", BackendPrincipalId = "other", DisplayName = "Other", Enabled = true });
            setup.LibraryTracks.Add(candidate);
            setup.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = otherSource ? fixture.Owner : otherOwner,
                ExternalSnapshotId = fixture.SnapshotId,
                SourceProviderId = "spotify",
                SourceExternalIdHash = Hash(otherSource ? "other" : "source"),
                LibraryTrackId = candidate.Id,
                Decision = ManualOverrideDecision.Reject,
                MatcherVersion = "accepted-routing-v22",
                DecisionVersion = 1,
                Reason = "Scoped rejection",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        await RunRoutineAsync(fixture, refresh);

        await using var saved = fixture.Factory.CreateDbContext();
        var decision = await saved.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstAsync();
        Assert.Equal(TrackMatchState.Accepted, decision.State);
        Assert.Equal(candidate.Id, decision.LibraryTrackId);
        Assert.Single(await saved.ManualTrackOverrides.Where(item => item.RevokedAt == null).ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Sqlite")]
    public async Task OldPersonalPinOrSourceRejectionKeepsPrecedenceOverHouseholdCandidateRejection(bool refresh, bool sourceRejection)
    {
        await using var fixture = await Fixture.CreateAsync(ExplicitPayload("", "null"));
        var rejected = Local(fixture.Owner, "household-rejected", "Track Melodies");
        var chosen = Local(fixture.Owner, "personal-chosen", "Track Melodies");
        var personalId = Guid.NewGuid();
        await using (var setup = fixture.Factory.CreateDbContext())
        {
            setup.LibraryTracks.AddRange(rejected, chosen);
            setup.ManualTrackOverrides.AddRange(
                new ManualTrackOverrideRecord
                {
                    Id = Guid.NewGuid(),
                    ExternalSnapshotId = fixture.SnapshotId,
                    SourceProviderId = "spotify",
                    SourceExternalIdHash = Hash("source"),
                    LibraryTrackId = rejected.Id,
                    Decision = ManualOverrideDecision.Reject,
                    MatcherVersion = "accepted-routing-v22",
                    DecisionVersion = 1,
                    Reason = "Household choice",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new ManualTrackOverrideRecord
                {
                    Id = personalId,
                    OwnerUserId = fixture.Owner,
                    ExternalSnapshotId = fixture.SnapshotId,
                    SourceProviderId = "spotify",
                    SourceExternalIdHash = Hash("source"),
                    LibraryTrackId = sourceRejection ? null : chosen.Id,
                    Decision = sourceRejection ? ManualOverrideDecision.Reject : ManualOverrideDecision.Pin,
                    MatcherVersion = "accepted-routing-v22",
                    DecisionVersion = 1,
                    Reason = "Personal choice",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            await setup.SaveChangesAsync();
        }

        await RunRoutineAsync(fixture, refresh);

        await using var saved = fixture.Factory.CreateDbContext();
        var snapshot = await saved.ExternalMetadataSnapshots.SingleAsync();
        var authorities = await saved.ManualTrackOverrides.ToArrayAsync();
        var effective = ManualTrackOverrides.Select(authorities, fixture.Owner).Effective;
        Assert.Equal(personalId, effective!.Id);
        var projection = TrackRouteProjector.Project(snapshot, await saved.TrackMatches.OrderByDescending(item => item.DecisionVersion).FirstOrDefaultAsync(),
            effective, await saved.ProviderTrackIdentities.SingleAsync(item => item.Id == snapshot.ProviderTrackIdentityId),
            await saved.LibraryTracks.ToArrayAsync(), await saved.ProviderTrackIdentities.ToArrayAsync(), ["deezer"]);
        Assert.Equal(sourceRejection ? TrackMatchState.Rejected : TrackMatchState.Pinned, projection.ReviewState);
        Assert.Equal(sourceRejection ? null : (Guid?)chosen.Id, projection.LibraryTrack?.Id);
        Assert.Equal(2, authorities.Length);
        Assert.All(authorities, item => { Assert.Equal("accepted-routing-v22", item.MatcherVersion); Assert.Null(item.RevokedAt); });
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task SeedCreatedSnapshotKeepsMissingExplicitEvidenceUnknown()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var setup = fixture.Factory.CreateDbContext())
        {
            setup.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = Guid.NewGuid(),
                OwnerUserId = fixture.Owner,
                ProviderId = "spotify",
                DisplayName = "Source",
                Enabled = true
            });
            setup.LibraryTracks.Add(Local(fixture.Owner, "legacy-clean", "Legacy Track (Clean)"));
            await setup.SaveChangesAsync();
        }
        SourceTrackSeed seed = new("spotify", "legacy", "Legacy Track", "Artist", "Album", 180000, null, null, "1");
        Assert.Equal(2, await fixture.Service.EnsureSourceSnapshotsAsync([seed]));

        Assert.Single(await fixture.Service.MatchSourceTracksAsync([seed], "unknown-seed"));

        await using var saved = fixture.Factory.CreateDbContext();
        var snapshot = await saved.ExternalMetadataSnapshots.SingleAsync(item => item.ExternalIdHash == Hash("legacy"));
        using var payload = JsonDocument.Parse(snapshot.PayloadJson);
        Assert.Null(TrackSnapshotMetadata.IsExplicit(payload.RootElement));
        var decision = await saved.TrackMatches.Where(item => item.ExternalSnapshotId == snapshot.Id)
            .OrderByDescending(item => item.DecisionVersion).FirstAsync();
        var score = Assert.Single(JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(decision.CandidateResultsJson)!);
        Assert.True(score.Confidence > 0);
        Assert.DoesNotContain("clean_edit_of_explicit", score.Reasons);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task ManagedRematchRechecksAuthorityAfterExternalSearchBeforeSaving()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["deezer"]);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "target")).ReturnsAsync((Song?)null);
        await using var fixture = await Fixture.CreateAsync(ExplicitPayload("IsExplicit", "true"), gateway: gateway.Object);
        gateway.Setup(item => item.SearchMatchingSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 15))
            .Returns(async () =>
            {
                await using var concurrent = fixture.Factory.CreateDbContext();
                concurrent.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
                {
                    Id = Guid.NewGuid(),
                    ExternalSnapshotId = fixture.SnapshotId,
                    SourceProviderId = "spotify",
                    SourceExternalIdHash = Hash("source"),
                    Decision = ManualOverrideDecision.Reject,
                    MatcherVersion = "accepted-routing-v22",
                    DecisionVersion = 1,
                    Reason = "Preserve the new household decision",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await concurrent.SaveChangesAsync();
                return new MatchingSongSearchResult([new Song { ExternalProvider = "deezer", ExternalId = "explicit", Title = "Track Melodies", Artist = "Artist", Album = "Album", Duration = 180, ExplicitContentLyrics = 1 }], []);
            });
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "owner",
            new AllstarrPrincipal(fixture.Owner, "jellyfin", "backend", "owner", "Owner", false),
            "authority-race", DateTimeOffset.UtcNow.AddMinutes(1), default);

        var result = await fixture.Service.RematchSnapshotAsync(context, fixture.SnapshotId, "authority-race", TrackRematchAllService.RolloutPolicyVersion);

        Assert.False(result.Succeeded);
        Assert.Equal(TrackMatchCommandFailure.Conflict, result.Failure);
        await using var saved = fixture.Factory.CreateDbContext();
        Assert.Empty(await saved.TrackMatches.ToArrayAsync());
        Assert.Equal(2, await saved.ProviderTrackIdentities.CountAsync());
        Assert.Single(await saved.ManualTrackOverrides.Where(item => item.RevokedAt == null).ToArrayAsync());
    }

    private static async Task RunRoutineAsync(Fixture fixture, bool refresh)
    {
        if (refresh) Assert.Single(await fixture.Service.MatchSourceTracksAsync(
            [new("spotify", "source", "Track Melodies", "Artist", "Album", 180000, null, null, "1")], "routine"));
        else Assert.True((await fixture.Service.RematchSnapshotAsync(new(fixture.Owner, false), fixture.SnapshotId, "routine")).Succeeded);
    }

    private static string ExplicitPayload(string field, string raw)
    {
        var values = new Dictionary<string, object?> { ["Title"] = "Track Melodies", ["Artist"] = "Artist", ["Album"] = "Album", ["DurationMilliseconds"] = 180000 };
        if (field.Length > 0) values[field] = JsonSerializer.Deserialize<JsonElement>(raw);
        return JsonSerializer.Serialize(values);
    }

    private static LibraryTrackRecord Local(Guid owner, string id, string title) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = owner,
        BackendInstanceId = "backend",
        BackendLibraryId = "music",
        BackendItemId = id,
        Title = title,
        Artist = "Artist",
        Album = "Album",
        DurationMilliseconds = 180000,
        Protocol = "jellyfin"
    };

    private static ExternalMetadataSnapshotRecord Snapshot(Guid owner, Guid? identity, string externalId,
        string payload, DateTimeOffset retrievedAt) => new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderTrackIdentityId = identity,
            ProviderId = "spotify",
            ResourceKind = "track",
            BackendInstanceId = "backend",
            BackendPrincipalId = "owner",
            Protocol = "jellyfin",
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

        public static async Task<Fixture> CreateAsync(string? payload = null,
            ProviderUseMode? policyTargetMode = null, IProtocolProviderGateway? gateway = null)
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
            var policies = policyTargetMode.HasValue
                ? new FixedPolicyResolver(fixture.Owner, policyTargetMode.Value)
                : null;
            fixture.Service = new(fixture.Factory, new TrackMatchDecisionEngine(),
                new ProviderAccountResolver(fixture.Factory), new Clock(), new TestBackendLibraryAccess(fixture.Factory, "music"),
                gateway == null ? null : new TrackMatchPlayableSearch(gateway, new TrackMatchDecisionEngine(), NullLogger<TrackMatchPlayableSearch>.Instance),
                providerGateway: gateway,
                effectivePolicies: policies);
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

    private sealed class FixedPolicyResolver(Guid owner, ProviderUseMode targetMode) : IEffectiveProviderPolicyResolver
    {
        private EffectiveProviderPolicySnapshot Snapshot(Guid userId) => new(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty,
            "lossless",
            0)
        {
            UserId = userId,
            ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty
                .Add("qobuz", targetMode)
        };

        public Task<EffectiveProviderPolicySnapshot> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot(owner));

        public Task<EffectiveProviderPolicySnapshot> ResolveForUserAsync(Guid userId,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot(userId));
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
