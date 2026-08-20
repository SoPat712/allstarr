using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Subsonic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class PlaybackSignalPipelineTests : IAsyncLifetime
{
    private PostgresTestDatabase database = null!;
    private readonly Guid tenant = Guid.CreateVersion7();
    private readonly Guid user = Guid.CreateVersion7();
    private Factory factory = null!;
    private DurableJobQueue jobs = null!;
    private Clock clock = null!;

    public async Task InitializeAsync()
    {
        database = await PostgresTestDatabase.CreateAsync();
        factory = new(database.Options); await using var db = await factory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow; db.Tenants.Add(new() { Id = tenant, Slug = "playback", Name = "Playback", CreatedAt = now });
        db.Users.Add(new() { Id = user, TenantId = tenant, DisplayName = "User", Status = PlatformUserStatus.Active, CreatedAt = now, UpdatedAt = now }); await db.SaveChangesAsync();
        var identity = Guid.CreateVersion7(); db.BackendIdentities.Add(new() { Id = identity, TenantId = tenant, UserId = user, BackendType = "jellyfin", BackendInstanceId = "backend", PrincipalId = "principal", CreatedAt = now, LastSeenAt = now });
        db.IntelligencePolicies.Add(new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            OwnerUserId = user,
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            LibraryScopeId = "music",
            Enabled = true,
            RetentionDays = 30,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.LibraryTracks.Add(new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            OwnerUserId = user,
            BackendIdentityId = identity,
            LibraryScopeId = "music",
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            BackendItemId = "track-1",
            FilePath = "/media/track.flac",
            Title = "Track",
            Artist = "Artist",
            DurationMilliseconds = 120000,
            ProviderIdsJson = "{}",
            IndexedAt = now,
            SourceModifiedAt = now,
            UpdatedAt = now
        }); await db.SaveChangesAsync();
        clock = new() { UtcNow = now }; var jobOptions = new DurableJobOptions(); jobs = new(factory, jobOptions, new JobPayloadPolicy(jobOptions), clock);
    }

    [Fact]
    public async Task RepeatedAndRestartedSignalCreatesOneExactScopeJob()
    {
        var pipeline = new PlaybackSignalPipeline(jobs); var request = Signal(PlaybackTransition.Start, "track-1", 0);
        Assert.True(await pipeline.RecordAsync(request));
        Assert.False(await pipeline.RecordAsync(request));
        Assert.False(await new PlaybackSignalPipeline(jobs).RecordAsync(request));
        await using var db = await factory.CreateDbContextAsync(); var job = Assert.Single(await db.Jobs.Where(x => x.Type == PlaybackSignalPipeline.JobType).ToListAsync());
        Assert.Equal(tenant, job.TenantId); Assert.Equal(user, job.OwnerUserId); Assert.Equal("music", job.LibraryScopeId);
    }

    [Fact]
    public async Task UnlimitedHistoryGivesRecommendationSignalsNoExpiry()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            var policy = await db.IntelligencePolicies.SingleAsync();
            policy.RetentionDays = 0;
            policy.AllowedSignalTypesJson = "[\"play\"]";
            await db.SaveChangesAsync();
        }
        var scope = new IntelligenceScope(tenant, user, "jellyfin", "backend", "music");

        Assert.True(await new RecommendationSignalWriter(factory, clock)
            .WriteAsync(scope, "play", "track-1", 1, clock.UtcNow));

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(DateTimeOffset.MaxValue, (await verify.ListeningSignals.SingleAsync()).ExpiresAt);
    }

    [Fact]
    public async Task AuthorizedSyntheticProtocolSmoke_RecordsLocalExternalSignalsAndTargetCheckpoints()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            var jellyfinPolicy = await db.IntelligencePolicies.SingleAsync();
            jellyfinPolicy.AllowedSignalTypesJson = "[\"complete\",\"play\"]";
            db.BackendIdentities.Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                UserId = user,
                BackendType = "subsonic",
                BackendInstanceId = "backend",
                PrincipalId = "subsonic-principal",
                CreatedAt = clock.UtcNow,
                LastSeenAt = clock.UtcNow
            });
            db.IntelligencePolicies.Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                OwnerUserId = user,
                Protocol = "subsonic",
                BackendInstanceId = "backend",
                LibraryScopeId = "music",
                Enabled = true,
                RetentionDays = 30,
                AllowedSignalTypesJson = "[\"complete\",\"play\"]",
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var pipeline = new PlaybackSignalPipeline(jobs);
        var jellyfin = Signal(PlaybackTransition.Start, "track-1", 0).ExecutionContext;
        var requests = new List<PlaybackSignalRequest>
        {
            new(jellyfin, PlaybackTransition.Start, "track-1", "smoke-device", "jellyfin-local", 0, clock.UtcNow),
            new(jellyfin, PlaybackTransition.Stop, "track-1", "smoke-device", "jellyfin-local",
                TimeSpan.FromSeconds(60).Ticks, clock.UtcNow.AddSeconds(60)),
            new(jellyfin, PlaybackTransition.Start, "ext-deezer-song-synthetic", "smoke-device", "jellyfin-external", 0,
                clock.UtcNow.AddSeconds(10)),
            new(jellyfin, PlaybackTransition.Stop, "ext-deezer-song-synthetic", "smoke-device", "jellyfin-external",
                TimeSpan.FromSeconds(60).Ticks, clock.UtcNow.AddSeconds(70))
        };
        var subsonic = new ProtocolExecutionContext(ProtocolKind.Subsonic, "backend", "subsonic-principal",
            new AllstarrPrincipal(tenant, user, "subsonic", "backend", "subsonic-principal", "User", false),
            "smoke-subsonic", clock.UtcNow.AddMinutes(1), default, libraryScopeId: "music");
        var parameters = new SubsonicRequestParameters("GET", null, null,
        [
            new("id", "track-1", SubsonicParameterSource.Query),
            new("id", "ext-deezer-song-synthetic", SubsonicParameterSource.Query),
            new("time", clock.UtcNow.ToUnixTimeMilliseconds().ToString(), SubsonicParameterSource.Query),
            new("time", clock.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds().ToString(), SubsonicParameterSource.Query),
            new("submission", "false", SubsonicParameterSource.Query),
            new("submission", "true", SubsonicParameterSource.Query)
        ]);
        var subsonicSignals = new SubsonicScrobbleProtocolAdapter().Parse(parameters, clock.UtcNow);
        Assert.Equal([PlaybackTransition.Start, PlaybackTransition.Submission], subsonicSignals.Select(item => item.Transition));
        requests.AddRange(subsonicSignals.Select(item => new PlaybackSignalRequest(subsonic, item.Transition,
            item.ItemId, "smoke-subsonic", $"subsonic:{item.EventKey}:{item.Index}", null, item.ObservedAt)));

        foreach (var request in requests) Assert.True(await pipeline.RecordAsync(request));
        var lastFm = new Target("lastfm", true);
        var listenBrainz = new Target("listenbrainz", true);
        var trackResolver = new PlaybackTrackResolver(factory,
        [
            new BackendMetadataResolver(new("Synthetic external", "Allstarr qualification", "Smoke", null, 120))
        ]);
        var handler = new PlaybackSignalJobHandler(new RecommendationSignalWriter(factory, clock),
            new ScopedPlaybackScrobbleDelivery(factory, [lastFm, listenBrainz],
                new EfPlaybackDeliveryCheckpointStore(factory), trackResolver),
            new Lyrics(), factory, trackResolver);
        var completed = 0;
        while (await jobs.ClaimNextAsync("smoke", [PlaybackSignalPipeline.JobType]) is { } claim)
        {
            var completion = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
            await jobs.CompleteAsync(claim, completion);
            completed++;
        }
        Assert.Equal(requests.Count, completed);

        await using var verify = await factory.CreateDbContextAsync();
        var occurrences = await verify.ListeningEvents.AsNoTracking().ToListAsync();
        Assert.Contains(occurrences, item => item.Protocol == "jellyfin" && item.TrackReference == "track-1" &&
                                             item.State == ListeningEventState.Completed);
        Assert.Contains(occurrences, item => item.Protocol == "jellyfin" && item.TrackReference == "ext-deezer-song-synthetic" &&
                                             item.State == ListeningEventState.Completed);
        Assert.Contains(await verify.ListeningSignals.AsNoTracking().ToListAsync(), item =>
            item.Protocol == "jellyfin" && item.SignalType == "complete");
        var checkpoints = await verify.PlaybackDeliveryCheckpoints.AsNoTracking().ToListAsync();
        Assert.Equal(["lastfm", "listenbrainz"], checkpoints.Select(item => item.TargetId).Distinct().Order());
        Assert.All(["lastfm", "listenbrainz"], target =>
        {
            Assert.Contains(checkpoints, item => item.TargetId == target && item.Kind == PlaybackScrobbleDeliveryKind.NowPlaying);
            Assert.Contains(checkpoints, item => item.TargetId == target && item.Kind == PlaybackScrobbleDeliveryKind.Completed);
        });
        var correlations = await verify.AuditEvents.AsNoTracking().Where(item => item.Category == "scrobble")
            .Select(item => item.CorrelationId).Distinct().Order().ToListAsync();
        Assert.Equal(checkpoints.Select(item => item.SignalKey).Distinct().Order(), correlations);
    }

    [Fact]
    public async Task MissingProtocolLibraryScope_ResolvesTheUniqueIndexedTrackScope()
    {
        var execution = Signal(PlaybackTransition.Start, "track-1", 0).ExecutionContext;
        var unscoped = new ProtocolExecutionContext(
            execution.Protocol, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId,
            execution.Principal, execution.CorrelationId, execution.Deadline, default);
        var pipeline = new PlaybackSignalPipeline(jobs, new ProtocolLibraryScopeResolver(factory));

        Assert.True(await pipeline.RecordAsync(new(
            unscoped, PlaybackTransition.Start, "track-1", "device", "session", 0, clock.UtcNow)));

        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal("music", Assert.Single(await db.Jobs.ToListAsync()).LibraryScopeId);
    }

    [Fact]
    public async Task MissingProtocolLibraryScope_RejectsAnAmbiguousItem()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            var original = await db.LibraryTracks.AsNoTracking().SingleAsync();
            db.LibraryTracks.Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                OwnerUserId = user,
                BackendIdentityId = original.BackendIdentityId,
                LibraryScopeId = "other",
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendItemId = "track-1",
                FilePath = "/media/other.flac",
                Title = "Track",
                Artist = "Artist",
                DurationMilliseconds = 120000,
                ProviderIdsJson = "{}",
                IndexedAt = clock.UtcNow,
                SourceModifiedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var execution = Signal(PlaybackTransition.Start, "track-1", 0).ExecutionContext;
        var unscoped = new ProtocolExecutionContext(
            execution.Protocol, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId,
            execution.Principal, execution.CorrelationId, execution.Deadline, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtocolLibraryScopeResolver(factory).ResolveAsync(unscoped, "track-1"));
    }

    [Fact]
    public async Task EmptyDurableIndex_UsesConfiguredJellyfinLibraryScope()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.LibraryTracks.RemoveRange(db.LibraryTracks);
            await db.SaveChangesAsync();
        }
        var execution = Signal(PlaybackTransition.Start, "backend-track", 0).ExecutionContext;
        var unscoped = new ProtocolExecutionContext(
            execution.Protocol, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId,
            execution.Principal, execution.CorrelationId, execution.Deadline, default);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Jellyfin:LibraryId"] = "configured-music" }).Build();

        var resolved = await new ProtocolLibraryScopeResolver(factory, configuration)
            .ResolveAsync(unscoped, "backend-track");

        Assert.Equal("configured-music", resolved.LibraryScopeId);
    }

    [Fact]
    public async Task EmptyDurableIndex_ResolvesPlaybackTrackFromBackendMetadata()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.LibraryTracks.RemoveRange(db.LibraryTracks);
            await db.SaveChangesAsync();
        }
        var metadata = new BackendMetadataResolver(new(
            "Backend title", "Backend artist", "Backend album", "/art", 180));
        var resolver = new PlaybackTrackResolver(factory, [metadata]);

        var resolved = await resolver.ResolveAsync(Payload() with { ItemId = "backend-track" });

        Assert.NotNull(resolved);
        Assert.Equal("Backend title", resolved.Title);
        Assert.Equal(180_000, resolved.DurationMilliseconds);
    }

    [Fact]
    public async Task MissingPlaySession_DedupesRetriesButAllowsALaterOccurrence()
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        var first = Signal(PlaybackTransition.Start, "track-1", 0) with
        {
            PlaySessionId = null,
            ObservedAt = clock.UtcNow
        };
        var later = first with { ObservedAt = clock.UtcNow.AddSeconds(31) };

        Assert.True(await pipeline.RecordAsync(first));
        Assert.False(await pipeline.RecordAsync(first));
        Assert.True(await pipeline.RecordAsync(later));
    }

    [Fact]
    public async Task ProgressUsesStableTenSecondBucketsAndOrdersDistinctTransitions()
    {
        var pipeline = new PlaybackSignalPipeline(jobs);
        Assert.True(await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "track-1", TimeSpan.FromSeconds(11).Ticks)));
        Assert.False(await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "track-1", TimeSpan.FromSeconds(19).Ticks)));
        Assert.True(await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "track-1", TimeSpan.FromSeconds(21).Ticks)));
        Assert.True(await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(21).Ticks)));
    }

    [Fact]
    public async Task HandlerRejectsCrossTenantClaimBeforeAnySideEffect()
    {
        var writer = new Writer(); var scrobbles = new Scrobbles(); var lyrics = new Lyrics();
        var handler = new PlaybackSignalJobHandler(writer, scrobbles, lyrics, factory);
        var payload = Signal(PlaybackTransition.Start, "track-1", 0);
        await new PlaybackSignalPipeline(jobs).RecordAsync(payload);
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]); Assert.NotNull(claim);
        var badPayload = new PlaybackSignalPayload(new(Guid.CreateVersion7(), user, "jellyfin", "backend", "music"), PlaybackTransition.Start, "track-1", "device", "session", 0, clock.UtcNow, new string('a', 64));
        var badClaim = claim! with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(badPayload) };
        var result = await handler.ExecuteAsync(new(badClaim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Failed, result.Kind); Assert.Equal(0, writer.Calls + scrobbles.Calls + lyrics.Calls);
    }

    [Fact]
    public async Task RetryAfterSignalWriteDoesNotDuplicateHabitWeightOrSuccessfulScrobble()
    {
        await new PlaybackSignalPipeline(jobs).RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]); Assert.NotNull(claim);
        var writer = new Writer(); var scrobbles = new Scrobbles { FailFirst = true }; var lyrics = new Lyrics();
        var handler = new PlaybackSignalJobHandler(writer, scrobbles, lyrics, factory);
        Assert.Equal(DurableJobCompletionKind.Retry, (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
        Assert.Equal(1, writer.Calls);
        Assert.Equal(1, scrobbles.Successes);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.ListeningEvents.ToListAsync());
    }

    [Fact]
    public async Task DisabledPolicySkipsPrivateHistoryButKeepsPlaybackSideEffects()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.IntelligencePolicies.SingleAsync()).Enabled = false;
            await db.SaveChangesAsync();
        }
        await new PlaybackSignalPipeline(jobs).RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]);
        var writer = new Writer(); var scrobbles = new Scrobbles(); var lyrics = new Lyrics();

        var result = await new PlaybackSignalJobHandler(writer, scrobbles, lyrics, factory)
            .ExecuteAsync(new(claim!, EmptyServices.Instance), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Equal(0, writer.Calls);
        Assert.Equal(1, scrobbles.Successes);
        Assert.Equal(1, lyrics.Calls);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.ListeningEvents.ToListAsync());
    }

    [Fact]
    public async Task AcceptedOccurrence_QueuesMusicBrainzWithoutCallingThePublicService()
    {
        await new PlaybackSignalPipeline(jobs).RecordAsync(
            Signal(PlaybackTransition.Start, "track-1", 0));
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]);
        var enrichment = new MusicBrainzListeningEnrichmentQueue(
            jobs, Options.Create(new MusicBrainzSettings { Enabled = true }));
        var handler = new PlaybackSignalJobHandler(
            new Writer(), new Scrobbles(), new Lyrics(), factory,
            new PlaybackTrackResolver(factory), enrichment);

        Assert.Equal(DurableJobCompletionKind.Succeeded,
            (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);

        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(MusicBrainzEnrichmentState.Pending,
            Assert.Single(await db.ListeningEvents.AsNoTracking().ToListAsync())
                .MusicBrainzEnrichmentState);
        Assert.Single(await db.Jobs.Where(job =>
            job.Type == MusicBrainzListeningEnrichmentQueue.JobType).ToListAsync());
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 0)]
    public async Task ScopedDeliverySkipsMissingOptionalAccounts(bool lastFm, bool listenBrainz, int expected)
    {
        var targets = new[] { new Target("lastfm", lastFm), new Target("listenbrainz", listenBrainz) };
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, targets, new Checkpoints());
        await delivery.DeliverAsync(Payload(), default);
        Assert.Equal(expected, targets.Sum(x => x.Successes));
    }

    [Fact]
    public async Task DurableCheckpointResumesAfterLaterTargetFailureWithoutRepeatingFirst()
    {
        var first = new Target("lastfm", true); var second = new Target("listenbrainz", true) { FailFirst = true };
        var checkpoints = new EfPlaybackDeliveryCheckpointStore(factory);
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [first, second], checkpoints);
        var failure = await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() =>
            delivery.DeliverAsync(Payload(), default));
        Assert.Equal("playback_scrobble_retrying", failure.Code);
        Assert.True(failure.Retryable);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var rows = await db.PlaybackDeliveryCheckpoints.AsNoTracking()
                .OrderBy(row => row.TargetId)
                .ToListAsync();
            Assert.Collection(rows,
                row => Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, row.State),
                row => Assert.Equal(ScopedPlaybackScrobbleOutcome.Retrying, row.State));
        }

        await delivery.DeliverAsync(Payload(), default);
        await delivery.DeliverAsync(Payload(), default);

        Assert.Equal(1, first.Calls);
        Assert.Equal(2, second.Calls);
        Assert.Equal(1, first.Successes);
        Assert.Equal(1, second.Successes);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.All(await verify.PlaybackDeliveryCheckpoints.AsNoTracking().ToListAsync(),
            row => Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, row.State));
    }

    [Fact]
    public async Task CancellationStopsTargetFanoutWithoutWritingARetryCheckpoint()
    {
        using var cancellation = new CancellationTokenSource();
        var cancelled = new Target("lastfm", true) { CancelSource = cancellation };
        var later = new Target("listenbrainz", true);
        var delivery = new ScopedPlaybackScrobbleDelivery(
            factory, [cancelled, later], new EfPlaybackDeliveryCheckpointStore(factory));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            delivery.DeliverAsync(Payload(), cancellation.Token));

        Assert.Equal(1, cancelled.Calls);
        Assert.Equal(0, later.Calls);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.PlaybackDeliveryCheckpoints.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ScopedDelivery_SummarizesMultipleTargetsIntoOneAuditEvent()
    {
        var delivery = new ScopedPlaybackScrobbleDelivery(
            factory,
            [new Target("lastfm", true), new Target("listenbrainz", true)],
            new Checkpoints());

        var payload = Payload();
        await delivery.DeliverAsync(payload, default);

        await using var database = await factory.CreateDbContextAsync();
        var audit = Assert.Single(await database.AuditEvents.AsNoTracking()
            .Where(item => item.Category == "scrobble" && item.Action == "delivered")
            .ToListAsync());
        using var details = System.Text.Json.JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal("success", audit.Outcome);
        Assert.Equal(ScopedPlaybackScrobbleDelivery.CheckpointKey(payload), audit.CorrelationId);
        Assert.Equal(2, details.RootElement.GetProperty("providerCount").GetInt32());
        Assert.Equal(["lastfm", "listenbrainz"], details.RootElement.GetProperty("providerIds")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["Last.fm", "ListenBrainz"], details.RootElement.GetProperty("providerNames")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(2, details.RootElement.GetProperty("attemptedCount").GetInt32());
        Assert.Equal(2, details.RootElement.GetProperty("deliveredCount").GetInt32());
        Assert.Equal("playback_scrobble_delivered", details.RootElement.GetProperty("reasonCode").GetString());
        Assert.True(details.RootElement.GetProperty("durationMilliseconds").GetInt64() >= 0);
        Assert.False(details.RootElement.TryGetProperty("Title", out _));
        Assert.False(details.RootElement.TryGetProperty("Artist", out _));
        Assert.False(details.RootElement.TryGetProperty("Album", out _));
        Assert.False(details.RootElement.TryGetProperty("observedAt", out _));
        Assert.DoesNotContain("track-1", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScopedDelivery_FirstUnauthorizedProviderDoesNotBlockHealthyProvider()
    {
        var rejected = new Target("lastfm", true) { Reject = true };
        var healthy = new Target("listenbrainz", true);
        var checkpoints = new Checkpoints();
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [rejected, healthy], checkpoints);
        var payload = Payload();

        var firstFailure = await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() =>
            delivery.DeliverAsync(payload, default));
        Assert.Equal("playback_scrobble_unauthorized", firstFailure.Code);
        Assert.Equal(0, rejected.Successes);
        Assert.Equal(1, healthy.Successes);

        await using (var database = await factory.CreateDbContextAsync())
        {
            var audit = Assert.Single(await database.AuditEvents.AsNoTracking()
                .Where(item => item.Category == "scrobble" && item.Action == "delivered")
                .ToListAsync());
            using var details = System.Text.Json.JsonDocument.Parse(audit.DetailsJson);
            Assert.Equal("partial-failure", audit.Outcome);
            Assert.Equal(ScopedPlaybackScrobbleDelivery.CheckpointKey(payload), audit.CorrelationId);
            Assert.Equal("playback_scrobble_unauthorized", details.RootElement.GetProperty("reasonCode").GetString());
            Assert.Equal(2, details.RootElement.GetProperty("providerCount").GetInt32());
            Assert.Equal(["Last.fm", "ListenBrainz"], details.RootElement.GetProperty("providerNames")
                .EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(2, details.RootElement.GetProperty("attemptedCount").GetInt32());
            Assert.Equal(1, details.RootElement.GetProperty("deliveredCount").GetInt32());
            Assert.Equal(1, details.RootElement.GetProperty("failedCount").GetInt32());
            Assert.True(details.RootElement.GetProperty("durationMilliseconds").GetInt64() >= 0);
            Assert.DoesNotContain("track-1", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("session", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payload", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        }

        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => delivery.DeliverAsync(payload, default));
        Assert.Equal(1, healthy.Successes);
    }

    [Fact]
    public async Task ScopedDelivery_IgnoredOutcomeIsCheckpointedWithoutRetry()
    {
        var target = new Target("lastfm", true)
        {
            Result = ScopedPlaybackScrobbleResult.Ignored("1", "Timestamp is too old", "{\"ignored\":1}")
        };
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new Checkpoints());

        await delivery.DeliverAsync(Payload(), default);
        await delivery.DeliverAsync(Payload(), default);

        Assert.Equal(1, target.Successes);
    }

    [Fact]
    public async Task ScopedDelivery_RetryingOutcomePreservesProviderDelay()
    {
        var target = new Target("lastfm", true)
        {
            Result = ScopedPlaybackScrobbleResult.Retrying("29", "Last.fm could not accept the listen yet.",
                TimeSpan.FromSeconds(42))
        };
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new Checkpoints());

        var failure = await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() =>
            delivery.DeliverAsync(Payload(), default));

        Assert.True(failure.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(42), failure.RetryAfter);
    }

    [Theory]
    [InlineData(59, 0)]
    [InlineData(60, 1)]
    public async Task CompletedScrobbleHonorsHalfTrackThreshold(int playedSeconds, int expected)
    {
        var target = new Target("lastfm", true); var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new Checkpoints());
        await delivery.DeliverAsync(Payload() with { PositionTicks = TimeSpan.FromSeconds(playedSeconds).Ticks }, default);
        Assert.Equal(expected, target.Successes);
    }

    [Fact]
    public async Task EligibleProgressScrobblesOnceAndStopDoesNotDuplicateIt()
    {
        var target = new Target("lastfm", true);
        var activity = new allstarr.Services.Common.PlaybackDeliveryActivityStore();
        var delivery = new ScopedPlaybackScrobbleDelivery(
            factory,
            [target],
            new Checkpoints(),
            activity: activity);
        var progress = Payload() with
        {
            Transition = PlaybackTransition.Progress,
            PositionTicks = TimeSpan.FromSeconds(60).Ticks
        };

        await delivery.DeliverAsync(progress, default);
        await delivery.DeliverAsync(progress with { Transition = PlaybackTransition.Stop }, default);

        Assert.Equal(1, target.Successes);
        Assert.True(activity.WasDelivered(progress.ItemId, progress.DeviceId!));
    }

    [Fact]
    public async Task ExplicitSubsonicSubmission_BypassesPlaybackThreshold()
    {
        var target = new Target("lastfm", true);
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new Checkpoints());

        await delivery.DeliverAsync(Payload() with
        {
            Transition = PlaybackTransition.Submission,
            PositionTicks = null
        }, default);

        Assert.Equal(1, target.Successes);
    }

    [Fact]
    public async Task Submission_WritesCompletedHabitSignal()
    {
        await new PlaybackSignalPipeline(jobs).RecordAsync(
            Signal(PlaybackTransition.Submission, "track-1", 0));
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]);
        var writer = new Writer();
        var handler = new PlaybackSignalJobHandler(writer, new Scrobbles(), new Lyrics(), factory);

        var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Equal(1, writer.Calls);
    }

    [Theory]
    [InlineData(10, "skip")]
    [InlineData(60, "complete")]
    public async Task StopSignal_ClassifiesShortAndCompletedPlayback(int playedSeconds, string expectedType)
    {
        await new PlaybackSignalPipeline(jobs).RecordAsync(
            Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(playedSeconds).Ticks));
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]);
        var writer = new Writer();
        var handler = new PlaybackSignalJobHandler(
            writer,
            new Scrobbles(),
            new Lyrics(),
            factory,
            new PlaybackTrackResolver(factory));

        var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Equal(expectedType, writer.LastType);
    }

    [Fact]
    public async Task StartProgressAndStopUpdateOneDurableOccurrence()
    {
        var startedAt = clock.UtcNow;
        var pipeline = new PlaybackSignalPipeline(jobs);
        Assert.True(await pipeline.RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0)));
        clock.UtcNow = startedAt.AddSeconds(60);
        Assert.True(await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "track-1", TimeSpan.FromSeconds(60).Ticks)));
        clock.UtcNow = startedAt.AddSeconds(61);
        Assert.True(await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(61).Ticks)));

        var handler = new PlaybackSignalJobHandler(
            new Writer(), new Scrobbles(), new Lyrics(), factory, new PlaybackTrackResolver(factory));
        for (var index = 0; index < 3; index++)
        {
            var claim = await jobs.ClaimNextAsync($"worker-{index}", [PlaybackSignalPipeline.JobType]);
            Assert.NotNull(claim);
            Assert.Equal(DurableJobCompletionKind.Succeeded,
                (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
        }

        await using var db = await factory.CreateDbContextAsync();
        var occurrence = Assert.Single(await db.ListeningEvents.AsNoTracking().ToListAsync());
        Assert.Equal(ListeningEventState.Completed, occurrence.State);
        Assert.Equal(startedAt, occurrence.StartedAt);
        Assert.Equal(startedAt, occurrence.ListenedAt);
        Assert.Equal(TimeSpan.FromSeconds(61).Ticks, occurrence.PositionTicks);
        Assert.Equal(120_000, occurrence.DurationMilliseconds);
        Assert.Equal("Track", occurrence.Title);
        Assert.Equal("Artist", occurrence.Artist);
        Assert.NotNull(occurrence.LibraryTrackId);
    }

    [Fact]
    public async Task ExternalSubmissionPersistsWithoutALibraryTrack()
    {
        const string itemId = "ext-deezer-song-provider-track";
        var pipeline = new PlaybackSignalPipeline(jobs);
        Assert.True(await pipeline.RecordAsync(Signal(PlaybackTransition.Submission, itemId, 0)));
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]);
        var resolver = new PlaybackTrackResolver(factory,
            [new BackendMetadataResolver(new("External title", "External artist", "External album", null, 180))]);
        var handler = new PlaybackSignalJobHandler(
            new RecommendationSignalWriter(factory, clock), new Scrobbles(), new Lyrics(), factory, resolver);

        Assert.Equal(DurableJobCompletionKind.Succeeded,
            (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);

        await using var db = await factory.CreateDbContextAsync();
        var occurrence = Assert.Single(await db.ListeningEvents.AsNoTracking().ToListAsync());
        Assert.Equal(ListeningEventState.Completed, occurrence.State);
        Assert.Null(occurrence.LibraryTrackId);
        Assert.Equal("External title", occurrence.Title);
        Assert.Equal("deezer", occurrence.ProviderId);
        Assert.Equal("deezer:provider-track", occurrence.ProviderTrackReference);
        Assert.Empty(await db.ListeningSignals.ToListAsync());
    }

    [Fact]
    public async Task SubmittedListenKeepsProvidedMetadataWithoutRelaying()
    {
        var request = Signal(PlaybackTransition.Submission, "listenbrainz:track", 0) with
        {
            SubmittedTrack = new(null, null, "listenbrainz:track", "Submitted title", "Submitted artist",
                "Submitted album", 181_000, RecordingMusicBrainzId: "12345678-1234-1234-1234-123456789abc"),
            RelayExternally = false,
            SourceKind = "listenbrainz-api"
        };
        Assert.True(await new PlaybackSignalPipeline(jobs).RecordAsync(request));
        var claim = await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]);
        var scrobbles = new Scrobbles();
        var handler = new PlaybackSignalJobHandler(
            new RecommendationSignalWriter(factory, clock), scrobbles, new Lyrics(), factory, new RejectResolver());

        Assert.Equal(DurableJobCompletionKind.Succeeded,
            (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);

        await using var db = await factory.CreateDbContextAsync();
        var occurrence = Assert.Single(await db.ListeningEvents.AsNoTracking().ToListAsync());
        Assert.Equal("Submitted title", occurrence.Title);
        Assert.Equal("Submitted artist", occurrence.Artist);
        Assert.Equal("listenbrainz-api", occurrence.SourceKind);
        Assert.Equal(0, scrobbles.Calls);
    }

    [Fact]
    public async Task ScopedDelivery_UsesDurableOccurrenceMetadataAndOriginalListenTime()
    {
        var startedAt = clock.UtcNow.AddMinutes(-4);
        var occurrenceKey = new string('b', 64);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ListeningEvents.Add(new ListeningEventRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                OwnerUserId = user,
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                LibraryScopeId = "music",
                OccurrenceKey = occurrenceKey,
                State = ListeningEventState.Completed,
                StartedAt = startedAt,
                ListenedAt = startedAt,
                UpdatedAt = clock.UtcNow,
                DurationMilliseconds = 180_000,
                ClientClass = "Finamp",
                DeviceClass = "mobile",
                SourceKind = "protocol",
                TrackReference = "track-1",
                Title = "Durable title",
                Artist = "Durable artist",
                Album = "Durable album",
                AlbumArtist = "Durable album artist",
                RecordingMusicBrainzId = "11111111-1111-1111-1111-111111111111",
                TrackNumber = 4,
                ChosenByUser = false
            });
            await db.SaveChangesAsync();
        }
        var target = new Target("lastfm", true);
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new Checkpoints());

        await delivery.DeliverAsync(Payload() with
        {
            Transition = PlaybackTransition.Submission,
            PositionTicks = null,
            OccurrenceKey = occurrenceKey,
            ObservedAt = clock.UtcNow
        }, default);

        Assert.Equal(startedAt, target.LastObservedAt);
        Assert.Equal(new ScopedPlaybackTrack(
            "Durable title", "Durable artist", "Durable album", 180_000,
            "Durable album artist", "11111111-1111-1111-1111-111111111111", 4, false, "Finamp", "mobile"),
            target.LastTrack);
    }

    [Fact]
    public async Task ProviderTrackId_ResolvesForScopedScrobbleDelivery()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            var track = await db.LibraryTracks.SingleAsync();
            track.ProviderIdsJson = "{\"deezer\":\"provider-track\"}";
            await db.SaveChangesAsync();
        }
        var target = new Target("lastfm", true);
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new Checkpoints());

        await delivery.DeliverAsync(Payload() with
        {
            ItemId = "deezer:provider-track",
            Transition = PlaybackTransition.Submission,
            PositionTicks = null
        }, default);

        Assert.Equal(1, target.Successes);
    }

    [Theory]
    [InlineData(29_000, 29, false)]
    [InlineData(600_000, 239, false)]
    [InlineData(600_000, 240, true)]
    public void CompletedScrobbleHonorsMinimumDurationAndFourMinuteCap(long durationMs, int playedSeconds, bool expected) =>
        Assert.Equal(expected, ScopedPlaybackScrobbleDelivery.EligibleForCompletedScrobble(durationMs, TimeSpan.FromSeconds(playedSeconds).Ticks));

    private PlaybackSignalRequest Signal(PlaybackTransition transition, string item, long ticks) => new(
        new(ProtocolKind.Jellyfin, "backend", "principal", new AllstarrPrincipal(tenant, user, "jellyfin", "backend", "principal", "User", false),
        "correlation", clock.UtcNow.AddMinutes(1), default, libraryScopeId: "music"), transition, item, "device", "session", ticks, clock.UtcNow);
    private PlaybackSignalPayload Payload() => new(new(tenant, user, "jellyfin", "backend", "music"), PlaybackTransition.Stop,
        "track-1", "device", "session", TimeSpan.FromSeconds(100).Ticks, clock.UtcNow, new string('a', 64));
    public async Task DisposeAsync() => await database.DisposeAsync();
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext> { public AllstarrDbContext CreateDbContext() => new(options); public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken token = default) => Task.FromResult(new AllstarrDbContext(options)); }
    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } }
    private sealed class Writer : IIdempotentRecommendationSignalWriter { private readonly HashSet<string> keys = []; public int Calls; public string? LastType; public Task<bool> WriteAsync(IntelligenceScope s, string t, string k, double v, DateTimeOffset o, CancellationToken c = default) { Calls++; LastType = t; return Task.FromResult(true); } public Task<bool> WriteIdempotentAsync(IntelligenceScope s, string t, string k, double v, DateTimeOffset o, string key, Guid job, CancellationToken c = default) { if (keys.Add(key)) { Calls++; LastType = t; } return Task.FromResult(true); } }
    private sealed class Scrobbles : IScopedPlaybackScrobbleDelivery { public int Calls; public int Successes; public bool FailFirst; public Task DeliverAsync(PlaybackSignalPayload p, CancellationToken c) { Calls++; if (FailFirst) { FailFirst = false; throw new IOException(); } Successes++; return Task.CompletedTask; } }
    private sealed class Lyrics : IPlaybackLyricsPrefetch { public int Calls; public Task PrefetchAsync(PlaybackSignalPayload p, CancellationToken c) { Calls++; return Task.CompletedTask; } }
    private sealed class RejectResolver : IPlaybackTrackResolver
    {
        public Task<PlaybackTrackSnapshot?> ResolveAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Submitted metadata should bypass lookup.");
    }
    private sealed class Target(string id, bool configured) : IExactScopePlaybackScrobbleTarget
    {
        public string ProviderId => id;
        public int Successes;
        public int Calls;
        public bool FailFirst;
        public bool Reject;
        public CancellationTokenSource? CancelSource;
        public ScopedPlaybackScrobbleResult Result = ScopedPlaybackScrobbleResult.Delivered();
        public DateTimeOffset? LastObservedAt;
        public ScopedPlaybackTrack? LastTrack;
        public Task<bool> IsConfiguredAsync(IntelligenceScope s, CancellationToken c) => Task.FromResult(configured);
        public Task<ScopedPlaybackScrobbleResult> DeliverAsync(IntelligenceScope s, PlaybackTransition t, ScopedPlaybackTrack track, long? p,
            DateTimeOffset o, string key, CancellationToken c)
        {
            Calls++;
            if (CancelSource != null) { CancelSource.Cancel(); throw new OperationCanceledException(c); }
            if (Reject) throw new UnauthorizedAccessException();
            if (FailFirst) { FailFirst = false; throw new IOException(); }
            LastObservedAt = o;
            LastTrack = track;
            Successes++;
            return Task.FromResult(Result);
        }
    }
    private sealed class Checkpoints : IPlaybackDeliveryCheckpointStore
    {
        private readonly HashSet<string> values = [];
        public Task<bool> IsCompletedAsync(Guid t, Guid u, string k, string target, CancellationToken c) =>
            Task.FromResult(values.Contains(k + target));
        public Task RecordAsync(Guid t, Guid u, string occurrenceKey, string signalKey,
            PlaybackScrobbleDeliveryKind kind, string target, ScopedPlaybackScrobbleResult result,
            CancellationToken c)
        {
            if (result.Outcome is ScopedPlaybackScrobbleOutcome.Delivered or ScopedPlaybackScrobbleOutcome.Ignored)
                values.Add(signalKey + target);
            return Task.CompletedTask;
        }
    }
    private sealed class EmptyServices : IServiceProvider { public static readonly EmptyServices Instance = new(); public object? GetService(Type t) => null; }
    private sealed class BackendMetadataResolver(allstarr.Services.Common.PlaybackTrackMetadata metadata)
        : allstarr.Services.Common.IPlaybackMetadataResolver
    {
        public Task<allstarr.Services.Common.PlaybackTrackMetadata?> ResolveAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<allstarr.Services.Common.PlaybackTrackMetadata?>(metadata);

        public Task<allstarr.Services.Common.PlaybackArtwork?> ResolveArtworkAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<allstarr.Services.Common.PlaybackArtwork?>(null);
    }
}

public sealed class PlaybackOccurrenceKeyTests
{
    private readonly IntelligenceScope scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "jellyfin", "backend", "music");

    [Fact]
    public void SessionVariantsShareOneOccurrenceKey()
    {
        var startedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var start = PlaybackSignalPipeline.CreateOccurrenceKey(scope, "track", "device", "session", 0, startedAt);
        var stop = PlaybackSignalPipeline.CreateOccurrenceKey(scope, "track", "device", "session",
            TimeSpan.FromMinutes(3).Ticks, startedAt.AddMinutes(3));

        Assert.Equal(start, stop);
    }

    [Fact]
    public void InferredVariantsShareAStartButLaterReplayIsDistinct()
    {
        var startedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var start = PlaybackSignalPipeline.CreateOccurrenceKey(scope, "track", "device", null, 0, startedAt);
        var progress = PlaybackSignalPipeline.CreateOccurrenceKey(scope, "track", "device", null,
            TimeSpan.FromSeconds(60).Ticks, startedAt.AddSeconds(60));
        var replay = PlaybackSignalPipeline.CreateOccurrenceKey(scope, "track", "device", null, 0,
            startedAt.AddSeconds(31));

        Assert.Equal(start, progress);
        Assert.NotEqual(start, replay);
    }

    [Fact]
    public void ProtocolAndUserRemainPartOfTheOccurrenceScope()
    {
        var observedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var jellyfin = PlaybackSignalPipeline.CreateOccurrenceKey(scope, "track", "device", "event", null, observedAt);
        var subsonic = PlaybackSignalPipeline.CreateOccurrenceKey(scope with { Protocol = "subsonic" },
            "track", "device", "event", null, observedAt);
        var otherUser = PlaybackSignalPipeline.CreateOccurrenceKey(scope with { OwnerUserId = Guid.NewGuid() },
            "track", "device", "event", null, observedAt);

        Assert.NotEqual(jellyfin, subsonic);
        Assert.NotEqual(jellyfin, otherUser);
    }

    [Fact]
    public void NowPlayingAndCompletedScrobblesUseSeparateCheckpointKeys()
    {
        var occurrence = new string('c', 64);
        var nowPlaying = new PlaybackSignalPayload(scope, PlaybackTransition.Start, "track", "device", "session",
            0, DateTimeOffset.UtcNow, new string('a', 64), occurrence);
        var completed = nowPlaying with
        {
            Transition = PlaybackTransition.Stop,
            PositionTicks = TimeSpan.FromMinutes(2).Ticks,
            SignalKey = new string('b', 64)
        };

        Assert.NotEqual(ScopedPlaybackScrobbleDelivery.CheckpointKey(nowPlaying),
            ScopedPlaybackScrobbleDelivery.CheckpointKey(completed));
        Assert.Equal(PlaybackSignalPipeline.Hash($"{occurrence}|completed"),
            ScopedPlaybackScrobbleDelivery.CheckpointKey(completed));
    }

    [Theory]
    [InlineData(PlaybackTransition.Start, true, 0, ListeningEventState.Playing)]
    [InlineData(PlaybackTransition.InferredStart, true, 0, ListeningEventState.Playing)]
    [InlineData(PlaybackTransition.Progress, true, 60, ListeningEventState.Completed)]
    [InlineData(PlaybackTransition.Stop, true, 60, ListeningEventState.Completed)]
    [InlineData(PlaybackTransition.Stop, true, 10, ListeningEventState.Skipped)]
    [InlineData(PlaybackTransition.InferredStop, false, 10, ListeningEventState.Abandoned)]
    [InlineData(PlaybackTransition.Submission, false, 0, ListeningEventState.Completed)]
    public void JellyfinAndSubsonicTransitionMatrixClassifiesEveryVariant(
        PlaybackTransition transition,
        bool knownTrack,
        int playedSeconds,
        ListeningEventState expected)
    {
        var payload = new PlaybackSignalPayload(
            scope, transition, "track", "device", "session",
            TimeSpan.FromSeconds(playedSeconds).Ticks, DateTimeOffset.Parse("2026-01-01T00:02:00Z"),
            new string('a', 64));
        var track = knownTrack
            ? new PlaybackTrackSnapshot(null, null, "track", "Track", "Artist", null, 120_000)
            : null;

        Assert.Equal(expected, PlaybackSignalJobHandler.Classify(payload, track));
    }
}
