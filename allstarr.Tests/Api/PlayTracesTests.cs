using System.Reflection;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Middleware;
using allstarr.Filters;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class PlayTracesTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task ListenerReadsOnlyOwnPlaysAndAdministratorReadsAllAcrossRestart(ProtocolKind protocol)
    {
        await using var fixture = await Fixture.CreateAsync(protocol);
        var own = await fixture.PlayAsync(fixture.Listener);
        var other = await fixture.PlayAsync(fixture.Admin);
        var restarted = fixture.Service();
        Assert.Equal(own, Assert.Single(await restarted.ListAsync(fixture.Listener, 100, default)).Id);
        Assert.Equal(2, (await restarted.ListAsync(fixture.Admin, 100, default)).Count);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemReportException>(() => restarted.DetailAsync(fixture.Listener, other, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemReportException>(() => restarted.DiagnosticsAsync(fixture.Listener, other, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemReportException>(() => restarted.ListAsync(fixture.Listener, 10, default, other))).StatusCode);
        var trace = await restarted.DetailAsync(fixture.Listener, own, default);
        Assert.Equal(protocol.ToString(), trace.Request.Protocol);
        Assert.Equal(fixture.Listener.AllstarrUserId, trace.ListenerId);
        Assert.Equal("ext-deezer-song-123", trace.Request.ItemId);
        Assert.Equal("Requested song", trace.Request.Presented.Title);
        Assert.Equal("mobile", trace.Request.Client);
        Assert.Equal("Phone", trace.Request.Device);
        Assert.Equal("flac", trace.Received!.Media.Codec);
        Assert.Equal(15, trace.Timing.FirstByteMs);
        fixture.Access.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CopyDiagnosticsOmitsIdentityAndRedactsEveryFreeTextSurface()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var timing = fixture.Timing(fixture.Listener);
        timing.Present(new Song
        {
            Title = "Song token=private-token",
            Artist = $"Artist 203.0.113.7 {fixture.Listener.AllstarrUserId} {fixture.Listener.AllstarrUserId!.Value:N} listener@example.test",
            Album = "Cookie: private-cookie",
            CoverArtUrl = "https://art.example.test/image?sig=private-signature",
            Duration = 200
        });
        timing.Resolve(new(ExternalSnapshotId: Guid.NewGuid(), Provider: "deezer", AccountLabel: "private-account", Cached: true));
        timing.Receive(new(new("audio/flac", "flac", "flac"), DurationSeconds: 200,
            Tags: new("https://media.example.test/private-path", "Bearer private-bearer", "password=private-password", "USABC1234567", null)));
        await fixture.Store.RecordAsync(timing.Snapshot("completed"));
        var json = JsonSerializer.Serialize(await fixture.Service().DiagnosticsAsync(fixture.Listener, timing.Id, default));
        foreach (var secret in new[] { "private-", "203.0.113.7", fixture.Listener.AllstarrUserId!.Value.ToString(), fixture.Listener.AllstarrUserId!.Value.ToString("N"), fixture.Listener.UserId, "private-account", "Phone", "mobile", "listener@example.test" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("flac", json);
        Assert.Contains("USABC1234567", json);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.DoesNotContain("private-token", (await db.AuditEvents.SingleAsync()).DetailsJson);
    }

    [Theory]
    [InlineData(200, 240, "Song", "Other", "Artist", "Other artist", true, false, 4)]
    [InlineData(200, 201, "Song!", "song", "Artist", "ARTIST", false, false, 0)]
    [InlineData(null, null, null, null, null, null, null, null, 0)]
    public void DifferencesRequireKnownEvidence(int? expected, int? actual, string? title, string? receivedTitle,
        string? artist, string? receivedArtist, bool? explicitExpected, bool? explicitActual, int flags)
    {
        var request = new PlayTraceMetadata(title, artist, null, expected * 1000L, null, explicitExpected);
        var received = new TrackMediaFacts(new("audio/flac", "flac", "flac"), DurationSeconds: actual,
            Tags: new(receivedTitle, receivedArtist, null, null, explicitActual));
        Assert.Equal(flags, PlayTraceDifferences.Compare(request, received).Count);
    }

    [Fact]
    public async Task TraceRetentionUsesTimingAgeAndCountBoundsAndHidesExpiredRowsWithoutWrites()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var timing = fixture.Timing(fixture.Listener);
        var sample = timing.Snapshot("completed");
        await using (var db = fixture.Factory.CreateDbContext())
        {
            for (var index = 0; index < 520; index++) db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = fixture.Listener.AllstarrUserId,
                Category = PlaybackStartTimingStore.Category,
                SubjectType = PlaybackStartTimingStore.Category,
                SubjectId = "recent",
                CreatedAt = fixture.Clock.GetUtcNow().AddSeconds(-index),
                DetailsJson = JsonSerializer.Serialize(sample)
            });
            await db.SaveChangesAsync();
        }
        await fixture.Store.RecordAsync(sample);
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Equal(500, await check.AuditEvents.CountAsync());
        var page = await fixture.Service().ListAsync(fixture.Listener, int.MaxValue, default);
        Assert.Equal(100, page.Count);
        var next = await fixture.Service().ListAsync(fixture.Listener, 100, default, page[^1].Id);
        Assert.Equal(100, next.Count);
        Assert.Empty(page.Select(item => item.Id).Intersect(next.Select(item => item.Id)));
        fixture.Clock.Advance(TimeSpan.FromDays(8));
        Assert.Empty(await fixture.Service().ListAsync(fixture.Admin, 100, default));
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service().DetailAsync(fixture.Listener, sample.Id, default))).StatusCode);
    }

    [Fact]
    public async Task RevokedOrChangedSessionCannotReadTrace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.PlayAsync(fixture.Listener);
        await using var db = fixture.Factory.CreateDbContext();
        (await db.Users.SingleAsync(user => user.Id == fixture.Listener.AllstarrUserId)).Enabled = false;
        await db.SaveChangesAsync();
        Assert.Equal(401, (await Assert.ThrowsAsync<ProblemReportException>(() => fixture.Service().DetailAsync(fixture.Listener, id, default))).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/admin/play-traces", true)]
    [InlineData("GET", "/api/admin/play-traces/00000000-0000-0000-0000-000000000001", true)]
    [InlineData("GET", "/api/admin/play-traces/00000000-0000-0000-0000-000000000001/diagnostics", true)]
    [InlineData("POST", "/api/admin/play-traces/00000000-0000-0000-0000-000000000001/report", true)]
    [InlineData("POST", "/api/admin/play-traces/00000000-0000-0000-0000-000000000001/version", true)]
    [InlineData("POST", "/api/admin/play-traces/00000000-0000-0000-0000-000000000001/forget", true)]
    [InlineData("POST", "/api/admin/play-traces", false)]
    [InlineData("GET", "/api/admin/play-traces/extra", false)]
    [InlineData("DELETE", "/api/admin/play-traces/00000000-0000-0000-0000-000000000001", false)]
    public void ListenerRoutePolicyIsBounded(string method, string path, bool expected)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method; http.Request.Path = path;
        var policy = typeof(AdminAuthenticationMiddleware).GetMethod("IsAllowedForNonAdministrator", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, policy.Invoke(null, [http.Request]));
    }

    [Fact]
    public async Task ActionsCheckTraceOwnershipAndHouseholdPermissionBeforeCallingServices()
    {
        Assert.Equal(typeof(AdminPortFilter), Assert.Single(typeof(PlayTracesController).GetCustomAttributes<ServiceFilterAttribute>()).ServiceType);
        await using var fixture = await Fixture.CreateAsync();
        var own = await fixture.PlayAsync(fixture.Listener);
        var other = await fixture.PlayAsync(fixture.Admin);
        var matches = new Mock<ITrackMatchRepository>(MockBehavior.Strict);
        var controller = new PlayTracesController(fixture.Service(), matches.Object, new(fixture.Factory, fixture.PlatformClock), fixture.Reports)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedObjectResult>(await controller.Detail(own, default));
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = fixture.Listener;
        Assert.Equal(404, Assert.IsType<ObjectResult>(await controller.Version(other, new("provider", ExternalProvider: "deezer", ExternalId: "456"), default)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Version(own, new("provider", AuthorityScope: "household"), default)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Forget(own, new("household"), default)).StatusCode);
        Assert.Equal(404, Assert.IsType<ObjectResult>(await controller.Report(other, new(Guid.NewGuid(), "Wrong song"), default)).StatusCode);
        matches.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, false)]
    [InlineData(ProtocolKind.Subsonic, true)]
    public async Task StreamOpeningFreezesRequestedSnapshotAndReceivedFacts(ProtocolKind protocol, bool originalDownload)
    {
        await using var fixture = await Fixture.CreateAsync(protocol);
        var snapshot = await fixture.SnapshotAsync(fixture.Listener);
        var account = Guid.NewGuid();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.ProviderAccounts.Add(new() { Id = account, ProviderId = "deezer", DisplayName = "Shared music", Enabled = true, Revision = 1 });
            await db.SaveChangesAsync();
        }
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var media = new ProviderMediaFormat("audio/flac", "flac", "flac", 900000, 44100, 16, 2);
        using var upstream = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        var routed = new ProtocolProviderStream(upstream, new("fixture", new("https://media.example.test/audio"),
            fixture.Clock.GetUtcNow().AddHours(1), true, true, media, ProviderStreamRetryBehavior.DoNotRetry), "deezer", "123", ServingAccountId: account, IsCached: true, SelectionReason: "replay:managed-cache")
        { FileFacts = new() { Measured = new(media, DurationSeconds: 220, Tags: new("Different song", "Artist", IsExplicit: false)) } };
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "123", ProviderAudioQuality.Any, null, false)).ReturnsAsync(routed);
        var opener = new ProviderStreamOpener(gateway.Object, new(), timings: fixture.Store, traces: fixture.Service());
        var http = new DefaultHttpContext(); http.Request.Method = "GET"; http.Response.Body = new MemoryStream();
        http.Request.Path = originalDownload
            ? protocol == ProtocolKind.Jellyfin ? "/Items/ext-deezer-song-123/Download" : "/rest/download"
            : protocol == ProtocolKind.Jellyfin ? "/Audio/ext-deezer-song-123/stream" : "/rest/stream";
        var execution = fixture.Context(fixture.Listener, originalDownload);
        await new PlaybackStartTimingMiddleware(async context =>
        {
            var result = Assert.IsType<FileStreamResult>(await opener.OpenAsync(context, execution, "deezer", "123", ProviderAudioQuality.Any));
            await result.FileStream.CopyToAsync(context.Response.Body);
            await result.FileStream.DisposeAsync();
        }, fixture.Store, fixture.Clock).InvokeAsync(http);
        var trace = Assert.Single(await fixture.Service().ListAsync(fixture.Listener, 10, default));
        Assert.Equal(snapshot, trace.Resolution.ExternalSnapshotId);
        Assert.Equal("123", trace.Resolution.ExternalId);
        Assert.Equal("replay:managed-cache", trace.Resolution.Route);
        Assert.Equal("Shared music", trace.Resolution.AccountLabel);
        Assert.True(trace.Resolution.Cached);
        Assert.Equal("Catalog song", trace.Request.Presented.Title);
        Assert.Equal("Artist", trace.Request.Presented.Artist);
        Assert.Equal(200000, trace.Request.Presented.DurationMilliseconds);
        Assert.Contains("title-mismatch", trace.Differences);
        Assert.Contains("duration-mismatch", trace.Differences);
        Assert.Contains("explicit-clean-mismatch", trace.Differences);
        Assert.Equal(trace.Id.ToString("N"), http.Response.Headers["X-Allstarr-Playback-Request"]);
        await fixture.SnapshotAsync(fixture.Listener, 2, "{}");
        Assert.Equal("Catalog song", (await fixture.Service().DetailAsync(fixture.Listener, trace.Id, default)).Request.Presented.Title);
        gateway.VerifyAll(); gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WrongSongReportUsesFrozenPlayAndExistingIdempotence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.PlayAsync(fixture.Listener);
        fixture.PlatformClock.UtcNow = fixture.Clock.GetUtcNow().AddHours(1);
        var controller = fixture.Controller(fixture.Listener, new Mock<ITrackMatchRepository>(MockBehavior.Strict));
        var request = new PlayTraceReportRequest(Guid.NewGuid(), "Heard a different song");
        var first = Assert.IsType<ObjectResult>(await controller.Report(id, request, default));
        Assert.Equal(201, first.StatusCode);
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.Report(id, request, default)).StatusCode);
        var receipt = Assert.IsType<ProblemReportReceipt>(first.Value);
        Assert.Equal(fixture.PlatformClock.UtcNow, receipt.CreatedAt);
        var report = await fixture.Reports.DetailAsync(fixture.Admin, receipt.Id, default);
        Assert.Equal("wrong-match", report.Kind);
        Assert.Equal(id, report.Context.PlaybackRequestId);
        Assert.Equal(fixture.Listener.AllstarrUserId, report.Context.SubjectUserId);
        Assert.Equal("Requested song", report.Context.Song!.Title);
        fixture.Access.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, "personal")]
    [InlineData(true, "household")]
    public async Task VersionChoiceUsesExplicitAuthorityScope(bool admin, string scope)
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = admin ? fixture.Admin : fixture.Listener;
        var id = await fixture.PlayAsync(session);
        var trace = await fixture.Service().DetailAsync(session, id, default);
        var matches = new Mock<ITrackMatchRepository>(MockBehavior.Strict);
        matches.Setup(item => item.ResolveSnapshotAsync(new(session.AllstarrUserId!.Value, admin), trace.Resolution.ExternalSnapshotId!.Value,
            It.Is<ResolveTrackMatchCommand>(command => command.AuthorityScope == scope && command.ExternalProvider == "qobuz" && command.ExternalId == "456"),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(TrackMatchCommandResult.Success(trace.Resolution.ExternalSnapshotId!.Value));
        await fixture.Controller(session, matches).Version(id, new("provider", ExternalProvider: "qobuz", ExternalId: "456", AuthorityScope: scope), default);
        matches.VerifyAll(); matches.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ForgetReleasesOnlyTheChosenManualLayerAndRejectsStaleAuthority()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = await fixture.SnapshotAsync(fixture.Listener);
        var authority = Guid.NewGuid();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var source = await db.ExternalMetadataSnapshots.SingleAsync();
            db.ManualTrackOverrides.Add(new()
            {
                Id = authority,
                OwnerUserId = fixture.Listener.AllstarrUserId,
                ExternalSnapshotId = snapshot,
                SourceProviderId = source.ProviderId,
                SourceExternalIdHash = source.ExternalIdHash,
                Decision = ManualOverrideDecision.Pin,
                TargetProviderId = "qobuz",
                TargetExternalId = "456",
                Revision = 2,
                DecisionVersion = 1,
                Reason = "Fixture"
            });
            await db.SaveChangesAsync();
        }
        using var timing = fixture.Timing(fixture.Listener);
        await fixture.Service().CaptureAsync(timing, fixture.Context(fixture.Listener), "deezer", "123");
        await fixture.Store.RecordAsync(timing.Snapshot("completed"));
        var matches = new Mock<ITrackMatchRepository>(MockBehavior.Strict);
        matches.Setup(item => item.RematchManualAuthorityAsync(It.Is<ProtocolExecutionContext>(context => context.Principal!.UserId == fixture.Listener.AllstarrUserId),
            snapshot, ManualTrackAuthorityKind.ProviderMatch, authority, 2, It.IsAny<string>(), It.IsAny<CancellationToken>(), "personal"))
            .ReturnsAsync(new TrackRematchCommandResult(true));
        var controller = fixture.Controller(fixture.Listener, matches);
        await controller.Forget(timing.Id, new(), default);
        matches.VerifyAll();
        Assert.Equal(409, Assert.IsType<ConflictObjectResult>(await controller.Forget(timing.Id, new(ExpectedAuthority: new(authority, 1)), default)).StatusCode);
        matches.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CanonicalRequestCapturesOnlyOwnedEvidenceAndFreezesDecisionScore()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = await fixture.SnapshotAsync(fixture.Listener);
        var foreign = await fixture.SnapshotAsync(fixture.Admin);
        var recording = Guid.NewGuid();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.CanonicalRecordings.Add(new()
            {
                Id = recording,
                CreatedByUserId = fixture.Listener.AllstarrUserId!.Value,
                PublicId = CanonicalRecordingRecord.DefaultPublicId(recording),
                Title = "Canonical song"
            });
            foreach (var (source, owner) in new[] { (snapshot, fixture.Listener.AllstarrUserId!.Value), (foreign, fixture.Admin.AllstarrUserId!.Value) })
                db.TrackMatches.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    ExternalSnapshotId = source,
                    CanonicalRecordingId = recording,
                    State = TrackMatchState.Accepted,
                    Confidence = .93,
                    Threshold = .9,
                    DecisionVersion = 1,
                    SourceSnapshotVersion = 1
                });
            await db.SaveChangesAsync();
        }
        using var timing = fixture.Timing(fixture.Listener);
        await fixture.Service().CaptureAsync(timing, fixture.Context(fixture.Listener), "allstarr", recording.ToString("D"));
        await fixture.Store.RecordAsync(timing.Snapshot("completed"));
        await using (var db = fixture.Factory.CreateDbContext())
        {
            db.TrackMatches.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = fixture.Listener.AllstarrUserId!.Value,
                ExternalSnapshotId = snapshot,
                CanonicalRecordingId = recording,
                State = TrackMatchState.Suggested,
                Confidence = .5,
                Threshold = .9,
                DecisionVersion = 2,
                SourceSnapshotVersion = 1
            });
            await db.SaveChangesAsync();
        }
        var trace = await fixture.Service().DetailAsync(fixture.Listener, timing.Id, default);
        Assert.Equal(snapshot, trace.Resolution.ExternalSnapshotId);
        Assert.Equal(recording, trace.Resolution.CanonicalRecordingId);
        Assert.Equal(.93, trace.Resolution.Score);
        Assert.Equal("accepted", trace.Resolution.State);
    }

    [Fact]
    public async Task FailedOpeningRetainsOwnRequestWithoutExceptionText()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "123", ProviderAudioQuality.Any, null, false))
            .ThrowsAsync(new HttpRequestException("private-upstream-error"));
        var opener = new ProviderStreamOpener(gateway.Object, new(), timings: fixture.Store, traces: fixture.Service());
        var http = new DefaultHttpContext(); http.Request.Method = "GET";
        await Assert.ThrowsAsync<HttpRequestException>(() => new PlaybackStartTimingMiddleware(async context =>
            await opener.OpenAsync(context, fixture.Context(fixture.Listener), "deezer", "123", ProviderAudioQuality.Any), fixture.Store, fixture.Clock).InvokeAsync(http));
        var trace = Assert.Single(await fixture.Service().ListAsync(fixture.Listener, 10, default));
        Assert.Equal("failed", trace.Outcome);
        Assert.Null(trace.Received);
        Assert.DoesNotContain("private-upstream-error", JsonSerializer.Serialize(await fixture.Service().DiagnosticsAsync(fixture.Listener, trace.Id, default)));
    }

    [Fact]
    public async Task DeliveryPreservesRequestedLimitsAndChosenConversion()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var timing = fixture.Timing(fixture.Listener);
        var request = new ProviderAudioDeliveryRequest
        {
            MaxBitrate = 192000,
            MaxSampleRate = 48000,
            PreferredCodec = "mp3",
            Formats = [new(["mp3"], ["mp3"], [])]
        };
        var source = new ProviderMediaFormat("audio/flac", "flac", "flac", 900000, 96000, 24, 2);
        var conversion = ProviderAudioTranscoder.Plan(request, source);
        Assert.NotNull(conversion);
        timing.Receive(new(source)); timing.Deliver(request, conversion);
        await fixture.Store.RecordAsync(timing.Snapshot("completed"));
        var trace = await fixture.Service().DetailAsync(fixture.Listener, timing.Id, default);
        Assert.True(trace.Delivery!.Converted);
        Assert.Equal(192000, trace.Delivery.MaxBitrate);
        Assert.Equal("mp3", Assert.Single(trace.Delivery.Codecs));
        Assert.Equal("mp3", trace.Delivery.Output!.Codec);
        Assert.Equal("flac", trace.Received!.Media.Codec);
    }

    [Theory]
    [InlineData("Deezer", "deezer")]
    [InlineData("applemusic", "apple-musickit")]
    [InlineData("apple-download", "apple-musickit")]
    public async Task SourceAliasesUseTheStoredProviderIdentity(string requestedProvider, string storedProvider)
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = await fixture.SnapshotAsync(fixture.Listener, provider: storedProvider);
        using var timing = fixture.Timing(fixture.Listener);
        await fixture.Service().CaptureAsync(timing, fixture.Context(fixture.Listener), requestedProvider, "123");
        Assert.Equal(snapshot, timing.Snapshot("completed").Trace!.Resolution.ExternalSnapshotId);
    }

    [Fact]
    public async Task RetainedPlaybackMetadataCanSupplyAReviewableSource()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshot = await fixture.SnapshotAsync(fixture.Listener, kind: "playback-metadata",
            payload: """{"Title":"Direct song","Artist":"Artist","DurationMilliseconds":210000,"ExplicitContentLyrics":0}""");
        using var timing = fixture.Timing(fixture.Listener);
        await fixture.Service().CaptureAsync(timing, fixture.Context(fixture.Listener), "deezer", "123");
        var trace = timing.Snapshot("completed").Trace!;
        Assert.Equal(snapshot, trace.Resolution.ExternalSnapshotId);
        Assert.Equal("Direct song", trace.Request.Presented.Title);
        Assert.False(trace.Request.Presented.IsExplicit);
    }

    private sealed class Fixture(SqliteTestDatabase database, ProtocolKind protocol) : IAsyncDisposable
    {
        public ProviderCheckServiceTests.Factory Factory { get; } = new(database.Options);
        public ManualTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-10-28T00:00:00Z"));
        public ProblemReportsTestsClock PlatformClock { get; } = new();
        public Mock<IBackendLibraryAccessResolver> Access { get; } = new(MockBehavior.Strict);
        public AdminAuthSession Listener { get; } = Session(protocol, false);
        public AdminAuthSession Admin { get; } = Session(protocol, true);
        public PlaybackStartTimingStore Store => new(Factory, Clock, NullLogger<PlaybackStartTimingStore>.Instance);
        public ProblemReportService Reports => new(Factory, PlatformClock, Access.Object, []);
        public PlayTraceService Service() => new(Factory, Clock, Reports);
        public static async Task<Fixture> CreateAsync(ProtocolKind protocol = ProtocolKind.Jellyfin)
        {
            var fixture = new Fixture(await SqliteTestDatabase.CreateAsync(), protocol);
            await using var db = fixture.Factory.CreateDbContext();
            foreach (var session in new[] { fixture.Listener, fixture.Admin }) db.Users.Add(new()
            {
                Id = session.AllstarrUserId!.Value,
                Enabled = true,
                DisplayName = session.UserName,
                BackendType = protocol.ToString().ToLowerInvariant(),
                BackendInstanceId = "fixture",
                BackendPrincipalId = session.UserId,
                IsAdmin = session.IsAdministrator
            });
            await db.SaveChangesAsync();
            return fixture;
        }
        public PlayTracesController Controller(AdminAuthSession session, Mock<ITrackMatchRepository> matches)
        {
            var controller = new PlayTracesController(Service(), matches.Object, new(Factory, PlatformClock), Reports)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
            controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
            return controller;
        }
        public ProtocolExecutionContext Context(AdminAuthSession session, bool originalDownload = false)
        {
            var principal = new AllstarrPrincipal(session.AllstarrUserId!.Value, protocol.ToString().ToLowerInvariant(), "fixture", session.UserId, session.UserName, session.IsAdministrator);
            return new(protocol, "fixture", session.UserId, principal, "fixture", Clock.GetUtcNow().AddMinutes(1), default, new("mobile", "private-device", "Phone")) { IsOriginalDownload = originalDownload };
        }
        public async Task<Guid> SnapshotAsync(AdminAuthSession session, int version = 1, string? payload = null, string provider = "deezer", string kind = "track")
        {
            await using var db = Factory.CreateDbContext();
            var id = Guid.NewGuid();
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = id,
                OwnerUserId = session.AllstarrUserId!.Value,
                Protocol = protocol.ToString().ToLowerInvariant(),
                BackendInstanceId = "fixture",
                BackendPrincipalId = session.UserId,
                ProviderId = provider,
                ResourceKind = kind,
                ExternalIdHash = CanonicalCatalogKeys.Hash("123"),
                SnapshotVersion = version,
                ProviderRevision = "fixture",
                PayloadSha256 = new string('a', 64),
                PayloadJson = payload ?? """{"title":"Catalog song","artist":"Artist","album":"Album","durationMilliseconds":200000,"isExplicit":true}""",
                RetrievedAt = Clock.GetUtcNow().AddSeconds(version)
            });
            await db.SaveChangesAsync();
            return id;
        }
        public PlaybackStartTimings Timing(AdminAuthSession session)
        {
            var timing = new PlaybackStartTimings(Clock);
            timing.Enable();
            var principal = new AllstarrPrincipal(session.AllstarrUserId!.Value, protocol.ToString().ToLowerInvariant(), "fixture", session.UserId, session.UserName, session.IsAdministrator);
            timing.BeginPlay(new(protocol, "fixture", session.UserId, principal, "fixture", Clock.GetUtcNow().AddMinutes(1), default,
                new("mobile", "private-device", "Phone")), "ext-deezer-song-123");
            return timing;
        }
        public async Task<Guid> PlayAsync(AdminAuthSession session)
        {
            using var timing = Timing(session);
            timing.Present(new Song { Title = "Requested song", Artist = "Artist", Album = "Album", Duration = 200 });
            timing.Resolve(new(ExternalSnapshotId: Guid.NewGuid(), Provider: "deezer", AccountLabel: "Shared account", Cached: true));
            timing.Receive(new(new("audio/flac", "flac", "flac", 900000), DurationSeconds: 200));
            Clock.Advance(TimeSpan.FromMilliseconds(15)); timing.Headers(); timing.FirstByte();
            await Store.RecordAsync(timing.Snapshot("completed"));
            return timing.Id;
        }
        public ValueTask DisposeAsync() => database.DisposeAsync();
        private static AdminAuthSession Session(ProtocolKind protocol, bool admin) => new()
        {
            SessionId = Guid.NewGuid().ToString(),
            UserId = admin ? "administrator-principal" : "listener-principal",
            UserName = admin ? "Admin" : "Listener",
            IsAdministrator = admin,
            AllstarrUserId = Guid.NewGuid(),
            BackendType = protocol.ToString(),
            BackendInstanceId = "fixture",
            JellyfinAccessToken = "private-token",
            ExpiresAtUtc = DateTime.Parse("2026-10-29T00:00:00Z").ToUniversalTime()
        };
    }
    private sealed class ProblemReportsTestsClock : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-28T00:00:00Z");
    }
}
