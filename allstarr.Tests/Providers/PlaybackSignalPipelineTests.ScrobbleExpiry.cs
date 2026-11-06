using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Playback;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Fact]
    public async Task ScrobbleDismissed_LateJobDoesNotReportDelivery()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        var payload = AcceptedScrobblePayload();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PlaybackDeliveryCheckpoints.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = user,
                SignalKey = ScopedPlaybackScrobbleDelivery.CheckpointKey(payload),
                TargetId = target.ProviderId,
                State = ScopedPlaybackScrobbleOutcome.Ignored,
                ProviderCode = "dismissed",
                UpdatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks)
            .DeliverAsync(payload, default);
        Assert.Equal(0, target.Sends);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.AuditEvents.Where(x => x.Category == "scrobble" && x.Action == "delivered").ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrobbleNowPlaying_RejectionOrPreparationFailureDoesNotRetry(bool preparation)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync()) { Reject = !preparation, FailPreparation = preparation };
        var payload = AcceptedScrobblePayload() with { Transition = PlaybackTransition.Start };
        await new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks)
            .DeliverAsync(payload, default);
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, row.State);
        Assert.Equal("now-playing-expired", row.ProviderCode);
    }

    [Theory]
    [InlineData("400")]
    [InlineData("delivery-not-attempted")]
    [InlineData("transport-failure")]
    public async Task ScrobbleNowPlaying_FailureExpiresWithoutAction(string code)
    {
        var store = new EfPlaybackDeliveryCheckpointStore(factory);
        await store.RecordAsync(user, "occurrence", "notice", PlaybackScrobbleDeliveryKind.NowPlaying, "listenbrainz",
            code == "transport-failure" ? ScopedPlaybackScrobbleResult.Retrying(code, "Try again") :
                ScopedPlaybackScrobbleResult.Permanent(code, "Reconnect and retry", true), default);
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, row.State);
        Assert.Equal("now-playing-expired", row.ProviderCode);
        Assert.False(row.RequiresReauthentication);
        Assert.Null(row.RetryAfter);
    }

    [Theory]
    [InlineData("lastfm", "delivery-not-attempted", false)]
    [InlineData("listenbrainz", "400", true)]
    public async Task ScrobbleStartup_ExpiresOldNoticesIdempotently(string target, string code, bool payload)
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PlaybackDeliveryCheckpoints.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = user,
                SignalKey = "old-notice",
                TargetId = target,
                Kind = PlaybackScrobbleDeliveryKind.NowPlaying,
                State = ScopedPlaybackScrobbleOutcome.PermanentFailure,
                ProviderCode = code,
                SignalPayloadJson = payload ? JsonSerializer.Serialize(AcceptedScrobblePayload()) : null,
                RequiresReauthentication = true,
                UpdatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var repair = new PlaybackScrobbleRepair(factory, jobs, playbackTracks);
        await repair.ReconcileStartupAsync(default);
        await using var first = await factory.CreateDbContextAsync();
        var expired = await first.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, expired.State);
        Assert.Equal("now-playing-expired", expired.ProviderCode);
        Assert.False(expired.RequiresReauthentication);
        await repair.ReconcileStartupAsync(default);
        await using var second = await factory.CreateDbContextAsync();
        Assert.Equal(expired.Revision, (await second.PlaybackDeliveryCheckpoints.SingleAsync()).Revision);
        Assert.Empty(await second.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task ScrobbleNowPlaying_EndedJobExpiresWithoutResend()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = allstarr.Core.Storage.DurableJobState.Failed;
            await db.SaveChangesAsync();
        }
        await new PlaybackScrobbleRepair(factory, jobs, playbackTracks).RecoverPendingAsync(default);
        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.NowPlaying);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, row.State);
        Assert.Equal("now-playing-expired", row.ProviderCode);
        Assert.Single(await verify.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task ScrobbleNowPlaying_StartupDoesNotTreatAnActiveNoticeAsAnUncertainListen()
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync("listenbrainz"));
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Start, "track-1", 0));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var notice = await db.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.NowPlaying);
            notice.AttemptStartedAt = clock.UtcNow;
            notice.ReplaySafe = false;
            await db.SaveChangesAsync();
        }
        await new PlaybackSignalJobHandler(new Writer(), new Scrobbles(), new Lyrics(), factory, playbackTracks, recoveryJobs: jobs).RecoverAsync(default);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Retrying,
            (await verify.PlaybackDeliveryCheckpoints.SingleAsync(x => x.Kind == PlaybackScrobbleDeliveryKind.NowPlaying)).State);
        Assert.Single(await verify.Jobs.ToArrayAsync());
    }

    [Theory]
    [InlineData(PlaybackScrobbleDeliveryKind.NowPlaying, "retry")]
    [InlineData(PlaybackScrobbleDeliveryKind.NowPlaying, "confirm-delivered")]
    [InlineData(PlaybackScrobbleDeliveryKind.Completed, "retry")]
    [InlineData(PlaybackScrobbleDeliveryKind.Completed, "confirm-delivered")]
    public async Task ScrobbleRepair_RejectsNoticeOrMissingCompletedPayload(PlaybackScrobbleDeliveryKind kind, string action)
    {
        await using var db = await factory.CreateDbContextAsync();
        var payload = AcceptedScrobblePayload() with { Transition = PlaybackTransition.Start };
        var row = new PlaybackDeliveryCheckpointEntity
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user,
            SignalKey = ScopedPlaybackScrobbleDelivery.CheckpointKey(payload),
            TargetId = "lastfm",
            Kind = kind,
            State = ScopedPlaybackScrobbleOutcome.PermanentFailure,
            ProviderCode = "ambiguous",
            SignalPayloadJson = kind == PlaybackScrobbleDeliveryKind.NowPlaying ? JsonSerializer.Serialize(payload) : null,
            UpdatedAt = clock.UtcNow
        };
        db.Add(row);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PlaybackScrobbleRepair(factory, jobs, playbackTracks).ResolveAsync(user, row.Id, row.Revision, action, 1, default));
        Assert.Empty(await db.Jobs.ToArrayAsync());
    }

    [Fact]
    public async Task ScrobbleMissingPayload_IsExplainedAndDismissibleByItsOwner()
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = new PlaybackDeliveryCheckpointEntity
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user,
            SignalKey = "missing",
            TargetId = "lastfm",
            State = ScopedPlaybackScrobbleOutcome.PermanentFailure,
            ProviderCode = "payload-unavailable",
            UpdatedAt = clock.UtcNow
        };
        db.Add(row);
        await db.SaveChangesAsync();
        var repair = new PlaybackScrobbleRepair(factory, jobs, playbackTracks);
        var controller = new ScrobbleDeliveriesController(factory, repair)
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
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(item.GetProperty("canResend").GetBoolean());
        Assert.False(item.GetProperty("canConfirm").GetBoolean());
        Assert.True(item.GetProperty("canDismiss").GetBoolean());
        Assert.Contains("saved completed listen", item.GetProperty("unavailableReason").GetString());
        Assert.False(await repair.ResolveAsync(Guid.NewGuid(), row.Id, row.Revision, "dismiss", null, default));
        Assert.True(await repair.ResolveAsync(user, row.Id, row.Revision, "dismiss", null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair.ResolveAsync(user, row.Id, row.Revision, "retry", 1, default));
        await AssertNoScrobbleAttentionAsync();
        Assert.Empty(await db.Jobs.ToArrayAsync());
    }
}
