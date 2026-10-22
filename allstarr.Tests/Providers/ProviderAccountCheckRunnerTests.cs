using System.Net;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderAccountCheckRunnerTests
{
    [Fact]
    public async Task Sweep_ReusesManualProbesAndOnlyChecksEnabledExistingAccounts()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var enabled = Account("deezer", true);
        var disabled = Account("deezer", false);
        await using (var db = factory.CreateDbContext())
        {
            db.ProviderAccounts.AddRange(enabled, disabled);
            await db.SaveChangesAsync();
        }
        var handler = new Handler();
        var config = Config("deezer,lrclib");
        var time = new ManualTimeProvider();
        var manager = Manager(config, handler, time);
        var history = new ProviderCheckHistory(factory, time);
        var runner = Runner(factory, manager, history, config, time);
        var manual = await manager.TestManagedProviderCapabilityAsync("deezer", "metadata", enabled.Id, new Dictionary<string, string>(), accountRevision: enabled.Revision);
        handler.Paths.Clear();
        await runner.SweepAsync(default);
        Assert.Equal(3, handler.Paths.Count(path => path == "api.deezer.com/track/3135556"));
        Assert.Single(handler.Paths, path => path.StartsWith("lrclib.net/", StringComparison.Ordinal));
        var recorded = await history.ReadLatestAsync(default);
        Assert.DoesNotContain(recorded, result => result.Key.ProviderAccountId == disabled.Id);
        Assert.Equal(manual.Health, Assert.Single(recorded, result => result.Key.ProviderAccountId == enabled.Id && result.Key.Capability == "metadata").Status.Health);
        Assert.All(recorded, result => Assert.NotNull(result.Status.TestedAt));
        await using (var db = factory.CreateDbContext())
        {
            db.ProviderAccounts.Remove(await db.ProviderAccounts.SingleAsync(row => row.Id == enabled.Id));
            await db.SaveChangesAsync();
        }
        handler.Paths.Clear();
        await runner.SweepAsync(default);
        Assert.Single(handler.Paths, path => path == "api.deezer.com/track/3135556");
        var restarted = Manager(config, handler, time);
        await Runner(factory, restarted, history, config, time).RestoreAsync(default);
        Assert.Null(restarted.GetManagedStatus("deezer", "metadata", enabled.Id, new Dictionary<string, string>(), enabled.Revision).TestedAt);
    }

    [Fact]
    public async Task History_RestoresOriginalTimeBeyondRoutingSampleLifetimeAndRejectsChangedCredentials()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var account = Account("deezer", true);
        await using (var db = factory.CreateDbContext()) { db.ProviderAccounts.Add(account); await db.SaveChangesAsync(); }
        var time = new ManualTimeProvider();
        var config = Config("deezer");
        var handler = new Handler();
        var history = new ProviderCheckHistory(factory, time);
        var manager = Manager(config, handler, time);
        await Runner(factory, manager, history, config, time).SweepAsync(default);
        var testedAt = manager.GetAccountFreeStatus("deezer", "metadata").TestedAt;
        time.Advance(TimeSpan.FromHours(7));
        manager = Manager(config, handler, time);
        await Runner(factory, manager, history, config, time).RestoreAsync(default);
        Assert.True(manager.GetAccountFreeStatus("deezer", "metadata").IsReady);
        Assert.Equal(testedAt, manager.GetAccountFreeStatus("deezer", "metadata").TestedAt);
        Assert.False(manager.GetAccountFreeStatus("deezer", "metadata").CircuitOpen);
        Assert.Null(manager.GetManagedStatus("deezer", "metadata", account.Id, new Dictionary<string, string>(), account.Revision + 1).TestedAt);
        Assert.Empty(handler.Paths.Skip(3));
    }

    [Fact]
    public async Task Degradation_NotifiesHouseholdAdministratorsOncePerTransitionIncludingAfterRestart()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var time = new ManualTimeProvider();
        var config = Config("lrclib");
        var handler = new Handler();
        var history = new ProviderCheckHistory(factory, time);
        var runner = Runner(factory, Manager(config, handler, time), history, config, time);
        await runner.SweepAsync(default);
        time.Advance(TimeSpan.FromMinutes(1));
        handler.Healthy = false;
        await runner.SweepAsync(default);
        runner = Runner(factory, Manager(config, handler, time), history, config, time);
        await runner.RestoreAsync(default);
        time.Advance(TimeSpan.FromMinutes(1));
        await runner.SweepAsync(default);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.AuditEvents.Where(row => row.Action == "provider-degraded").ToListAsync());
        var feed = new AdminUpdateFeed(factory);
        var notices = await feed.ReadAsync(new(Guid.NewGuid(), true), new(DateTimeOffset.MinValue, 0, Guid.Empty, 0), 100, default);
        Assert.Single(notices, item => item.Action == "provider-degraded" && item.RequiresAdministrator);
        handler.Healthy = true;
        time.Advance(TimeSpan.FromMinutes(1));
        await runner.SweepAsync(default);
        handler.Healthy = false;
        time.Advance(TimeSpan.FromMinutes(1));
        await runner.SweepAsync(default);
        Assert.Equal(2, await db.AuditEvents.CountAsync(row => row.Action == "provider-degraded"));
    }

    [Fact]
    public async Task FirstAutomaticFailureAfterAManualSuccess_ProducesOneNotice()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var time = new ManualTimeProvider();
        var config = Config("lrclib");
        var handler = new Handler();
        var manager = Manager(config, handler, time);
        await manager.TestAccountFreeProviderCapabilityAsync("lrclib", "lyrics");
        var history = new ProviderCheckHistory(factory, time);
        var runner = Runner(factory, manager, history, config, time);
        handler.Healthy = false;
        time.Advance(TimeSpan.FromSeconds(1));
        await runner.SweepAsync(default);
        time.Advance(TimeSpan.FromSeconds(1));
        await runner.SweepAsync(default);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.AuditEvents.Where(row => row.Action == "provider-degraded").ToListAsync());
    }

    [Fact]
    public async Task SignedOutExtension_RecordsSignInNeededWithoutProbingAndOverridesPriorHealth()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var account = Account("fixture-extension", true);
        await using (var db = factory.CreateDbContext()) { db.ProviderAccounts.Add(account); await db.SaveChangesAsync(); }
        var implementation = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        implementation.SetupGet(item => item.ProviderId).Returns(account.ProviderId);
        implementation.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor(
            account.ProviderId, "Fixture", "Fixture extension", ProviderOrigin.Extension, "1", "1.0",
            [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                "1.0", ["searchTracks", "getTrack"], [ProviderAccountScope.Shared])], new ProviderPermissionDescriptor(), entryPoint: "index.js")
            { UsesSignedSession = true }, [implementation.Object])]);
        var time = new ManualTimeProvider();
        var config = Config("");
        var manager = Manager(config, new Handler(), time, registry);
        await manager.TestManagedProviderCapabilityAsync(account.ProviderId, "metadata", account.Id, new Dictionary<string, string>(), accountRevision: account.Revision);
        time.Advance(TimeSpan.FromSeconds(1));
        var sessions = new Mock<IProviderCheckSessions>();
        sessions.Setup(item => item.IsSignedInAsync(account.ProviderId, account.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var history = new ProviderCheckHistory(factory, time);
        var runner = new ProviderAccountCheckRunner(factory, manager, history, config, time,
            NullLogger<ProviderAccountCheckRunner>.Instance, registry: registry, sessions: sessions.Object);
        await runner.SweepAsync(default);
        var result = Assert.Single(await history.ReadLatestAsync(default));
        Assert.Equal("sign_in_needed", result.Status.ReasonCode);
        Assert.Equal(ProviderHealthState.Degraded, manager.GetManagedStatus(account.ProviderId, "metadata", account.Id, new Dictionary<string, string>(), account.Revision).Health);
        sessions.VerifyAll();
    }

    [Fact]
    public async Task CheckTimeout_IsRecordedAndWarningsContainNoResponseSecrets()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var time = new ManualTimeProvider();
        var config = Config("lrclib");
        var handler = new BlockingHandler();
        var logger = new CapturingLogger();
        var history = new ProviderCheckHistory(factory, time);
        var runner = new ProviderAccountCheckRunner(factory, Manager(config, handler, time), history, config, time, logger);
        var sweep = runner.SweepAsync(default);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await runner.SweepAsync(default));
        time.Advance(TimeSpan.FromSeconds(45));
        Assert.False(await sweep.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("timeout", Assert.Single(await history.ReadLatestAsync(default)).Status.ReasonCode);
        Assert.Single(logger.Messages);
        Assert.DoesNotContain("private-token", string.Join(" ", logger.Messages));
        Assert.DoesNotContain("https://", string.Join(" ", logger.Messages));
    }

    [Fact]
    public async Task Sweep_PausesBetweenChecksAndCancelsWithoutRecordingUnfinishedWork()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var time = new ManualTimeProvider();
        var config = new ConfigurationBuilder().AddConfiguration(Config("deezer,lrclib"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ProviderChecks:PauseMilliseconds"] = "500" }).Build();
        var handler = new Handler();
        var history = new ProviderCheckHistory(factory, time);
        var runner = Runner(factory, Manager(config, handler, time), history, config, time);
        using var cancellation = new CancellationTokenSource();
        var sweep = runner.SweepAsync(cancellation.Token);
        while (await time.NextTimerAsync() != TimeSpan.FromMilliseconds(500)) { }
        Assert.Single(handler.Paths);
        Assert.Single(await history.ReadLatestAsync(default));
        time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Single(handler.Paths);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep);
        Assert.Single(await history.ReadLatestAsync(default));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task History_ExpiresChecksAfterThirtyDays()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var time = new ManualTimeProvider();
        var history = new ProviderCheckHistory(factory, time);
        var config = Config("lrclib");
        var runner = Runner(factory, Manager(config, new Handler(), time), history, config, time);
        await runner.SweepAsync(default);
        time.Advance(TimeSpan.FromDays(31));
        Assert.Empty(await history.ReadLatestAsync(default));
        await runner.SweepAsync(default);
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.AuditEvents.Where(row => row.Category == ProviderCheckHistory.Category).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Streaming_UsesTheManualMediaDiagnosticAndPersistsItsSeekRung(bool managed)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var user = Guid.NewGuid();
        var recording = Guid.NewGuid();
        var account = managed ? Account("fixture-stream", true) : null;
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new()
            {
                Id = user,
                Enabled = true,
                IsAdmin = true,
                DisplayName = "Fixture",
                BackendType = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = "fixture"
            });
            if (account != null) db.ProviderAccounts.Add(account);
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = user, Revision = 1 });
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.NewGuid(),
                CanonicalRecordingId = recording,
                ProviderId = "fixture-stream",
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                ExternalId = "fixture-track",
                ExternalIdHash = new string('c', 64),
                CatalogNamespace = "default",
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "fixture",
                DecisionVersion = 1,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }
        var streamHealthy = true;
        var stream = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        stream.SetupGet(item => item.ProviderId).Returns("fixture-stream");
        stream.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        stream.Setup(item => item.GetStreamLeaseAsync(It.Is<ProviderExecutionContext>(context =>
                context.Account == null && !managed || context.Account != null && context.Account.AccountId == account!.Id),
            It.Is<ProviderStreamLeaseRequest>(request => request.TrackId.Value == "fixture-track")))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new ProviderStreamLease(
                "fixture", new Uri("https://media.example.test/sample?token=private-token"), DateTimeOffset.UtcNow.AddHours(1),
                true, true, new ProviderMediaFormat("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry,
                (_, _) =>
                {
                    var response = new HttpResponseMessage(streamHealthy ? HttpStatusCode.PartialContent : HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent(new byte[65536]) };
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 65535, 80000);
                    return Task.FromResult(response);
                })));
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor("fixture-stream", "Fixture", "Fixture stream",
            ProviderOrigin.Extension, "1", "1.0", [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                managed ? ProviderAccountRequirement.Required : ProviderAccountRequirement.None, "1.0", ["getStreamLease", "probeStream"],
                managed ? [ProviderAccountScope.Shared] : [])], new ProviderPermissionDescriptor(), entryPoint: "index.js"), [stream.Object])]);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        if (account != null)
            accounts.Setup(item => item.ResolveAsync(It.Is<ProviderRouteAccountRequest>(request => request.RequestedAccountId == account.Id),
                It.IsAny<CancellationToken>())).ReturnsAsync(new ProviderRouteAccountResolution(
                    new ProviderAccountContext(account.Id, account.ProviderId, ProviderAccountScope.Shared, account.Revision), account.Revision));
        using var selector = new ProviderCtsTrackSelector(factory);
        var media = new ProviderCtsDiagnosticRunner(registry, accounts.Object, selector, Mock.Of<IProviderOutcomeObserver>(), factory);
        var actor = new ProviderActorContext(ProviderActorKind.Administrator, user, new ProviderBackendPrincipal("jellyfin", "fixture", "fixture"));
        var manual = await media.MeasureAsync(actor, "fixture-stream", account?.Id, ProviderAudioQuality.HighResolution, "manual-check");
        var config = Config("");
        var time = new ManualTimeProvider();
        var manager = Manager(config, new Handler(), time, registry);
        var history = new ProviderCheckHistory(factory, time);
        var runner = new ProviderAccountCheckRunner(factory, manager, history, config, time,
            NullLogger<ProviderAccountCheckRunner>.Instance, registry: registry, media: media);
        Assert.True(await runner.SweepAsync(default));
        var recorded = Assert.Single(await history.ReadLatestAsync(default));
        Assert.True(manual.Succeeded);
        Assert.True(recorded.Status.IsReady);
        Assert.Equal("byte-range", recorded.SeekRung);
        Assert.Equal(manual.SeekRung, recorded.SeekRung);
        Assert.Equal(account?.Id, recorded.Key.ProviderAccountId);
        await using var ledger = factory.CreateDbContext();
        var rows = await ledger.AuditEvents.Where(row => row.Category == "provider-cts").ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.DoesNotContain("private-token", row.DetailsJson));
        stream.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Exactly(2));
        var scheduled = Assert.Single(rows, row => row.Action == "scheduled.measure");
        Assert.Null(scheduled.ActorUserId);
        Assert.Equal("recorded", scheduled.Outcome);
        var controller = new ProviderDiagnosticsController(factory, media, Mock.Of<IEffectiveProviderPolicyResolver>(), registry)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "other-admin",
            UserName = "Fixture",
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            IsAdministrator = true,
            AllstarrUserId = Guid.NewGuid()
        };
        foreach (var provider in new[] { "paxsenix", "lrclib", "lyricsplus", "musixmatch", "spotify", "fixture-metadata" })
            ledger.AuditEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                Category = "provider-cts",
                Action = "scheduled.measure",
                Outcome = "failed",
                DetailsJson = JsonSerializer.Serialize(ProviderCtsDiagnosticResult.Failure(400, provider, null, "capability", "unsupported_capability")),
                CreatedAt = DateTimeOffset.UtcNow
            });
        await ledger.SaveChangesAsync();
        using var latest = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await controller.LatestDeepStream(default)).Value));
        var measurement = Assert.Single(latest.RootElement.GetProperty("measurements").EnumerateArray());
        Assert.Equal("byte-range", measurement.GetProperty("SeekRung").GetString());

        streamHealthy = false;
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await runner.SweepAsync(default));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await runner.SweepAsync(default));
        var notices = await new AdminUpdateFeed(factory).ReadAsync(new(Guid.NewGuid(), true),
            new(DateTimeOffset.MinValue, 0, Guid.Empty, 0), 100, default);
        Assert.Single(notices, item => item.Action == "provider-degraded");

        stream.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Failure(ProviderError.ServiceUnavailable()));
        Assert.False(await runner.SweepAsync(default));
        Assert.Equal("service_unavailable", Assert.Single(await history.ReadLatestAsync(default)).Status.ReasonCode);

    }

    [Theory]
    [InlineData(null, 180)]
    [InlineData("60", 60)]
    public async Task DownloadStreaming_AutomaticCheckUsesTheDiagnosticTimeCap(string? configuredSeconds, int seconds)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var user = Guid.CreateVersion7();
        var recording = Guid.CreateVersion7();
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new()
            {
                Id = user,
                Enabled = true,
                IsAdmin = true,
                DisplayName = "Fixture",
                BackendType = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = "fixture"
            });
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = user });
            db.ProviderTrackIdentities.Add(new()
            {
                Id = Guid.CreateVersion7(),
                CanonicalRecordingId = recording,
                ProviderId = "fixture-stream",
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                ExternalId = "track",
                ExternalIdHash = new string('c', 64),
                CatalogNamespace = "default",
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "fixture",
                DecisionVersion = 1
            });
            await db.SaveChangesAsync();
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderOutcome<ProviderStreamLease>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Mock<IProviderDownloadStreamingCapability>();
        stream.SetupGet(item => item.ProviderId).Returns("fixture-stream");
        stream.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        stream.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns<ProviderExecutionContext, ProviderStreamLeaseRequest>((context, _) =>
            {
                entered.TrySetResult();
                return release.Task.WaitAsync(context.CancellationToken);
            });
        var registry = new ProviderRegistry([new(new ProviderDescriptor("fixture-stream", "Fixture", "Fixture",
            ProviderOrigin.Extension, "1", "1", [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["getStreamLease"], [])], new(), entryPoint: "index.js"), [stream.Object])]);
        var config = Config("");
        if (configuredSeconds != null) config["ProtocolExecution:DownloadDiagnosticTimeoutSeconds"] = configuredSeconds;
        var options = new allstarr.Core.Protocols.ProtocolExecutionOptions();
        config.GetSection(allstarr.Core.Protocols.ProtocolExecutionOptions.SectionName).Bind(options);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var selector = new ProviderCtsTrackSelector(factory);
        var media = new ProviderCtsDiagnosticRunner(registry, Mock.Of<IProviderRouteAccountResolver>(), selector,
            Mock.Of<IProviderOutcomeObserver>(), executionOptions: options, timeProvider: time);
        var history = new ProviderCheckHistory(factory, time);
        var runner = new ProviderAccountCheckRunner(factory, Manager(config, new Handler(), time, registry), history, config, time,
            NullLogger<ProviderAccountCheckRunner>.Instance, registry: registry, media: media);
        var pending = runner.SweepAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), await time.NextTimerAsync());
            time.Advance(TimeSpan.FromSeconds(seconds - 1));
            Assert.False(pending.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("timeout", Assert.Single(await history.ReadLatestAsync(default)).Status.ReasonCode);
        }
        finally { release.TrySetResult(ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.Canceled))); }
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("busy")]
    [InlineData("timeout")]
    public async Task LyricsSweep_UsesTheBoundedLyricsProbeAndIgnoresStoredPlayback(string outcome)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var config = Config("paxsenix");
        var handler = new LyricsHandler(outcome);
        var apple = AppleProviderTestFactory.Client(new HttpClient(new AppleProviderTestFactory.Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                """{"data":[{"id":"987654","attributes":{"name":"Never Gonna Give You Up","artistName":"Rick Astley"}}]}""")
            })));
        using var client = new HttpClient(handler);
        var adapter = new allstarr.Core.Providers.Lyrics.PaxsenixCapabilityAdapter(client, null,
            new allstarr.Core.Providers.Lyrics.PaxsenixRequestPacer(time, TimeSpan.Zero), time,
            new AppleMusicKitMetadataCapabilityAdapter(apple));
        var manager = Manager(config, handler, time, configureServices: services => services.AddSingleton(apple).AddSingleton(adapter));
        var history = new ProviderCheckHistory(factory, time);
        var staleKey = ProviderRuntimeStatusKey.CreateAccountFree("paxsenix", "streaming");
        await history.RecordAsync(new(staleKey, null, new ProviderRuntimeStatus
        {
            Provider = "paxsenix",
            Capability = "streaming",
            IsSupported = true,
            IsEnabled = true,
            Configuration = ProviderConfigurationState.NotRequired,
            Health = ProviderHealthState.Degraded,
            TestedAt = time.GetUtcNow(),
            ReasonCode = "probe_failed"
        }), "old-check", default);
        var runner = Runner(factory, manager, history, config, time);
        await runner.RestoreAsync(default);
        var pending = runner.SweepAsync(default);
        if (outcome == "timeout")
        {
            await time.WaitForTimersAsync(TimeSpan.FromSeconds(8));
            time.Advance(TimeSpan.FromSeconds(8));
        }
        Assert.Equal(outcome == "healthy", await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        var status = Assert.Single(manager.GetAllAccountFreeStatuses(), item => item.Provider == "paxsenix");
        Assert.Equal("lyrics", status.Capability);
        Assert.Equal(outcome == "healthy" ? ProviderHealthState.Healthy : ProviderHealthState.Degraded, status.Health);
        Assert.Equal(outcome == "healthy" ? null : "lyrics-upstream-busy-or-timed-out", status.ReasonCode);
        Assert.InRange(status.LatencyMilliseconds!.Value, 0, 20000);
        Assert.All(handler.Paths, path => Assert.Equal("/apple-music/lyrics", path));
        Assert.NotEmpty(handler.Paths);
        var recorded = Assert.Single(await history.ReadLatestAsync(default), item => item.Key.Capability == "lyrics");
        Assert.Equal(status.Health, recorded.Status.Health);
    }

    [Fact]
    public async Task LimitedCapabilitySweep_ChecksOnlyDeclaredLyricsAndMetadata()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.SetupGet(item => item.ProviderId).Returns("fixture-metadata");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture-metadata",
                [new(new("fixture-metadata", ProviderResourceKind.Track, "track"), "Song", [new("Artist")])])));
        var lyrics = new Mock<IProviderLyricsCapability>(MockBehavior.Strict);
        lyrics.SetupGet(item => item.ProviderId).Returns("fixture-lyrics");
        lyrics.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Lyrics);
        lyrics.Setup(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderLyricsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderLyricsResult>.Failure(ProviderError.LyricsUpstreamBusyOrTimedOut()));
        var registry = new ProviderRegistry([
            new(new ProviderDescriptor("fixture-metadata", "Metadata", "Metadata source", ProviderOrigin.Extension, "1", "1",
                [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                    "1", ["searchTracks"])], new(), entryPoint: "index.js"), [metadata.Object]),
            new(new ProviderDescriptor("fixture-lyrics", "Lyrics", "Lyrics source", ProviderOrigin.Extension, "1", "1",
                [new(ProviderCapabilityKind.Lyrics, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                    "1", ["fetchLyrics"])], new(), entryPoint: "index.js"), [lyrics.Object])]);
        var config = Config("");
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var manager = Manager(config, new Handler(), time, registry, services => services
            .AddSingleton(new ProviderDiagnosticExecutionFactory(registry, Mock.Of<IProviderRouteAccountResolver>()))
            .AddSingleton(new ProviderTryOperations(registry)));
        var history = new ProviderCheckHistory(factory, time);
        Assert.False(await Runner(factory, manager, history, config, time).SweepAsync(default));
        var recorded = await history.ReadLatestAsync(default);
        Assert.Equal(2, recorded.Count);
        Assert.Equal(ProviderHealthState.Healthy, Assert.Single(recorded, item => item.Key.Capability == "metadata").Status.Health);
        Assert.Equal("lyrics-upstream-busy-or-timed-out", Assert.Single(recorded, item => item.Key.Capability == "lyrics").Status.ReasonCode);
        metadata.Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Once);
        lyrics.Verify(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderLyricsRequest>()), Times.Once);
    }

    private sealed class LyricsHandler(string outcome) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (outcome == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(outcome == "healthy" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("""{"type":"Line","content":[{}],"lrc":"[00:01]Words"}""") };
        }
    }

    private static ProviderAccountRecord Account(string provider, bool enabled) => new()
    { Id = Guid.NewGuid(), ProviderId = provider, Enabled = enabled, DisplayName = "Fixture", Revision = 1 };
    private static IConfiguration Config(string enabled)
    {
        var all = new[] { "apple-musickit", "qobuz", "deezer", "spotify", "lrcred", "unison", "lrclib", "lyricsplus", "musixmatch", "lastfm", "listenbrainz", "multi-scrobbler", "koito", "audiomuse-ai" };
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MULTI_PROVIDER_DISABLED_PROVIDERS"] = string.Join(',', all.Concat(new[] { "paxsenix", "genius" }).Except(enabled.Split(','))),
            ["ProviderChecks:PauseMilliseconds"] = "0"
        }).Build();
    }
    private static ProviderAccountCheckRunner Runner(ProviderCheckServiceTests.Factory factory, ProviderStatusManager manager,
        ProviderCheckHistory history, IConfiguration config, TimeProvider time) => new(factory, manager, history, config, time, NullLogger<ProviderAccountCheckRunner>.Instance);
    private static ProviderStatusManager Manager(IConfiguration config, HttpMessageHandler handler, TimeProvider time, IProviderRegistry? registry = null, Action<ServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        if (registry != null) services.AddSingleton(registry);
        configureServices?.Invoke(services);
        return new(config, new HttpFactory(handler), NullLogger<ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()),
            Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()),
            services: services.BuildServiceProvider(), clock: new Clock(time));
    }
    private sealed class Clock(TimeProvider time) : IPlatformClock { public DateTimeOffset UtcNow => time.GetUtcNow(); }
    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Handler : HttpMessageHandler
    {
        public bool Healthy = true;
        public List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri!.Host + request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(Healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("{\"id\":3135556}") });
        }
    }
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new HttpRequestException("https://fixture.invalid/?token=private-token");
        }
    }
    private sealed class CapturingLogger : ILogger<ProviderAccountCheckRunner>
    {
        public List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Warning) Messages.Add(formatter(state, exception)); }
    }
}
