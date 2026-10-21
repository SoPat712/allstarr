using System.Data.Common;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Playback;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundDelivery_FirstDestinationFailureStillAttemptsSecondDestination(bool throws)
    {
        var rejected = new RecoveryTarget(await AddScrobbleAccountAsync("listenbrainz")) { Reject = !throws, FailPreparation = throws };
        var healthy = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [rejected, healthy]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        var claim = (await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]))!;
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [rejected, healthy], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks);
        var handler = new PlaybackSignalJobHandler(new Writer(), delivery, new Lyrics(), factory, playbackTracks, recoveryJobs: jobs);
        var completion = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
        Assert.Equal(throws ? DurableJobCompletionKind.Retry : DurableJobCompletionKind.Failed, completion.Kind);
        Assert.Equal(1, healthy.Sends);
        Assert.Equal(throws ? 0 : 1, rejected.Sends);
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.PlaybackDeliveryCheckpoints.OrderBy(x => x.TargetId).ToArrayAsync();
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, rows[0].State);
        Assert.Equal(throws ? ScopedPlaybackScrobbleOutcome.Retrying : ScopedPlaybackScrobbleOutcome.PermanentFailure, rows[1].State);
        Assert.Equal(throws ? "transport-failure" : "http-400", rows[1].ProviderCode);
        Assert.Equal(throws ? 0 : 1, rows[1].SendCount);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("exhausted")]
    public async Task ScrobbleJobEnd_RequeuesUnsentDestinationOnly(string outcome)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        var claim = (await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]))!;
        if (outcome == "exhausted")
        {
            await using var setup = await factory.CreateDbContextAsync();
            (await setup.Jobs.SingleAsync()).MaxAttempts = 1;
            await setup.SaveChangesAsync();
        }
        await jobs.CompleteAsync(claim, outcome switch
        {
            "success" => DurableJobCompletion.Success(),
            "cancel" => DurableJobCompletion.Cancelled(),
            "exhausted" => DurableJobCompletion.Retry("temporary", "Try later"),
            _ => DurableJobCompletion.Failure("rejected", "Rejected")
        });
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks, recoveryJobs: jobs);
        await ((IDurableJobRecoveryHandler)handler).RecoverCompletedAsync(claim, default);
        await ((IDurableJobRecoveryHandler)handler).RecoverCompletedAsync(claim, default);
        await AssertRecoveredDestinationAsync(target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrobbleRecovery_RequeuesOrphanAfterQueuedCancellationOrRestart(bool restart)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync();
            if (restart)
            {
                job.State = DurableJobState.Running;
                job.CancellationRequestedAt = clock.UtcNow;
                await db.SaveChangesAsync();
            }
            else await jobs.RequestCancellationAsync(job.Id, user);
        }
        if (restart) await jobs.RecoverInterruptedAsync();
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks, recoveryJobs: jobs);
        await handler.RecoverAsync(default);
        await handler.RecoverAsync(default);
        await AssertRecoveredDestinationAsync(target);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("ambiguous")]
    [InlineData("verification-pending")]
    public async Task ScrobbleRecovery_PreservesActiveJobAndUncertainSend(string evidence)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks, recoveryJobs: jobs);
        await handler.RecoverAsync(default);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.Jobs.ToArrayAsync());
        var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
        row.AttemptStartedAt = evidence == "attempt" ? clock.UtcNow : null;
        row.SendCount = evidence == "attempt" ? 1 : 0;
        row.ProviderCode = evidence == "attempt" ? "pending" : evidence;
        row.ReplaySafe = false;
        (await db.Jobs.SingleAsync()).State = DurableJobState.Failed;
        await db.SaveChangesAsync();
        await handler.RecoverAsync(default);
        var verification = Assert.Single(await db.Jobs.AsNoTracking().Where(x => x.State == DurableJobState.Pending).ToArrayAsync());
        Assert.StartsWith("scrobble-verify:", verification.IdempotencyKey);
        var payload = JsonSerializer.Deserialize<PlaybackSignalPayload>(verification.PayloadJson)!;
        Assert.Equal(["lastfm"], payload.DestinationTargets);
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() =>
            new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks)
                .DeliverAsync(payload, default));
        Assert.Equal(0, target.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrobbleRestart_PreservesResumableJobOrRecoversExhaustedJob(bool exhausted)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await jobs.ClaimNextAsync("interrupted", [PlaybackSignalPipeline.JobType]);
        var options = new DurableJobOptions { MaxConsecutiveInterruptions = exhausted ? 1 : 3 };
        var restartedJobs = new DurableJobQueue(factory, options, new JobPayloadPolicy(options), clock);
        await restartedJobs.RecoverInterruptedAsync();
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks, recoveryJobs: restartedJobs);
        await handler.RecoverAsync(default);
        await handler.RecoverAsync(default);
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(exhausted ? 2 : 1, await db.Jobs.CountAsync());
        await AssertRecoveredDestinationAsync(target);
    }

    [Fact]
    public async Task ScrobbleRecovery_RequeuesSavedDestinationWhenOriginalJobIsMissing()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Jobs.Remove(await db.Jobs.SingleAsync());
            (await db.PlaybackDeliveryCheckpoints.SingleAsync()).UpdatedAt = clock.UtcNow.AddHours(-2);
            await db.SaveChangesAsync();
        }
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks, recoveryJobs: jobs);
        await handler.RecoverAsync(default);
        await handler.RecoverAsync(default);
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Single(await db.Jobs.ToArrayAsync());
        await AssertRecoveredDestinationAsync(target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrobblePendingRetry_RestoresCompletedListenPayload(bool automatic)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        var pipeline = new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]));
        await pipeline.RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks, recoveryJobs: jobs);
        while (await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]) is { } claim)
            await jobs.CompleteAsync(claim, await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.Completed);
            foreach (var nowPlaying in await db.PlaybackDeliveryCheckpoints.Where(x => x.Kind == PlaybackScrobbleDeliveryKind.NowPlaying).ToArrayAsync())
                nowPlaying.State = ScopedPlaybackScrobbleOutcome.Delivered;
            row.SignalPayloadJson = null;
            row.SubmissionJson = null;
            row.State = ScopedPlaybackScrobbleOutcome.Retrying;
            row.ProviderCode = "pending";
            await db.SaveChangesAsync();
            if (!automatic)
                Assert.True(await new PlaybackScrobbleRepair(factory, jobs).ResolveAsync(user, row.Id, row.Revision, "retry", 1, default));
        }
        if (automatic) await handler.RecoverAsync(default);
        await AssertRecoveredDestinationAsync(target);
    }

    [Fact]
    public async Task ScrobbleStartReservation_DoesNotBecomeACompletedListen()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        await using (var before = await factory.CreateDbContextAsync())
            Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored,
                (await before.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.Completed)).State);
        var claim = (await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]))!;
        await jobs.CompleteAsync(claim, DurableJobCompletion.Success());
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks, recoveryJobs: jobs);
        await handler.RecoverAsync(default);
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.Completed);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, row.State);
        Assert.Null(row.SubmissionJson);
        Assert.Equal(0, target.Sends);
        await AssertNoScrobbleAttentionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PlaybackScrobbleRepair(factory, jobs, playbackTracks).ResolveAsync(user, row.Id, row.Revision, "retry", 1, default));
    }

    [Theory]
    [InlineData(PlaybackTransition.Stop)]
    [InlineData(PlaybackTransition.InferredStop)]
    public async Task ScrobbleSkippedListen_StaysIgnoredAfterRecoveryAndLateCompletion(PlaybackTransition ending)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        var pipeline = new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]));
        await pipeline.RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        await pipeline.RecordAsync(Signal(ending, "track-1", TimeSpan.FromSeconds(10).Ticks));
        await DrainScrobbleSignalsAsync(target);
        await new PlaybackScrobbleRepair(factory, jobs, playbackTracks).RecoverPendingAsync(default);
        await AssertNoScrobbleAttentionAsync();
        await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await DrainScrobbleSignalsAsync(target);
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.Completed);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, row.State);
        Assert.Equal(0, row.SendCount);
        Assert.Null(row.SubmissionJson);
        Assert.Equal(1, target.Sends);
        Assert.Empty(await db.AuditEvents.Where(x => x.Category == "scrobble" && x.Action == "delivered").ToArrayAsync());
        await AssertNoScrobbleAttentionAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScrobbleStartup_ClosesExpiredEmptyReservationsWithoutSending(bool bound, bool completedHistory)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        var pipeline = new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]));
        await pipeline.RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Jobs.RemoveRange(await db.Jobs.ToArrayAsync());
            db.PlaybackDeliveryCheckpoints.RemoveRange(await db.PlaybackDeliveryCheckpoints.Where(x => x.Kind == PlaybackScrobbleDeliveryKind.NowPlaying).ToArrayAsync());
            var row = await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.Completed);
            row.State = ScopedPlaybackScrobbleOutcome.Retrying;
            row.ProviderCode = "pending";
            row.DestinationJson = bound ? row.DestinationJson : null;
            row.UpdatedAt = clock.UtcNow.AddHours(-2);
            if (completedHistory)
                db.ListeningEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = user,
                    Protocol = "jellyfin",
                    BackendInstanceId = "backend",
                    OccurrenceKey = row.OccurrenceKey!,
                    TrackReference = "track-1",
                    SourceKind = "protocol",
                    State = ListeningEventState.Completed,
                    Title = "Track",
                    Artist = "Artist",
                    DurationMilliseconds = 120_000,
                    ListenedAt = clock.UtcNow.AddHours(-2),
                    UpdatedAt = clock.UtcNow.AddHours(-2)
                });
            await db.SaveChangesAsync();
        }
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks, recoveryJobs: jobs);
        await handler.RecoverAsync(default);
        await handler.RecoverAsync(default);
        await AssertNoScrobbleAttentionAsync();
        await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await DrainScrobbleSignalsAsync(target);
        await using var verify = await factory.CreateDbContextAsync();
        var ignored = await verify.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, ignored.State);
        Assert.Equal(0, ignored.SendCount);
        Assert.Equal(0, target.Sends);
        Assert.DoesNotContain(await verify.Jobs.ToArrayAsync(), x => x.IdempotencyKey.StartsWith("scrobble-recover:"));
    }

    [Fact]
    public async Task ScrobbleExpiredStart_CannotBeRevivedByALateObservation()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        var pipeline = new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]));
        await pipeline.RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.Completed);
            row.UpdatedAt = clock.UtcNow.AddHours(-2);
            await db.SaveChangesAsync();
        }
        await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await DrainScrobbleSignalsAsync(target);
        await using var verify = await factory.CreateDbContextAsync();
        var rowAfter = await verify.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.Completed);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, rowAfter.State);
        Assert.Equal(0, rowAfter.SendCount);
        Assert.Equal(1, target.Sends);
        await AssertNoScrobbleAttentionAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrobbleRecovery_UsesIndexedPagesAndBoundsEachSweep(bool startup)
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            for (var index = 0; index < 105; index++)
                db.PlaybackDeliveryCheckpoints.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = user,
                    SignalKey = index.ToString("x64"),
                    TargetId = "lastfm",
                    State = ScopedPlaybackScrobbleOutcome.Retrying,
                    ProviderCode = "pending",
                    UpdatedAt = clock.UtcNow.AddHours(-2)
                });
            await db.SaveChangesAsync();
        }
        var capture = new RecoveryQueryCapture();
        var queryFactory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(capture).Options);
        var repair = new PlaybackScrobbleRepair(queryFactory, jobs, playbackTracks);
        if (startup)
            await new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), queryFactory, playbackTracks, recoveryJobs: jobs).RecoverAsync(default);
        else await repair.RecoverPendingAsync(default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(startup ? 105 : 100, await db.PlaybackDeliveryCheckpoints.CountAsync(x => x.State == ScopedPlaybackScrobbleOutcome.Ignored));
            Assert.Equal(startup ? 0 : 5, await db.PlaybackDeliveryCheckpoints.CountAsync(x => x.State == ScopedPlaybackScrobbleOutcome.Retrying));
        }
        await repair.RecoverPendingAsync(default);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(105, await verify.PlaybackDeliveryCheckpoints.CountAsync(x => x.State == ScopedPlaybackScrobbleOutcome.Ignored));
        Assert.Empty(await verify.Jobs.ToArrayAsync());
        var query = capture.Queries.Last(x => x.Sql.Contains("WHERE", StringComparison.Ordinal));
        await verify.Database.OpenConnectionAsync();
        await using var explain = verify.Database.GetDbConnection().CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + query.Sql;
        foreach (var (name, value) in query.Parameters)
        {
            var parameter = explain.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            explain.Parameters.Add(parameter);
        }
        await using var reader = await explain.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, x => x.Contains("SEARCH", StringComparison.Ordinal) &&
            x.Contains("sqlite_autoindex_playback_delivery_checkpoints", StringComparison.Ordinal));
    }

    private sealed class RecoveryQueryCapture : DbCommandInterceptor
    {
        public List<(string Sql, (string Name, object Value)[] Parameters)> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"playback_delivery_checkpoints\"", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"Recoverable\"", StringComparison.Ordinal) &&
                command.CommandText.Contains("LIMIT", StringComparison.Ordinal))
                Queries.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(x => (x.ParameterName, x.Value!)).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task DrainScrobbleSignalsAsync(RecoveryTarget target)
    {
        var handler = new PlaybackSignalJobHandler(new Writer(),
            new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks),
            new Lyrics(), factory, playbackTracks, recoveryJobs: jobs);
        var count = 0;
        while (await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]) is { } claim)
        {
            Assert.True(++count <= 8, "Playback recovery must remain bounded.");
            await jobs.CompleteAsync(claim, await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default));
            await handler.RecoverCompletedAsync(claim, default);
        }
    }

    private async Task AssertNoScrobbleAttentionAsync()
    {
        var controller = new ScrobbleDeliveriesController(factory, new(factory, jobs, playbackTracks))
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = user.ToString(),
            UserName = "Listener",
            AllstarrUserId = user,
            IsAdministrator = false,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var response = Assert.IsType<OkObjectResult>(await controller.List(attentionOnly: true));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task HomeScrobbleCount_ExcludesIncompleteReservations()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var code in new[] { "listen-incomplete", "listen-not-completed", "provider-ignored", "accepted" })
                db.PlaybackDeliveryCheckpoints.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = user,
                    SignalKey = PlaybackSignalPipeline.Hash(code),
                    TargetId = "lastfm",
                    State = code == "accepted" ? ScopedPlaybackScrobbleOutcome.Delivered : ScopedPlaybackScrobbleOutcome.Ignored,
                    ProviderCode = code,
                    UpdatedAt = clock.UtcNow
                });
            await db.SaveChangesAsync();
        }
        await using var services = new ServiceCollection().AddSingleton<IDbContextFactory<AllstarrDbContext>>(factory)
            .AddSingleton(new DurablePlaylistProjectionReader(factory, null!)).BuildServiceProvider();
        var matches = new allstarr.Core.Matching.TrackMatchCommandService(factory, new allstarr.Core.Matching.TrackMatchDecisionEngine(),
            new ProviderAccountResolver(factory), clock, Moq.Mock.Of<allstarr.Core.Protocols.IBackendLibraryAccessResolver>());
        var status = new allstarr.Services.Common.ProviderStatusManager(new ConfigurationBuilder().Build(), null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<allstarr.Services.Common.ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()),
            Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()));
        var controller = new AdminUiController(new ConfigurationBuilder().Build(),
            Options.Create(new SpotifyApiSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new MusicBrainzSettings()), null!, status,
            new ProviderAccountOptions(), matches)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = user.ToString(),
            UserName = "Listener",
            AllstarrUserId = user,
            IsAdministrator = false,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var response = Assert.IsType<OkObjectResult>(await controller.GetHome());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        Assert.Equal(2, json.RootElement.GetProperty("stats").GetProperty("scrobbleDeliveries").GetInt32());
    }

    [Fact]
    public async Task ScrobbleAcceptance_CompletionAttachesPayloadToEarlierReservation()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        var pipeline = new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]));
        await pipeline.RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await DrainScrobbleSignalsAsync(target);
        await new PlaybackScrobbleRepair(factory, jobs, playbackTracks).RecoverPendingAsync(default);
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.Completed);
        Assert.NotNull(row.SignalPayloadJson);
        Assert.Equal(PlaybackTransition.Stop, JsonSerializer.Deserialize<PlaybackSignalPayload>(row.SignalPayloadJson)!.Transition);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, row.State);
        Assert.Equal(1, row.SendCount);
        Assert.Equal(2, target.Sends);
    }

    [Fact]
    public async Task ScrobblePendingRetry_RestoresPayloadFromEligibleJobWithoutLocalHistory()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
            row.SignalPayloadJson = null;
            (await db.Jobs.SingleAsync()).State = DurableJobState.Failed;
            await db.SaveChangesAsync();
            Assert.Empty(await db.ListeningEvents.ToArrayAsync());
            Assert.True(await new PlaybackScrobbleRepair(factory, jobs, playbackTracks).ResolveAsync(user, row.Id, row.Revision, "retry", 1, default));
        }
        await AssertRecoveredDestinationAsync(target);
    }

    [Fact]
    public async Task ScrobbleRecovery_UnprocessedRecoveryJobRequiresAttentionInsteadOfLooping()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = DurableJobState.Failed;
            await db.SaveChangesAsync();
        }
        var handler = new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks, recoveryJobs: jobs);
        await handler.RecoverAsync(default);
        var claim = (await jobs.ClaimNextAsync("recovery", [PlaybackSignalPipeline.JobType]))!;
        await jobs.CompleteAsync(claim, DurableJobCompletion.Success());
        await ((IDurableJobRecoveryHandler)handler).RecoverCompletedAsync(claim, default);
        await handler.RecoverAsync(default);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.Jobs.CountAsync());
        var row = await verify.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.Equal(ScopedPlaybackScrobbleOutcome.PermanentFailure, row.State);
        Assert.Equal("delivery-not-attempted", row.ProviderCode);
        Assert.True(await new PlaybackScrobbleRepair(factory, jobs).ResolveAsync(user, row.Id, row.Revision, "retry", 1, default));
        await AssertRecoveredDestinationAsync(target);
    }

    [Fact]
    public async Task ScrobbleEarlyProgress_DoesNotBlockLaterEligibleCompletion()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        var pipeline = new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]));
        var handler = new PlaybackSignalJobHandler(new Writer(),
            new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks),
            new Lyrics(), factory, playbackTracks, recoveryJobs: jobs);
        await pipeline.RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        await DrainAsync();
        await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "track-1", TimeSpan.FromSeconds(20).Ticks));
        await DrainAsync();
        await pipeline.RecordAsync(Signal(PlaybackTransition.Progress, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await DrainAsync();
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.Completed);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, row.State);
        Assert.Equal(1, row.SendCount);
        Assert.Equal(2, target.Sends);

        async Task DrainAsync()
        {
            var count = 0;
            while (await jobs.ClaimNextAsync("worker", [PlaybackSignalPipeline.JobType]) is { } claim)
            {
                Assert.True(++count <= 4, "Playback recovery must have a bounded retry budget.");
                await jobs.CompleteAsync(claim, await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default));
                await ((IDurableJobRecoveryHandler)handler).RecoverCompletedAsync(claim, default);
            }
        }
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("verification-pending")]
    public async Task ScrobbleVerification_SaveFailureKeepsUncertaintyWithoutAttemptMarker(string code)
    {
        await AddScrobbleAccountAsync();
        var http = new VerificationHttp { Mode = "failure" };
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() =>
            VerificationDelivery(http).DeliverAsync(AcceptedScrobblePayload(), default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
            row.AttemptStartedAt = null;
            row.ProviderCode = code;
            row.State = ScopedPlaybackScrobbleOutcome.Retrying;
            await db.SaveChangesAsync();
        }
        http.Mode = "";
        http.Found = true;
        var delivery = new ScopedPlaybackScrobbleDelivery(factory,
            [new allstarr.Services.Scrobbling.LastFmScopedPlaybackScrobbleTarget(new(http),
                new ScopedRecommendationAccountAccessor(factory, new VerificationSecrets()))],
            new FailAcceptedCheckpoint(new EfPlaybackDeliveryCheckpointStore(factory)), playbackTracks);
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => delivery.DeliverAsync(AcceptedScrobblePayload(), default));
        await VerificationDelivery(http).DeliverAsync(AcceptedScrobblePayload(), default);
        Assert.Equal(1, http.Sends);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, (await verify.PlaybackDeliveryCheckpoints.SingleAsync()).State);
    }

    [Fact]
    public async Task ScrobbleRecovery_UnavailableMetadataDoesNotBlockOtherSavedListen()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        var pipeline = new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]));
        await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks) with { PlaySessionId = "unavailable" });
        await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks) with { PlaySessionId = "saved" });
        await using (var db = await factory.CreateDbContextAsync())
        {
            var records = await db.Jobs.ToArrayAsync();
            foreach (var job in records) job.State = DurableJobState.Failed;
            var missing = JsonSerializer.Deserialize<PlaybackSignalPayload>(records.Single(x =>
                JsonSerializer.Deserialize<PlaybackSignalPayload>(x.PayloadJson)!.PlaySessionId == "unavailable").PayloadJson)!;
            (await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.SignalKey == ScopedPlaybackScrobbleDelivery.CheckpointKey(missing))).SignalPayloadJson = null;
            var saved = await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.SignalKey != ScopedPlaybackScrobbleDelivery.CheckpointKey(missing));
            saved.SignalPayloadJson = JsonSerializer.Serialize(JsonSerializer.Deserialize<PlaybackSignalPayload>(saved.SignalPayloadJson!)! with
            {
                SubmittedTrack = new(null, null, "track-1", "Track", "Artist", null, 120_000)
            });
            await db.SaveChangesAsync();
        }
        await new PlaybackScrobbleRepair(factory, jobs, new RejectResolver()).RecoverPendingAsync(default);
        await using var verify = await factory.CreateDbContextAsync();
        var queued = Assert.Single(await verify.Jobs.Where(x => x.State == DurableJobState.Pending).ToArrayAsync());
        Assert.Equal("saved", JsonSerializer.Deserialize<PlaybackSignalPayload>(queued.PayloadJson)!.PlaySessionId);
        Assert.Contains(await verify.PlaybackDeliveryCheckpoints.ToArrayAsync(), x => x.ProviderCode == "listen-incomplete");
    }

    private async Task AssertRecoveredDestinationAsync(RecoveryTarget target)
    {
        var claim = await jobs.ClaimNextAsync("recovery", [PlaybackSignalPipeline.JobType]);
        Assert.NotNull(claim);
        var payload = claim.Payload.Deserialize<PlaybackSignalPayload>()!;
        Assert.Equal(["lastfm"], payload.DestinationTargets);
        await new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks)
            .DeliverAsync(payload, default);
        Assert.Equal(1, target.Sends);
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.Completed);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, row.State);
        Assert.Equal(target.Account.Id, row.DestinationAccountId);
        Assert.Equal(1, row.SendCount);
    }

    private sealed class RecoveryTarget(allstarr.Core.Storage.ProviderAccountRecord account) : IBoundPlaybackScrobbleTarget
    {
        public allstarr.Core.Storage.ProviderAccountRecord Account => account;
        public string ProviderId => account.ProviderId;
        public bool Reject, FailPreparation;
        public int Sends;
        public Task<bool> IsConfiguredAsync(IntelligenceScope scope, CancellationToken token) => Task.FromResult(true);
        public Task<ProviderAccountContext?> FindDestinationAsync(IntelligenceScope scope, CancellationToken token) =>
            Task.FromResult<ProviderAccountContext?>(new(account.Id, account.ProviderId, account.Scope, account.Revision, true, account.OwnerUserId, "test", null));
        public Task<bool> CanReplayAsync(IntelligenceScope scope, ProviderAccountContext destination, CancellationToken token) =>
            FailPreparation ? throw new IOException("Preparation unavailable") : Task.FromResult(false);
        public Task<ScopedPlaybackScrobbleResult> DeliverAsync(IntelligenceScope scope, PlaybackTransition transition,
            ScopedPlaybackTrack track, long? position, DateTimeOffset occurred, string key, CancellationToken token) => throw new NotSupportedException();
        public Task<ScopedPlaybackScrobbleResult> DeliverBoundAsync(IntelligenceScope scope, ProviderAccountContext destination,
            PlaybackTransition transition, ScopedPlaybackTrack track, long? position, DateTimeOffset occurred, string key, CancellationToken token)
        {
            Sends++;
            return Task.FromResult(Reject ? ScopedPlaybackScrobbleResult.Permanent("http-400", "Rejected") : ScopedPlaybackScrobbleResult.Delivered());
        }
    }
}
