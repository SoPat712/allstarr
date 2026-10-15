using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Scrobbling;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Fact]
    public async Task ScrobbleRetry_DoesNotSelectReplacementAccount()
    {
        var account = await AddScrobbleAccountAsync();
        var secrets = new ScrobbleSecrets();
        var http = new ScrobbleHttp(HttpStatusCode.TooManyRequests);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, secrets));
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks);
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => delivery.DeliverAsync(AcceptedScrobblePayload(), default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.ProviderAccounts.SingleAsync(x => x.Id == account.Id)).Enabled = false;
            await db.SaveChangesAsync();
        }
        await AddScrobbleAccountAsync();
        http.Status = HttpStatusCode.OK;
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => delivery.DeliverAsync(AcceptedScrobblePayload(), default));
        Assert.Equal(1, http.Calls);
        Assert.Equal(account.Id, Assert.Single(secrets.Used.Select(x => x.AccountId).Distinct()));
    }

    [Fact]
    public async Task ScrobbleAcceptedBeforeSaveFailure_IsNotSentAgainWithoutReview()
    {
        await AddScrobbleAccountAsync();
        var http = new ScrobbleHttp(HttpStatusCode.OK);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets()));
        var checkpoints = new FailAcceptedCheckpoint(new EfPlaybackDeliveryCheckpointStore(factory));
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], checkpoints, playbackTracks);
        await Assert.ThrowsAnyAsync<Exception>(() => delivery.DeliverAsync(AcceptedScrobblePayload(), default));
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() =>
            new ScopedPlaybackScrobbleDelivery(factory, [target], checkpoints, playbackTracks).DeliverAsync(AcceptedScrobblePayload(), default));
        Assert.Equal(1, http.Calls);
        await using var db = await factory.CreateDbContextAsync();
        var record = Assert.Single(await db.PlaybackDeliveryCheckpoints.ToListAsync());
        Assert.Equal("ambiguous", record.ProviderCode);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.PermanentFailure, record.State);
    }

    [Fact]
    public async Task ScrobbleAcceptance_FreezesRevisionBeforeTheWorkerRuns()
    {
        var account = await AddScrobbleAccountAsync();
        var http = new ScrobbleHttp(HttpStatusCode.OK);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets()));
        var pipeline = new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]));
        var signal = Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks);
        Assert.True(await pipeline.RecordAsync(signal));
        Assert.False(await pipeline.RecordAsync(signal));
        PlaybackSignalPayload payload;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = Assert.Single(await db.PlaybackDeliveryCheckpoints.ToListAsync());
            Assert.Equal(account.Id, row.DestinationAccountId);
            Assert.Equal(1, row.DestinationAccountRevision);
            Assert.Equal(0, row.SendCount);
            (await db.ProviderAccounts.SingleAsync()).Revision++;
            await db.SaveChangesAsync();
            payload = JsonSerializer.Deserialize<PlaybackSignalPayload>((await db.Jobs.SingleAsync()).PayloadJson)!;
        }
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks);
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => delivery.DeliverAsync(payload, default));
        Assert.Equal(0, http.Calls);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.True((await verify.PlaybackDeliveryCheckpoints.SingleAsync()).RequiresReauthentication);
    }

    [Fact]
    public async Task ScrobbleAcceptance_DoesNotAddANewlyConfiguredDestinationDuringExecution()
    {
        var http = new ScrobbleHttp(HttpStatusCode.OK);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets()));
        var pipeline = new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]));
        await pipeline.RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await AddScrobbleAccountAsync();
        await using var db = await factory.CreateDbContextAsync();
        var payload = JsonSerializer.Deserialize<PlaybackSignalPayload>((await db.Jobs.SingleAsync()).PayloadJson)!;
        await new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks).DeliverAsync(payload, default);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task ScrobbleRetry_PreservesMetadataTimestampAndBackoff()
    {
        await AddScrobbleAccountAsync();
        var http = new ScrobbleHttp(HttpStatusCode.TooManyRequests);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets()));
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks);
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => delivery.DeliverAsync(AcceptedScrobblePayload(), default));
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => delivery.DeliverAsync(AcceptedScrobblePayload(), default));
        Assert.Equal(1, http.Calls);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
            Assert.True(row.RetryAfter > row.UpdatedAt);
            row.RetryAfter = DateTimeOffset.UtcNow.AddMinutes(-1);
            (await db.LibraryTracks.SingleAsync()).Title = "Changed catalog title";
            await db.SaveChangesAsync();
        }
        http.Status = HttpStatusCode.OK;
        await delivery.DeliverAsync(AcceptedScrobblePayload() with { ObservedAt = clock.UtcNow.AddHours(1) }, default);
        await delivery.DeliverAsync(AcceptedScrobblePayload(), default);
        Assert.Equal(2, http.Calls);
        Assert.Equal(http.Bodies[0], http.Bodies[1]);
    }

    [Fact]
    public async Task ScrobbleReauthentication_RequiresExplicitSameAccountRevisionRepair()
    {
        var account = await AddScrobbleAccountAsync();
        var http = new ScrobbleHttp(HttpStatusCode.Unauthorized);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets()));
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks);
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => delivery.DeliverAsync(AcceptedScrobblePayload(), default));
        PlaybackDeliveryCheckpointEntity row;
        await using (var db = await factory.CreateDbContextAsync())
        {
            row = await db.PlaybackDeliveryCheckpoints.AsNoTracking().SingleAsync();
            Assert.True(row.RequiresReauthentication);
            (await db.ProviderAccounts.SingleAsync()).Revision = 2;
            await db.SaveChangesAsync();
        }
        var repair = new PlaybackScrobbleRepair(factory, jobs);
        var replacementAccount = await AddScrobbleAccountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair.ResolveAsync(user, row.Id, row.Revision, "retry", 1, default, replacementAccount.Id));
        Assert.False(await repair.ResolveAsync(Guid.NewGuid(), row.Id, row.Revision, "retry", 2, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair.ResolveAsync(user, row.Id, row.Revision, "retry", 1, default));
        Assert.True(await repair.ResolveAsync(user, row.Id, row.Revision, "retry", 2, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair.ResolveAsync(user, row.Id, row.Revision, "retry", 2, default));
        http.Status = HttpStatusCode.OK;
        await delivery.DeliverAsync(AcceptedScrobblePayload(), default);
        await using var verify = await factory.CreateDbContextAsync();
        var saved = await verify.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.Equal(account.Id, saved.DestinationAccountId);
        Assert.Equal(2, saved.DestinationAccountRevision);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, saved.State);
        Assert.Single(await verify.Jobs.ToListAsync());
        Assert.Single(await verify.AuditEvents.Where(x => x.Action == "delivery-repaired").ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrobbleDedupe_SurvivesListeningHistoryPurge(bool sweep)
    {
        await AddScrobbleAccountAsync();
        var payload = AcceptedScrobblePayload() with { OccurrenceKey = new string('c', 64) };
        var http = new ScrobbleHttp(HttpStatusCode.OK);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets()));
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks);
        await delivery.DeliverAsync(payload, default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ListeningEvents.Add(new ListeningEventRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = user,
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                OccurrenceKey = payload.OccurrenceKey!,
                TrackReference = "track-1",
                SourceKind = "protocol",
                UpdatedAt = clock.UtcNow.AddDays(-90)
            });
            await db.SaveChangesAsync();
        }
        if (sweep) await new ListeningHistoryRetentionSweeper(factory, clock).SweepAsync();
        else await new IntelligencePolicyService(factory, clock).DisableAndPurgeAsync(payload.Scope);
        await delivery.DeliverAsync(payload, default);
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task ScrobbleDeliveryStatus_IsOwnerScopedAndDoesNotExposeStoredBindings()
    {
        await AddScrobbleAccountAsync();
        var http = new ScrobbleHttp(HttpStatusCode.Unauthorized);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets()));
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() =>
            new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks).DeliverAsync(AcceptedScrobblePayload(), default));
        var controller = new allstarr.Controllers.ScrobbleDeliveriesController(factory, new(factory, jobs))
        { ControllerContext = new() { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
        Assert.IsType<Microsoft.AspNetCore.Mvc.ForbidResult>(await controller.List());
        void SignIn(Guid owner) => controller.HttpContext.Items[allstarr.Services.Admin.AdminAuthSessionService.HttpContextSessionItemKey] =
            new allstarr.Services.Admin.AdminAuthSession
            {
                SessionId = "fixture",
                UserId = owner.ToString(),
                UserName = "Listener",
                IsAdministrator = true,
                BackendType = "Jellyfin",
                AllstarrUserId = owner,
                JellyfinAccessToken = "fixture",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                LastSeenUtc = DateTime.UtcNow
            };
        SignIn(Guid.NewGuid());
        var foreign = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.List());
        using (var result = JsonDocument.Parse(JsonSerializer.Serialize(foreign.Value)))
            Assert.Empty(result.RootElement.GetProperty("items").EnumerateArray());
        SignIn(user);
        var own = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.List());
        var json = JsonSerializer.Serialize(own.Value);
        using (var result = JsonDocument.Parse(json)) Assert.Single(result.RootElement.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain("DestinationJson", json);
        Assert.DoesNotContain("SignalPayloadJson", json);
        Assert.DoesNotContain("fixture-secret", json);
        await using var db = await factory.CreateDbContextAsync();
        var id = (await db.PlaybackDeliveryCheckpoints.SingleAsync()).Id;
        var otherTarget = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.List(attentionOnly: true, targetId: "koito"));
        using (var none = JsonDocument.Parse(JsonSerializer.Serialize(otherTarget.Value)))
            Assert.Empty(none.RootElement.GetProperty("items").EnumerateArray());
        var page = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.List(id));
        using var empty = JsonDocument.Parse(JsonSerializer.Serialize(page.Value));
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ScrobbleRetryExhaustion_RemainsRepairable()
    {
        await AddScrobbleAccountAsync();
        var target = new LastFmScopedPlaybackScrobbleTarget(new(new ScrobbleHttp(HttpStatusCode.TooManyRequests)),
            new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets()));
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).MaxAttempts = 1;
            await db.SaveChangesAsync();
        }
        var claim = await jobs.ClaimNextAsync("scrobble-exhaust", [PlaybackSignalPipeline.JobType]);
        var handler = new PlaybackSignalJobHandler(new Writer(), new ScopedPlaybackScrobbleDelivery(factory,
            [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks), new Lyrics(), factory, playbackTracks);
        var completion = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
        await jobs.CompleteAsync(claim!, completion);
        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.PlaybackDeliveryCheckpoints.AsNoTracking().SingleAsync();
        Assert.Equal("retry-exhausted", row.ProviderCode);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.PermanentFailure, row.State);
        Assert.True(await new PlaybackScrobbleRepair(factory, jobs).ResolveAsync(user, row.Id, row.Revision, "retry", 1, default));
    }

    [Fact]
    public async Task ScrobbleWithoutHistory_UsesTheOriginalPlaybackStartTime()
    {
        await AddScrobbleAccountAsync();
        var http = new ScrobbleHttp(HttpStatusCode.OK);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets()));
        var payload = AcceptedScrobblePayload();
        await new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks)
            .DeliverAsync(payload, default);
        var timestamp = (payload.ObservedAt - TimeSpan.FromTicks(payload.PositionTicks!.Value)).ToUnixTimeSeconds().ToString();
        Assert.Contains("timestamp=" + timestamp, Assert.Single(http.Bodies));
    }

    [Fact]
    public async Task ScrobbleDelivery_UsesEncryptedExactAccountSecrets()
    {
        var account = await AddScrobbleAccountAsync();
        var keyPath = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "scrobble-keyring.json");
        await File.WriteAllTextAsync(keyPath, JsonSerializer.Serialize(new
        {
            activeKeyId = "fixture-key",
            keys = new Dictionary<string, string> { ["fixture-key"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) }
        }));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var options = new allstarr.Core.Secrets.SecretStoreOptions { KeyRingPath = keyPath };
        var store = new allstarr.Core.Secrets.EncryptedSecretStore(factory,
            new allstarr.Core.Secrets.FileSecretKeyRingProvider(options), options, clock);
        var reference = await store.StoreAsync(user, $"provider-account:lastfm:{account.Id:N}",
            System.Text.Encoding.UTF8.GetBytes("{\"apiKey\":\"fixture-key\",\"sharedSecret\":\"encrypted-only-secret\",\"sessionKey\":\"encrypted-only-session\"}"), cancellationToken: default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.ProviderAccounts.SingleAsync()).SecretReferenceId = reference.Id;
            await db.SaveChangesAsync();
        }
        var http = new ScrobbleHttp(HttpStatusCode.OK);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory,
            new EncryptedProviderAccountSecretAccessor(store)));
        await new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks)
            .DeliverAsync(AcceptedScrobblePayload(), default);
        Assert.Contains("sk=encrypted-only-session", Assert.Single(http.Bodies));
        await using var verify = await factory.CreateDbContextAsync();
        var receipt = await verify.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.DoesNotContain("encrypted-only", JsonSerializer.Serialize(receipt));
        Assert.DoesNotContain("encrypted-only", System.Text.Encoding.UTF8.GetString((await verify.SecretVersions.SingleAsync()).Ciphertext));
    }

    [Fact]
    public async Task ScrobbleRetry_DoesNotLoseFrozenListenWhenMetadataDisappears()
    {
        await AddScrobbleAccountAsync();
        var http = new ScrobbleHttp(HttpStatusCode.TooManyRequests);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets()));
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks);
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => delivery.DeliverAsync(AcceptedScrobblePayload(), default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.LibraryTracks.RemoveRange(await db.LibraryTracks.ToArrayAsync());
            (await db.PlaybackDeliveryCheckpoints.SingleAsync()).RetryAfter = clock.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        http.Status = HttpStatusCode.OK;
        await delivery.DeliverAsync(AcceptedScrobblePayload(), default);
        Assert.Equal(2, http.Calls);
        Assert.Equal(http.Bodies[0], http.Bodies[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyScrobble_RequiresAnExplicitDestinationBeforeAnySend(bool configured)
    {
        var account = configured ? await AddScrobbleAccountAsync() : null;
        if (!configured)
        {
            await using var legacy = await factory.CreateDbContextAsync();
            legacy.PlaybackDeliveryCheckpoints.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = user,
                SignalKey = ScopedPlaybackScrobbleDelivery.CheckpointKey(Payload()),
                OccurrenceKey = Payload().SignalKey,
                TargetId = "lastfm",
                Kind = PlaybackScrobbleDeliveryKind.Completed,
                State = ScopedPlaybackScrobbleOutcome.Retrying,
                UpdatedAt = clock.UtcNow
            });
            await legacy.SaveChangesAsync();
        }
        var http = new ScrobbleHttp(HttpStatusCode.OK);
        var target = new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets()));
        var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks);
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => delivery.DeliverAsync(Payload(), default));
        Assert.Equal(0, http.Calls);
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.PlaybackDeliveryCheckpoints.AsNoTracking().SingleAsync();
        Assert.Null(row.DestinationAccountId);
        Assert.Equal("destination-unbound", row.ProviderCode);
        account ??= await AddScrobbleAccountAsync();
        var foreignUser = Guid.CreateVersion7();
        var foreignAccount = Guid.CreateVersion7();
        await using (var other = await factory.CreateDbContextAsync())
        {
            other.Users.Add(User(foreignUser, "jellyfin", "backend", "other-listener", "Other listener", clock.UtcNow));
            other.ProviderAccounts.Add(new()
            {
                Id = foreignAccount,
                OwnerUserId = foreignUser,
                ProviderId = "lastfm",
                DisplayName = "Other account",
                Revision = 1,
                Enabled = true,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            });
            await other.SaveChangesAsync();
        }
        var repair = new PlaybackScrobbleRepair(factory, jobs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair.ResolveAsync(user, row.Id, row.Revision, "retry", 1, default, foreignAccount));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair.ResolveAsync(user, row.Id, row.Revision, "retry", 1, default));
        Assert.True(await repair.ResolveAsync(user, row.Id, row.Revision, "retry", 1, default, account.Id));
        await delivery.DeliverAsync(AcceptedScrobblePayload(), default);
        Assert.Equal(1, http.Calls);
        Assert.Equal(account.Id, (await db.PlaybackDeliveryCheckpoints.AsNoTracking().SingleAsync()).DestinationAccountId);
    }

    private PlaybackSignalPayload AcceptedScrobblePayload() => Payload() with { DestinationTargets = ["lastfm", "listenbrainz", "multi-scrobbler", "koito"] };

    private async Task<ProviderAccountRecord> AddScrobbleAccountAsync(string providerId = "lastfm")
    {
        var account = new ProviderAccountRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user,
            ProviderId = providerId,
            DisplayName = "Scrobble target",
            Enabled = true,
            Revision = 1,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };
        await using var db = await factory.CreateDbContextAsync();
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private sealed class ScrobbleSecrets : IProviderAccountSecretAccessor
    {
        public List<ProviderAccountContext> Used { get; } = [];
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken)
        {
            Used.Add(account);
            return operation(System.Text.Encoding.UTF8.GetBytes("{\"apiKey\":\"fixture-key\",\"sharedSecret\":\"fixture-secret\",\"sessionKey\":\"fixture-session\",\"token\":\"fixture-token\"}"));
        }
    }

    private sealed class ScrobbleHttp(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status = status;
        public int Calls;
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(Status) { Content = new StringContent("{\"scrobbles\":{\"scrobble\":{\"ignoredMessage\":{\"code\":\"0\"}},\"@attr\":{\"accepted\":\"1\",\"ignored\":\"0\"}}}") };
        }
    }

    private sealed class FailAcceptedCheckpoint(IPlaybackDeliveryCheckpointStore inner) : IPlaybackDeliveryCheckpointStore
    {
        private bool fail = true;
        public Task<bool> IsCompletedAsync(Guid owner, string signal, string target, CancellationToken token) => inner.IsCompletedAsync(owner, signal, target, token);
        public Task RecordAsync(Guid owner, string occurrence, string signal, PlaybackScrobbleDeliveryKind kind, string target, ScopedPlaybackScrobbleResult result, CancellationToken token)
        {
            if (fail && result.Outcome == ScopedPlaybackScrobbleOutcome.Delivered) { fail = false; throw new IOException("Checkpoint unavailable"); }
            return inner.RecordAsync(owner, occurrence, signal, kind, target, result, token);
        }
    }
}
