using System.Diagnostics;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed partial class ListeningHistoryResolutionTests
{
    [Fact]
    public async Task SameSpotifyReferenceReusesMatchedListenerEventWithoutSearch()
    {
        var track = await Library("Known Recording");
        const string reference = "spotify:track:1111111111111111111111";
        await Add("Original", providerReference: reference);
        await using (var db = factory.CreateDbContext())
        {
            var play = await db.ListeningEvents.SingleAsync();
            play.CanonicalRecordingId = track.CanonicalRecordingId;
            play.LibraryTrackId = track.Id;
            await db.SaveChangesAsync();
        }
        await Add("Original", 3, providerReference: reference);
        await Handler().RecoverAsync(default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        gateway.Verify(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(), 15), Times.Never);
        await using var read = factory.CreateDbContext();
        Assert.All(await read.ListeningEvents.ToArrayAsync(), item => Assert.Equal(track.CanonicalRecordingId, item.CanonicalRecordingId));
    }

    [Fact]
    public async Task ThreeUnavailableProvidersDoNotStopRemainingSongs()
    {
        for (var index = 0; index < 8; index++) await Add("Song " + index);
        await using (var db = factory.CreateDbContext())
        {
            for (var index = 0; index < 8; index++)
            {
                var recording = Guid.NewGuid();
                db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
                db.ProviderTrackIdentities.Add(new()
                {
                    Id = Guid.NewGuid(),
                    CanonicalRecordingId = recording,
                    ProviderId = "deezer",
                    ExternalId = "Song " + index,
                    ExternalIdHash = ListeningHistoryImportService.Hash("Song " + index),
                    ResourceKind = ProviderResourceKind.Track,
                    Scope = ProviderIdentityScope.Catalog,
                    Verification = ProviderIdentityVerification.Verified,
                    VerificationMethod = "source-use",
                    DecisionVersion = 1
                });
            }
            await db.SaveChangesAsync();
        }
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["qobuz", "tidal", "soundcloud", "deezer"]);
        foreach (var provider in new[] { "qobuz", "tidal", "soundcloud" })
            gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), provider, It.IsAny<string>(), 15))
                .ThrowsAsync(new HttpRequestException());
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .Returns((ProtocolExecutionContext _, string _, string query, int _) => Task.FromResult(
                ProviderOutcome<IReadOnlyList<Song>>.Success([new()
                {
                    ExternalProvider = "deezer", ExternalId = query[..6], Title = query[..6], Artist = "Artist", Album = "Album", Duration = 180
                }])));
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .Returns((ProtocolExecutionContext _, string _, string id) => Task.FromResult<Song?>(new()
            {
                ExternalProvider = "deezer",
                ExternalId = id,
                Title = id,
                Artist = "Artist",
                Album = "Album",
                Duration = 180
            }));
        await Handler().RecoverAsync(default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        gateway.Verify(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15), Times.AtLeastOnce);
        await using var read = factory.CreateDbContext();
        Assert.All(await read.ListeningEvents.ToArrayAsync(), item => Assert.NotNull(item.CanonicalRecordingId));
        Assert.Empty(await read.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "history-resolution").ToArrayAsync());
    }

    [Fact]
    public async Task LargeLocalFixtureUsesNoProviderCallsAndQueuesAtMostOneTagBatch()
    {
        await SeedLocalImport(5375, 6);
        await using (var seed = factory.CreateDbContext())
        {
            var all = await seed.LibraryTracks.OrderBy(item => item.Id).ToArrayAsync();
            foreach (var track in all.Skip(3694))
            {
                var identity = new ProviderTrackIdentityRecord
                {
                    Id = Guid.NewGuid(),
                    CanonicalRecordingId = track.CanonicalRecordingId!.Value,
                    ProviderId = "deezer",
                    ExternalId = track.BackendItemId,
                    ExternalIdHash = ListeningHistoryImportService.Hash(track.BackendItemId),
                    ResourceKind = ProviderResourceKind.Track,
                    Verification = ProviderIdentityVerification.Verified,
                    Scope = ProviderIdentityScope.Catalog,
                    VerificationMethod = "source-use",
                    DecisionVersion = 1
                };
                seed.ProviderTrackIdentities.Add(identity);
                var payload = JsonSerializer.Serialize(new { track.Title, track.Artist, track.Album, track.DurationMilliseconds });
                seed.ExternalMetadataSnapshots.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    ProviderId = "deezer",
                    ResourceKind = "playback-metadata",
                    ExternalIdHash = identity.ExternalIdHash,
                    ProviderTrackIdentityId = identity.Id,
                    PayloadJson = payload,
                    PayloadSha256 = ListeningHistoryImportService.Hash(payload),
                    SnapshotVersion = 1,
                    RetrievedAt = clock.UtcNow
                });
                seed.LibraryTracks.Remove(track);
            }
            for (var index = 5375; index < 8078; index++) seed.CanonicalRecordings.Add(new() { Id = Guid.NewGuid(), CreatedByUserId = owner });
            for (var index = 1681; index < 14763; index++) seed.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = all[index % all.Length].CanonicalRecordingId!.Value,
                ProviderId = "deezer",
                ExternalId = "stored-" + index,
                ExternalIdHash = ListeningHistoryImportService.Hash("stored-" + index),
                ResourceKind = ProviderResourceKind.Track,
                Verification = ProviderIdentityVerification.Verified,
                Scope = ProviderIdentityScope.Catalog,
                VerificationMethod = "source-use",
                DecisionVersion = 1
            });
            await seed.SaveChangesAsync();
        }
        await Handler().RecoverAsync(default);
        var elapsed = Stopwatch.StartNew();
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run(collectTags: true, realTime: true)).Kind);
        elapsed.Stop();
        var measurement = $"Local fixture: 5375 songs, 32250 plays, 8078 recordings, 14763 identities, 3694 library tracks: {elapsed.Elapsed.TotalSeconds:F3}s, 0 provider calls";
        output.WriteLine(measurement);
        Console.WriteLine(measurement);
        // About 13 seconds of this is the deliberate pause between batches that keeps writes out of playback's way.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(75), elapsed.Elapsed.ToString());
        gateway.Verify(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(), 15), Times.Never);
        await using var read = factory.CreateDbContext();
        Assert.Equal(32250, await read.ListeningEvents.CountAsync(item => item.CanonicalRecordingId != null));
        Assert.InRange(await read.Jobs.CountAsync(item => item.Type == "recording.tags.refresh"), 1, 100);
    }
    [Fact]
    public async Task FinalProgressCountsEachSongReasonOnce()
    {
        await Library("Local");
        await Add("Local", 8);
        await Add("Missing", 4);
        await Handler().RecoverAsync(default);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("history-test", [ListeningHistoryResolution.JobTypeName]));
        var updates = new List<DurableJobProgressUpdate>();
        var result = await Handler().ExecuteAsync(new(claim, services)
        {
            ReportProgressAsync = (update, _) => { updates.Add(update); return Task.FromResult(true); }
        }, default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        using var counts = JsonDocument.Parse(updates.Last().Message);
        Assert.Equal(1, counts.RootElement.GetProperty("matched-stored-metadata").GetInt32());
        Assert.Equal(1, counts.RootElement.GetProperty("no-candidates").GetInt32());
        Assert.Equal(2, counts.RootElement.EnumerateObject().Sum(item => item.Value.GetInt32()));
    }

    [Fact]
    public async Task ExistingVerifiedIdentityResolvesSongWithoutCanonicalIdOnRead()
    {
        await Add("Song", providerReference: "spotify:track:1111111111111111111111");
        var recording = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = recording,
                ProviderId = "deezer",
                ExternalId = "found",
                ExternalIdHash = ListeningHistoryImportService.Hash("found"),
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "source-use",
                DecisionVersion = 1
            });
            await db.SaveChangesAsync();
        }
        var song = new Song { ExternalProvider = "deezer", ExternalId = "found", Title = "Song", Artist = "Artist", Album = "Album", Duration = 180 };
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([song]));
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "found")).ReturnsAsync(song);
        await Handler().RecoverAsync(default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        await using var read = factory.CreateDbContext();
        var play = await read.ListeningEvents.SingleAsync();
        Assert.Equal(recording, play.CanonicalRecordingId);
        Assert.Equal("spotify", play.ProviderId);
        Assert.Equal("spotify:track:1111111111111111111111", play.ProviderTrackReference);
    }

    [Fact]
    public async Task ProviderRestartDoesNotRepeatFinishedSongs()
    {
        await Add("First", 3);
        await Add("Second");
        await Handler().RecoverAsync(default);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("history-test", [ListeningHistoryResolution.JobTypeName]));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler().ExecuteAsync(new(claim, services)
        {
            ReportProgressAsync = (_, _) => { cancellation.Cancel(); return Task.FromResult(true); }
        }, cancellation.Token));
        gateway.Invocations.Clear();
        clock.UtcNow += TimeSpan.FromHours(1);
        Assert.Equal(1, await jobs.RecoverInterruptedAsync());
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        Assert.DoesNotContain(gateway.Invocations, item => item.Method.Name == "SearchAutomaticProviderSongsAsync" &&
            item.Arguments[2].ToString()!.Contains("First", StringComparison.Ordinal));
        await using var read = factory.CreateDbContext();
        Assert.Equal(2, await read.ExternalMetadataSnapshots.CountAsync(item => item.ResourceKind == "history-resolution"));
    }

    [Fact]
    public async Task ContinuingClaimReusesLoadedEventsUntilCountChanges()
    {
        await Add("Missing");
        var reads = new HistoryReads();
        factory = new(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(reads).Options);
        var handler = Handler();
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Failure(new(ProviderErrorKind.TransientFailure)));
        await handler.RecoverAsync(default);
        var initial = reads.EventLoads;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("history-test", [ListeningHistoryResolution.JobTypeName]));
            var result = await handler.ExecuteAsync(new(claim, services), default);
            Assert.Equal(DurableJobCompletionKind.Deferred, result.Kind);
            await jobs.CompleteAsync(claim, result);
            clock.UtcNow += TimeSpan.FromMinutes(2);
            if (attempt == 1) await Add("Another");
        }
        Assert.Equal(initial + 2, reads.EventLoads);
    }

    private sealed class HistoryReads : DbCommandInterceptor
    {
        public int EventLoads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ImportProvenance", StringComparison.Ordinal) &&
                command.CommandText.Contains("FROM \"listening_events\"", StringComparison.Ordinal)) EventLoads++;
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedSpotifyIdentityReusesStoredCanonicalWithEditionSafety(bool live)
    {
        var track = await Library("僕の戦争");
        const string id = "1111111111111111111111";
        await using (var db = factory.CreateDbContext())
        {
            (await db.LibraryTracks.SingleAsync()).Artist = "神聖かまってちゃん";
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = track.CanonicalRecordingId!.Value,
                ProviderId = "spotify",
                ExternalId = id,
                ExternalIdHash = ListeningHistoryImportService.Hash(id),
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "source-use",
                DecisionVersion = 1
            });
            await db.SaveChangesAsync();
        }
        await Add(live ? "Boku no Sensou (Live)" : "Boku no Sensou", providerReference: "spotify:track:" + id);
        await using (var db = factory.CreateDbContext())
        {
            (await db.ListeningEvents.SingleAsync()).Artist = "Shinsei Kamattechan";
            await db.SaveChangesAsync();
        }
        await Handler().RecoverAsync(default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        await using var read = factory.CreateDbContext();
        var play = await read.ListeningEvents.SingleAsync();
        Assert.Equal(live ? null : track.CanonicalRecordingId, play.CanonicalRecordingId);
        if (!live) gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LegacyRetryPositionDoesNotBypassMostPlayedSongs()
    {
        await Add("Popular", 4);
        await Add("Quiet");
        await Handler().RecoverAsync(default);
        await using (var db = factory.CreateDbContext())
        {
            (await db.Jobs.SingleAsync()).PayloadJson = JsonSerializer.Serialize(new ListeningHistoryResolutionPayload(Scope, RetryOffset: 1));
            await db.SaveChangesAsync();
        }
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        var first = gateway.Invocations.First(item => item.Method.Name == "SearchAutomaticProviderSongsAsync");
        Assert.Contains("Popular", first.Arguments[2].ToString());
    }

    [Theory]
    [InlineData("provider", "provider-failure:TransientFailure")]
    [InlineData("rejected", "candidates-rejected:")]
    [InlineData("edition", "edition-mismatch")]
    [InlineData("missing-song", "song-read-failed")]
    [InlineData("missing-identity", "identity-missing")]
    [InlineData("ineligible", "not-playback-eligible")]
    [InlineData("exception", "exception:InvalidOperationException")]
    public async Task EveryProviderExitReportsOneStageReason(string mode, string expected)
    {
        await Add("Song");
        var song = new Song { Title = "Song", Artist = "Artist", Album = "Album", Duration = 180, ExternalProvider = "deezer", ExternalId = "found" };
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync(mode == "provider" ? ProviderOutcome<IReadOnlyList<Song>>.Failure(new(ProviderErrorKind.TransientFailure)) :
                ProviderOutcome<IReadOnlyList<Song>>.Success([mode == "rejected" ? new()
                {
                    Title = "Completely Different", Artist = "Someone Else", Album = "Other", Duration = 800, ExternalProvider = "deezer", ExternalId = "other"
                } : song]));
        var read = gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "found"));
        if (mode == "exception") read.ThrowsAsync(new InvalidOperationException("Unavailable"));
        else read.ReturnsAsync(mode == "missing-song" ? null : mode == "edition" ? new Song
        {
            Title = "Song (Live)",
            Artist = "Artist",
            Album = "Album",
            Duration = 180,
            ExternalProvider = "deezer",
            ExternalId = "found"
        } : song);
        if (mode == "ineligible")
        {
            await using var db = factory.CreateDbContext();
            var recording = Guid.NewGuid();
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = recording,
                ProviderId = "deezer",
                ExternalId = "found",
                ExternalIdHash = ListeningHistoryImportService.Hash("found"),
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "automatic-suggestion",
                DecisionVersion = 1
            });
            await db.SaveChangesAsync();
        }
        await Handler().RecoverAsync(default);
        var claim = Assert.IsType<DurableJobClaim>(await jobs.ClaimNextAsync("history-test", [ListeningHistoryResolution.JobTypeName]));
        var updates = new List<DurableJobProgressUpdate>();
        var logger = new Mock<ILogger<ListeningHistoryResolution>>();
        await Handler(logger: logger.Object).ExecuteAsync(new(claim, services)
        {
            ReportProgressAsync = (update, _) => { updates.Add(update); return Task.FromResult(true); }
        }, default);
        using var summary = JsonDocument.Parse(updates.Last().Message);
        var reason = Assert.Single(summary.RootElement.EnumerateObject());
        Assert.StartsWith(expected, reason.Name);
        Assert.Equal(1, reason.Value.GetInt32());
        Assert.Single(logger.Invocations, item => item.Method.Name == "Log" && (LogLevel)item.Arguments[0] == LogLevel.Information);
        if (mode == "exception") Assert.Contains(logger.Invocations, item => item.Method.Name == "Log" &&
            (LogLevel)item.Arguments[0] == LogLevel.Warning && item.Arguments[2].ToString()!.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetiredProviderLinkStillIdentifiesTheSong(bool anotherRoute)
    {
        await Add("Song");
        var recording = Guid.NewGuid();
        var other = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
            db.ProviderTrackIdentities.Add(Link("deezer", "found", CatalogIdentityPolicy.SupersededVerificationMethod, Guid.NewGuid()));
            if (anotherRoute) db.ProviderTrackIdentities.Add(Link("apple-musickit", "apple", "automatic-match", other));
            await db.SaveChangesAsync();
        }
        var song = new Song { Title = "Song", Artist = "Artist", Album = "Album", Duration = 180, ExternalProvider = "deezer", ExternalId = "found" };
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([song]));
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "found")).ReturnsAsync(song);
        await ListeningHistoryResolution.EnqueueAsync(factory, jobs, Scope, clock.UtcNow, default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await Run()).Kind);
        await using var read = factory.CreateDbContext();
        var play = await read.ListeningEvents.SingleAsync();
        Assert.Equal(anotherRoute ? recording : null, play.CanonicalRecordingId);
        Assert.Equal(anotherRoute ? other : null, play.ProviderTrackIdentityId);

        ProviderTrackIdentityRecord Link(string provider, string external, string method, Guid id) => new()
        {
            Id = id,
            CanonicalRecordingId = recording,
            ProviderId = provider,
            ExternalId = external,
            ExternalIdHash = ListeningHistoryImportService.Hash(external),
            ResourceKind = ProviderResourceKind.Track,
            Scope = ProviderIdentityScope.Catalog,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = method,
            DecisionVersion = 1
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleAccountIdentitiesRequireUnambiguousCanonicalEvidence(bool known)
    {
        await Add("Song");
        var preferred = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            var first = Guid.NewGuid();
            var account = Guid.NewGuid();
            db.CanonicalRecordings.AddRange(new CanonicalRecordingRecord { Id = first, CreatedByUserId = owner },
                new CanonicalRecordingRecord { Id = preferred, CreatedByUserId = owner });
            db.ProviderAccounts.Add(new() { Id = account, ProviderId = "deezer", OwnerUserId = owner, Enabled = true });
            db.ProviderTrackIdentities.AddRange(new ProviderTrackIdentityRecord
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                CanonicalRecordingId = first,
                ProviderId = "deezer",
                ExternalId = "found",
                ExternalIdHash = ListeningHistoryImportService.Hash("found"),
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "source-use",
                DecisionVersion = 1
            }, new ProviderTrackIdentityRecord
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000002"),
                CanonicalRecordingId = preferred,
                ProviderId = "deezer",
                ExternalId = "found",
                ExternalIdHash = ListeningHistoryImportService.Hash("found"),
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Account,
                ProviderAccountId = account,
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "source-use",
                DecisionVersion = 1
            });
            await db.SaveChangesAsync();
        }
        var song = new Song
        {
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            Duration = 180,
            ExternalProvider = "deezer",
            ExternalId = "found",
            CanonicalRecordingId = known ? preferred : null
        };
        gateway.Setup(item => item.SearchAutomaticProviderSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>(), 15))
            .ReturnsAsync(ProviderOutcome<IReadOnlyList<Song>>.Success([song]));
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "found")).ReturnsAsync(song);
        await Handler().RecoverAsync(default);
        Assert.Equal(known ? DurableJobCompletionKind.Succeeded : DurableJobCompletionKind.Deferred, (await Run()).Kind);
        await using var read = factory.CreateDbContext();
        Assert.Equal(known ? preferred : (Guid?)null, (await read.ListeningEvents.SingleAsync()).CanonicalRecordingId);
    }

}
