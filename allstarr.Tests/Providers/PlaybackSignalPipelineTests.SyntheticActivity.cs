using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    private const string SyntheticProviderItem = "ext-deezer-song-check";

    [Fact]
    public async Task SyntheticActivity_UsesPersistedIdentityAndPreservesNativePlayback()
    {
        var stale = Signal(PlaybackTransition.Start, SyntheticProviderItem, 0);
        await SetSyntheticActivityAsync();
        var pipeline = new PlaybackSignalPipeline(jobs, factory: factory);

        Assert.False(await pipeline.RecordAsync(stale));
        Assert.True(await pipeline.RecordAsync(stale with { ItemId = "track-1" }));

        await using var db = factory.CreateDbContext();
        var saved = Assert.Single(await db.Jobs.ToListAsync());
        Assert.Equal("track-1", JsonSerializer.Deserialize<PlaybackSignalPayload>(saved.PayloadJson)!.ItemId);
        Assert.Empty(await db.PlaybackDeliveryCheckpoints.ToListAsync());
    }

    [Theory]
    [InlineData("jellyfin", "backend", SyntheticProviderItem, true)]
    [InlineData("subsonic", "backend", SyntheticProviderItem, false)]
    [InlineData("jellyfin", "other", SyntheticProviderItem, false)]
    [InlineData("jellyfin", "backend", "track-1", false)]
    public async Task SyntheticActivity_PersistedPolicyRequiresTheExactBackendScope(string protocol, string backend,
        string itemId, bool excluded)
    {
        await SetSyntheticActivityAsync();
        await using var db = factory.CreateDbContext();
        Assert.Equal(excluded, await SyntheticActivityPolicy.IsExcludedAsync(db,
            new(user, protocol, backend), itemId, default));
        Assert.False(await SyntheticActivityPolicy.IsExcludedAsync(db,
            new(Guid.CreateVersion7(), protocol, backend), itemId, default));
        (await db.Users.SingleAsync(item => item.Id == user)).Enabled = false;
        await db.SaveChangesAsync();
        Assert.Equal(excluded, await SyntheticActivityPolicy.IsExcludedAsync(db,
            new(user, protocol, backend), itemId, default));
    }

    [Theory]
    [InlineData(true, PlaybackTransition.Start)]
    [InlineData(true, PlaybackTransition.Stop)]
    [InlineData(true, PlaybackTransition.Submission)]
    [InlineData(false, PlaybackTransition.Start)]
    [InlineData(false, PlaybackTransition.Stop)]
    [InlineData(false, PlaybackTransition.Submission)]
    public async Task SyntheticActivity_QueuedSignalsRecheckDesignationBeforeEveryEffect(bool synthetic, PlaybackTransition transition)
    {
        await new PlaybackSignalPipeline(jobs).RecordAsync(Signal(transition, SyntheticProviderItem,
            TimeSpan.FromSeconds(100).Ticks) with
        {
            SubmittedTrack = new(null, null, SyntheticProviderItem, "Track", "Artist", "Album", 120000)
        });
        if (synthetic) await SetSyntheticActivityAsync();
        var writer = new Writer();
        var scrobbles = new Scrobbles();
        var lyrics = new Lyrics();
        var enrichment = new MusicBrainzListeningEnrichmentQueue(jobs,
            Options.Create(new MusicBrainzSettings { Enabled = true }));
        var handler = new PlaybackSignalJobHandler(writer, scrobbles, lyrics, factory, new RejectResolver(), enrichment);
        var claim = await jobs.ClaimNextAsync("exclusion", [PlaybackSignalPipeline.JobType]);
        Assert.NotNull(claim);

        var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        Assert.Equal(synthetic ? 0 : 1, writer.Calls);
        Assert.Equal(synthetic ? 0 : 1, scrobbles.Calls);
        Assert.Equal(!synthetic && transition == PlaybackTransition.Start ? 1 : 0, lyrics.Calls);
        await using var db = factory.CreateDbContext();
        Assert.Equal(synthetic ? 0 : 1, await db.ListeningEvents.CountAsync());
        Assert.Equal(synthetic ? 0 : 1, await db.Jobs.CountAsync(item => item.Type == MusicBrainzListeningEnrichmentQueue.JobType));
        if (synthetic)
        {
            Assert.Empty(await db.ListeningSignals.ToListAsync());
            Assert.Empty(await db.PlaybackDeliveryCheckpoints.ToListAsync());
        }
    }

    [Fact]
    public async Task SyntheticActivity_AcceptanceCreatesNoJobOrDestinationReservation()
    {
        var target = new SyntheticDeliveryProbe(await AddScrobbleAccountAsync());
        await SetSyntheticActivityAsync();
        var payload = Payload() with { ItemId = SyntheticProviderItem };

        var result = await new PlaybackScrobbleAcceptance(factory, jobs, [target]).EnqueueAsync(
            new DurableJobEnqueueRequest<PlaybackSignalPayload>(PlaybackSignalPipeline.JobType, "excluded", payload, user), default);

        Assert.False(result.Created);
        Assert.Equal(0, target.Lookups);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Jobs.ToListAsync());
        Assert.Empty(await db.PlaybackDeliveryCheckpoints.ToListAsync());
    }

    [Fact]
    public async Task SyntheticActivity_UnboundTargetsAndMetadataAreNotConsulted()
    {
        await SetSyntheticActivityAsync();
        var target = new Target("test", true);
        await new ScopedPlaybackScrobbleDelivery(factory, [target], new Checkpoints(), new RejectResolver())
            .DeliverAsync(Payload() with { ItemId = SyntheticProviderItem }, default);

        Assert.Equal(0, target.Calls);
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.PlaybackDeliveryCheckpoints.ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData(PlaybackTransition.Start, true)]
    [InlineData(PlaybackTransition.Stop, true)]
    [InlineData(PlaybackTransition.Stop, false)]
    public async Task SyntheticActivity_FrozenDestinationsDoNotVerifyOrSend(PlaybackTransition transition, bool enabled)
    {
        var target = new SyntheticDeliveryProbe(await AddScrobbleAccountAsync());
        var payload = Payload() with { ItemId = SyntheticProviderItem, Transition = transition, DestinationTargets = ["lastfm"] };
        var row = BoundPlaybackScrobbleDelivery.New(payload, ScopedPlaybackScrobbleDelivery.CheckpointKey(payload),
            transition == PlaybackTransition.Start ? PlaybackScrobbleDeliveryKind.NowPlaying : PlaybackScrobbleDeliveryKind.Completed,
            "lastfm", target.Destination);
        row.State = ScopedPlaybackScrobbleOutcome.PermanentFailure;
        row.ProviderCode = "ambiguous";
        row.AttemptStartedAt = clock.UtcNow.AddMinutes(-5);
        await using (var db = factory.CreateDbContext())
        {
            db.PlaybackDeliveryCheckpoints.Add(row);
            await db.SaveChangesAsync();
        }
        await SetSyntheticActivityAsync(enabled);

        var result = await new BoundPlaybackScrobbleDelivery(factory, new EfPlaybackDeliveryCheckpointStore(factory))
            .DeliverAsync(target, payload, new("Track", "Artist", null, 120000), clock.UtcNow, default);

        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, result!.Outcome);
        Assert.Equal(0, target.Lookups + target.Verifications + target.Preparations + target.Sends);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(0, (await verify.PlaybackDeliveryCheckpoints.SingleAsync()).SendCount);
    }

    [Fact]
    public async Task SyntheticActivity_RecoveryAndExplicitRetryDoNotRequeueSavedProviderListen()
    {
        var target = new SyntheticDeliveryProbe(await AddScrobbleAccountAsync());
        var payload = Payload() with { ItemId = SyntheticProviderItem, DestinationTargets = ["lastfm"] };
        var row = BoundPlaybackScrobbleDelivery.New(payload, ScopedPlaybackScrobbleDelivery.CheckpointKey(payload),
            PlaybackScrobbleDeliveryKind.Completed, "lastfm", target.Destination);
        row.SubmissionJson = JsonSerializer.Serialize(new PlaybackScrobbleSubmission(payload.Transition,
            new("Track", "Artist", null, 120000), payload.PositionTicks, payload.ObservedAt));
        await using (var db = factory.CreateDbContext())
        {
            db.PlaybackDeliveryCheckpoints.Add(row);
            await db.SaveChangesAsync();
        }
        await SetSyntheticActivityAsync();
        var repair = new PlaybackScrobbleRepair(factory, jobs, new RejectResolver());
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair.ResolveAsync(user, row.Id, row.Revision,
            "retry", target.Destination.Revision, default));
        await repair.ReconcileStartupAsync(default);

        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.Jobs.ToListAsync());
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, (await verify.PlaybackDeliveryCheckpoints.SingleAsync()).State);
        Assert.Empty(await verify.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task SyntheticActivity_PlayedMutationIsANoopButNormalListenerStatePersists()
    {
        var context = Signal(PlaybackTransition.Start, SyntheticProviderItem, 0).ExecutionContext;
        var store = new ProviderUserDataStore(factory);
        await SetSyntheticActivityAsync();
        Assert.True(await store.SetPlayedAsync(context, SyntheticProviderItem, true, clock.UtcNow));
        await using (var db = factory.CreateDbContext()) Assert.Empty(await db.ProviderUserData.ToListAsync());
        var other = Guid.CreateVersion7();
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(User(other, "jellyfin", "backend", "other", "Other", clock.UtcNow));
            await db.SaveChangesAsync();
        }
        var normal = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "other",
            new(other, "jellyfin", "backend", "other", "Other", false), "normal", clock.UtcNow.AddMinutes(1), default);
        Assert.True(await store.SetPlayedAsync(normal, SyntheticProviderItem, true, clock.UtcNow));
        Assert.Equal(1, (await store.ReadAsync(normal, [SyntheticProviderItem]))[SyntheticProviderItem].PlayCount);
        Assert.Equal(0, (await store.ReadAsync(context, [SyntheticProviderItem]))[SyntheticProviderItem].PlayCount);
    }

    [Fact]
    public async Task SyntheticActivity_IdentityRefreshRetainsThePersistedDesignation()
    {
        await SetSyntheticActivityAsync();
        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        var resolver = new BackendIdentityResolver(factory, state, new IdentityOptions { BackendInstanceId = "backend" }, clock);

        var refreshed = await resolver.ResolveAsync(new("jellyfin", "principal", "Updated", false));
        var normal = await resolver.ResolveAsync(new("jellyfin", "other", "Other", false));

        Assert.True(refreshed!.IsSynthetic);
        Assert.False(normal!.IsSynthetic);
        await using var db = factory.CreateDbContext();
        Assert.True((await db.Users.SingleAsync(item => item.Id == user)).IsSynthetic);
    }

    private async Task SetSyntheticActivityAsync(bool enabled = true)
    {
        await using var db = factory.CreateDbContext();
        var listener = await db.Users.SingleAsync(item => item.Id == user);
        listener.IsSynthetic = true;
        listener.Enabled = enabled;
        await db.SaveChangesAsync();
    }

    private sealed class SyntheticDeliveryProbe(ProviderAccountRecord account) : IVerifiablePlaybackScrobbleTarget
    {
        public int Lookups, Verifications, Preparations, Sends;
        public string ProviderId => "lastfm";
        public ProviderAccountContext Destination => new(account.Id, account.ProviderId, account.Scope, account.Revision,
            true, account.OwnerUserId, "fixture", null);
        public Task<bool> IsConfiguredAsync(IntelligenceScope scope, CancellationToken token) => Task.FromResult(true);
        public Task<ProviderAccountContext?> FindDestinationAsync(IntelligenceScope scope, CancellationToken token)
        { Lookups++; return Task.FromResult<ProviderAccountContext?>(Destination); }
        public Task<bool> CanReplayAsync(IntelligenceScope scope, ProviderAccountContext destination, CancellationToken token)
        { Preparations++; return Task.FromResult(false); }
        public Task<PlaybackScrobbleVerification> VerifyAsync(IntelligenceScope scope, ProviderAccountContext destination,
            PlaybackScrobbleSubmission submission, CancellationToken token)
        { Verifications++; return Task.FromResult(PlaybackScrobbleVerification.Absent); }
        public Task<ScopedPlaybackScrobbleResult> DeliverAsync(IntelligenceScope scope, PlaybackTransition transition,
            ScopedPlaybackTrack track, long? ticks, DateTimeOffset observedAt, string key, CancellationToken token)
        { Sends++; return Task.FromResult(ScopedPlaybackScrobbleResult.Delivered()); }
        public Task<ScopedPlaybackScrobbleResult> DeliverBoundAsync(IntelligenceScope scope, ProviderAccountContext destination,
            PlaybackTransition transition, ScopedPlaybackTrack track, long? ticks, DateTimeOffset observedAt, string key, CancellationToken token) =>
            DeliverAsync(scope, transition, track, ticks, observedAt, key, token);
    }
}
