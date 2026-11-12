using System.Net;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Playback;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Services.Scrobbling;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Theory]
    [InlineData("track-1", false, false, PlaybackTransition.Stop)]
    [InlineData("ext-deezer-song-known", false, false, PlaybackTransition.Stop)]
    [InlineData("ext-deezer-song-known", true, false, PlaybackTransition.Stop)]
    [InlineData("track-1", false, true, PlaybackTransition.Stop)]
    [InlineData("ext-deezer-song-known", true, true, PlaybackTransition.Stop)]
    [InlineData("ext-deezer-song-known", true, false, PlaybackTransition.Submission)]
    public async Task ScrobbleMetadata_TitleAndArtistReachBothServices(string itemId, bool savedMetadata, bool identifiers, PlaybackTransition transition)
    {
        await AddScrobbleAccountAsync();
        await AddScrobbleAccountAsync("listenbrainz");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var track = await db.LibraryTracks.SingleAsync();
            track.DurationMilliseconds = null;
            track.MusicBrainzRecordingId = identifiers ? "11111111-1111-1111-1111-111111111111" : null;
            track.Isrc = identifiers ? "USAAA2600001" : null;
            await db.SaveChangesAsync();
        }
        var resolver = new PlaybackTrackResolver(factory, new TestBackendLibraryAccess(factory, "music"),
            [new BackendMetadataResolver(new("Track", "Artist", null, null, null))]);
        var http = new ScrobbleHttp(HttpStatusCode.OK);
        var accounts = new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets());
        IExactScopePlaybackScrobbleTarget[] targets =
        [new LastFmScopedPlaybackScrobbleTarget(new(http), accounts), new ListenBrainzScopedPlaybackScrobbleTarget(new(http), accounts)];
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, targets))
            .RecordAsync(Signal(transition, itemId, transition == PlaybackTransition.Submission ? 0 : TimeSpan.FromSeconds(100).Ticks) with
            {
                SubmittedTrack = savedMetadata ? new(null, null, itemId, "Track", "Artist", null, null,
                    RecordingMusicBrainzId: identifiers ? "11111111-1111-1111-1111-111111111111" : null,
                    Isrc: identifiers ? "USAAA2600001" : null) : null
            });
        var claim = (await jobs.ClaimNextAsync("delivery", [PlaybackSignalPipeline.JobType]))!;
        var handler = new PlaybackSignalJobHandler(new Writer(), new ScopedPlaybackScrobbleDelivery(factory,
            targets, new EfPlaybackDeliveryCheckpointStore(factory), resolver), new Lyrics(), factory, resolver);
        var completion = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.Equal(2, http.Calls);
        var form = http.Bodies.Single(body => body.Contains("track.scrobble", StringComparison.Ordinal));
        Assert.Contains("artist=Artist", form);
        Assert.Contains("track=Track", form);
        Assert.DoesNotContain("duration=", form);
        Assert.DoesNotContain("album=", form);
        Assert.Equal(identifiers, form.Contains("mbid=11111111-1111-1111-1111-111111111111", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(http.Bodies.Single(body => body.StartsWith('{')));
        var metadata = json.RootElement.GetProperty("payload")[0].GetProperty("track_metadata");
        Assert.Equal("Artist", metadata.GetProperty("artist_name").GetString());
        Assert.Equal("Track", metadata.GetProperty("track_name").GetString());
        Assert.False(metadata.TryGetProperty("release_name", out _));
        Assert.False(metadata.GetProperty("additional_info").TryGetProperty("duration_ms", out _));
        var additional = metadata.GetProperty("additional_info");
        Assert.Equal(identifiers, additional.TryGetProperty("recording_mbid", out _));
        Assert.Equal(identifiers, additional.TryGetProperty("isrc", out var isrc));
        if (identifiers) Assert.Equal("USAAA2600001", isrc.GetString());
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.PlaybackDeliveryCheckpoints.CountAsync());
        Assert.All(await verify.PlaybackDeliveryCheckpoints.ToArrayAsync(), row => Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, row.State));
    }

    [Theory]
    [InlineData("delivery-not-attempted")]
    [InlineData("song-unidentified")]
    [InlineData("history")]
    public async Task ScrobbleRecovery_RequeuesExistingFailureOnceWhenTitleAndArtistAreSaved(string code)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "ext-deezer-song-known", TimeSpan.FromSeconds(100).Ticks) with
            { SubmittedTrack = new(null, null, "ext-deezer-song-known", "Track", "Artist", null, null) });
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = DurableJobState.Failed;
            var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
            row.State = ScopedPlaybackScrobbleOutcome.PermanentFailure;
            row.ProviderCode = code == "history" ? "song-unidentified" : code;
            if (code == "history")
            {
                var payload = JsonSerializer.Deserialize<PlaybackSignalPayload>(row.SignalPayloadJson!)!;
                row.SignalPayloadJson = JsonSerializer.Serialize(payload with { SubmittedTrack = null });
                (await db.Jobs.SingleAsync()).PayloadJson = row.SignalPayloadJson!;
                db.ListeningEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = user,
                    Protocol = "jellyfin",
                    BackendInstanceId = "backend",
                    OccurrenceKey = payload.OccurrenceKey!,
                    TrackReference = payload.ItemId,
                    Title = "Track",
                    Artist = "Artist",
                    State = ListeningEventState.Abandoned,
                    UpdatedAt = clock.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }
        var repair = new PlaybackScrobbleRepair(factory, jobs, new RejectResolver());
        await repair.ReconcileStartupAsync(default);
        await repair.ReconcileStartupAsync(default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(2, await db.Jobs.CountAsync());
            Assert.Equal(ScopedPlaybackScrobbleOutcome.Retrying, (await db.PlaybackDeliveryCheckpoints.SingleAsync()).State);
        }
        await AssertRecoveredDestinationAsync(target);
        await repair.ReconcileStartupAsync(default);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.Jobs.CountAsync());
    }

    [Theory]
    [InlineData("lastfm", "credentials")]
    [InlineData("lastfm", "account")]
    [InlineData("lastfm", "race")]
    [InlineData("listenbrainz", "credentials")]
    [InlineData("listenbrainz", "account")]
    [InlineData("listenbrainz", "race")]
    public async Task ScrobbleDelivery_MissingAccountOrCredentialsRemainsUnsentAndRetrying(string provider, string unavailable)
    {
        var account = await AddScrobbleAccountAsync(provider);
        var http = new ScrobbleHttp(HttpStatusCode.OK);
        IProviderAccountSecretAccessor secrets = unavailable == "credentials" ? new EmptyScrobbleSecrets() :
            unavailable == "race" ? new VanishingScrobbleSecrets() : new ScrobbleSecrets();
        var accessor = new ScopedRecommendationAccountAccessor(factory, secrets);
        IBoundPlaybackScrobbleTarget target = provider == "lastfm"
            ? new LastFmScopedPlaybackScrobbleTarget(new(http), accessor)
            : new ListenBrainzScopedPlaybackScrobbleTarget(new(http), accessor);
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await using (var db = await factory.CreateDbContextAsync())
        {
            if (unavailable == "account") (await db.ProviderAccounts.SingleAsync()).Enabled = false;
            (await db.Jobs.SingleAsync()).MaxAttempts = 1;
            await db.SaveChangesAsync();
        }
        var claim = (await jobs.ClaimNextAsync("delivery", [PlaybackSignalPipeline.JobType]))!;
        var handler = new PlaybackSignalJobHandler(new Writer(),
            new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks),
            new Lyrics(), factory, playbackTracks);
        Assert.Equal(DurableJobCompletionKind.Retry, (await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default)).Kind);
        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Retrying, row.State);
        Assert.Equal("destination-changed", row.ProviderCode);
        Assert.Equal(account.Id, row.DestinationAccountId);
        Assert.Equal(unavailable == "race" ? 1 : 0, row.SendCount);
        Assert.Null(row.AttemptStartedAt);
        Assert.Equal(0, http.Calls);
    }

    [Theory]
    [InlineData("lastfm", false)]
    [InlineData("listenbrainz", false)]
    [InlineData("lastfm", true)]
    [InlineData("listenbrainz", true)]
    public async Task ScrobbleDelivery_ReconnectedSavedAccountResumesOnlyForSameOwner(string provider, bool ownershipChanged)
    {
        var account = await AddScrobbleAccountAsync(provider);
        var secrets = new EmptyScrobbleSecrets();
        var http = new ScrobbleHttp(HttpStatusCode.OK);
        var accounts = new ScopedRecommendationAccountAccessor(factory, secrets);
        IBoundPlaybackScrobbleTarget target = provider == "lastfm"
            ? new LastFmScopedPlaybackScrobbleTarget(new(http), accounts)
            : new ListenBrainzScopedPlaybackScrobbleTarget(new(http), accounts);
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        var handler = new PlaybackSignalJobHandler(new Writer(),
            new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks),
            new Lyrics(), factory, playbackTracks);
        var claim = (await jobs.ClaimNextAsync("delivery", [PlaybackSignalPipeline.JobType]))!;
        await jobs.CompleteAsync(claim, await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var saved = await db.ProviderAccounts.SingleAsync();
            saved.Revision++;
            if (ownershipChanged)
            {
                var other = Guid.CreateVersion7();
                db.Users.Add(User(other, "jellyfin", "backend", "new-owner", "Other", clock.UtcNow));
                saved.OwnerUserId = other;
            }
            var checkpoint = await db.PlaybackDeliveryCheckpoints.SingleAsync();
            checkpoint.RetryAfter = null;
            checkpoint.ProviderCode = "recovery-queued";
            clock.UtcNow = (await db.Jobs.SingleAsync()).AvailableAt;
            await db.SaveChangesAsync();
        }
        secrets.Configured = true;
        claim = (await jobs.ClaimNextAsync("delivery", [PlaybackSignalPipeline.JobType]))!;
        var completion = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
        Assert.Equal(ownershipChanged ? DurableJobCompletionKind.Retry : DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.Equal(ownershipChanged ? 0 : 1, http.Calls);
        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.Equal(account.Id, row.DestinationAccountId);
        Assert.Equal(ownershipChanged ? 1 : 2, row.DestinationAccountRevision);
    }

    [Theory]
    [InlineData("lastfm")]
    [InlineData("listenbrainz")]
    public async Task ScrobbleDelivery_ReauthenticationUsesGrowingRetryDelay(string provider)
    {
        await AddScrobbleAccountAsync(provider);
        var http = new ScrobbleHttp(HttpStatusCode.OK);
        var accounts = new ScopedRecommendationAccountAccessor(factory, new EmptyScrobbleSecrets());
        IBoundPlaybackScrobbleTarget target = provider == "lastfm"
            ? new LastFmScopedPlaybackScrobbleTarget(new(http), accounts)
            : new ListenBrainzScopedPlaybackScrobbleTarget(new(http), accounts);
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        var handler = new PlaybackSignalJobHandler(new Writer(),
            new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks),
            new Lyrics(), factory, playbackTracks);
        foreach (var seconds in new[] { 30, 60, 120 })
        {
            var claim = (await jobs.ClaimNextAsync("delivery", [PlaybackSignalPipeline.JobType]))!;
            var result = await handler.ExecuteAsync(new(claim, EmptyServices.Instance), default);
            Assert.Equal(DurableJobCompletionKind.Retry, result.Kind);
            Assert.Equal(TimeSpan.FromSeconds(seconds), result.RetryDelay);
            await jobs.CompleteAsync(claim, result);
            await using var db = await factory.CreateDbContextAsync();
            var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
            Assert.True(row.RequiresReauthentication);
            Assert.Equal(TimeSpan.FromSeconds(seconds), row.RetryAfter - row.UpdatedAt);
            Assert.Equal(clock.UtcNow.AddSeconds(seconds), (await db.Jobs.SingleAsync()).AvailableAt);
            clock.UtcNow = (await db.Jobs.SingleAsync()).AvailableAt;
        }
        Assert.Equal(0, http.Calls);
    }

    [Theory]
    [InlineData(null, 29, false)]
    [InlineData(null, 30, true)]
    [InlineData(0L, 30, true)]
    [InlineData(20_000L, 30, false)]
    [InlineData(120_000L, 30, false)]
    [InlineData(120_000L, 60, true)]
    public void ScrobbleDelivery_OptionalDurationKeepsListenedTimeThreshold(long? duration, int played, bool eligible) =>
        Assert.Equal(eligible, ScopedPlaybackScrobbleDelivery.EligibleForCompletedScrobble(duration, TimeSpan.FromSeconds(played).Ticks));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrobbleRecovery_UnavailableListenerWaitsUntilEnabled(bool existingFailure)
    {
        var target = new RecoveryTarget(await AddScrobbleAccountAsync());
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks) with
            { SubmittedTrack = new(null, null, "track-1", "Track", "Artist", null, 120_000) });
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = DurableJobState.Failed;
            (await db.Users.SingleAsync()).Enabled = false;
            if (existingFailure)
            {
                var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
                row.State = ScopedPlaybackScrobbleOutcome.PermanentFailure;
                row.ProviderCode = "delivery-not-attempted";
            }
            await db.SaveChangesAsync();
        }
        var repair = new PlaybackScrobbleRepair(factory, jobs, playbackTracks, clock);
        await repair.RecoverPendingAsync(default);
        await repair.RecoverPendingAsync(default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
            Assert.Equal(clock.UtcNow.AddSeconds(30), row.RetryAfter);
            clock.UtcNow = row.RetryAfter!.Value;
        }
        await repair.RecoverPendingAsync(default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Single(await db.Jobs.ToArrayAsync());
            var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
            Assert.Equal(ScopedPlaybackScrobbleOutcome.Retrying, row.State);
            Assert.Equal(clock.UtcNow.AddSeconds(60), row.RetryAfter);
            (await db.Users.SingleAsync()).Enabled = true;
            await db.SaveChangesAsync();
        }
        clock.UtcNow += TimeSpan.FromMinutes(5);
        await repair.RecoverPendingAsync(default);
        await AssertRecoveredDestinationAsync(target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrobbleUnidentified_IsDismissibleEvenWithSavedSubmission(bool savedSubmission)
    {
        var payload = AcceptedScrobblePayload();
        var row = new PlaybackDeliveryCheckpointEntity
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user,
            TargetId = "lastfm",
            SignalKey = ScopedPlaybackScrobbleDelivery.CheckpointKey(payload),
            State = ScopedPlaybackScrobbleOutcome.PermanentFailure,
            ProviderCode = "song-unidentified",
            SignalPayloadJson = JsonSerializer.Serialize(payload),
            SubmissionJson = savedSubmission ? JsonSerializer.Serialize(new PlaybackScrobbleSubmission(payload.Transition,
                new("Track", "Artist", null, 120_000), payload.PositionTicks, payload.ObservedAt)) : null,
            UpdatedAt = clock.UtcNow
        };
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Add(row);
            await db.SaveChangesAsync();
        }
        using var json = await ReadScrobbleAttentionAsync();
        Assert.True(Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("canDismiss").GetBoolean());
        Assert.True(await new PlaybackScrobbleRepair(factory, jobs).ResolveAsync(user, row.Id, row.Revision, "dismiss", null, default));
        await AssertNoScrobbleAttentionAsync();
    }

    [Fact]
    public async Task ScrobbleAttention_CountsOnlyCompletedDeliveriesWaitingOrNeedingAction()
    {
        var account = await AddScrobbleAccountAsync();
        var expected = new List<Guid>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            Add(ScopedPlaybackScrobbleOutcome.Retrying, "metadata-unavailable", true);
            Add(ScopedPlaybackScrobbleOutcome.Retrying, "delivery-not-attempted", true);
            Add(ScopedPlaybackScrobbleOutcome.Retrying, "destination-changed", true);
            Add(ScopedPlaybackScrobbleOutcome.PermanentFailure, "song-unidentified", true);
            Add(ScopedPlaybackScrobbleOutcome.PermanentFailure, "payload-unavailable", true);
            Add(ScopedPlaybackScrobbleOutcome.Delivered, null, false);
            Add(ScopedPlaybackScrobbleOutcome.Ignored, "dismissed", false);
            Add(ScopedPlaybackScrobbleOutcome.Retrying, "pending", false);
            Add(ScopedPlaybackScrobbleOutcome.PermanentFailure, "ambiguous", false, PlaybackScrobbleDeliveryKind.NowPlaying);
            await db.SaveChangesAsync();

            void Add(ScopedPlaybackScrobbleOutcome state, string? code, bool needsAttention,
                PlaybackScrobbleDeliveryKind kind = PlaybackScrobbleDeliveryKind.Completed)
            {
                var id = Guid.CreateVersion7();
                db.Add(new PlaybackDeliveryCheckpointEntity
                {
                    Id = id,
                    OwnerUserId = user,
                    TargetId = "lastfm",
                    DestinationAccountId = account.Id,
                    SignalKey = id.ToString("N"),
                    State = state,
                    ProviderCode = code,
                    Kind = kind,
                    UpdatedAt = clock.UtcNow
                });
                if (needsAttention) expected.Add(id);
            }
        }
        using var json = await ReadScrobbleAttentionAsync();
        var rows = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(expected.Order(), rows.Select(row => row.GetProperty("Id").GetGuid()).Order());
        Assert.Equal(expected.Count, rows.Count(row => row.GetProperty("DestinationAccountId").GetGuid() == account.Id));
    }

    private async Task<JsonDocument> ReadScrobbleAttentionAsync()
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
        return JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
    }

    private sealed class EmptyScrobbleSecrets : IProviderAccountSecretAccessor
    {
        public bool Configured;
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken token) =>
            Configured ? new ScrobbleSecrets().UseAsync(account, operation, token) : operation("{}"u8.ToArray());
    }

    private sealed class VanishingScrobbleSecrets : IProviderAccountSecretAccessor
    {
        private int calls;
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken token) =>
            ++calls == 1 ? new ScrobbleSecrets().UseAsync(account, operation, token) : throw new UnauthorizedAccessException();
    }
}
