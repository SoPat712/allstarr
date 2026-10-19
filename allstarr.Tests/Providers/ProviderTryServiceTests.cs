using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderTryServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_RecordsTheCapabilityObservationAndExactContract(bool succeeds)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(succeeds ? ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", [Fixture.Track()])) :
                ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(new(ProviderErrorKind.TransientFailure)));
        var result = await fixture.Service.ExecuteAsync(fixture.Actor, "fixture", Search(), "observation", CancellationToken.None);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(succeeds, result.Result!.Succeeded);
        var health = fixture.Status.GetAccountFreeStatus("fixture", "metadata");
        Assert.Equal(succeeds ? ProviderHealthState.Healthy : ProviderHealthState.Degraded, health.Health);
        Assert.NotNull(health.LatencyMilliseconds);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Result));
        var root = json.RootElement;
        Assert.Equal("fixture", root.GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("accountId").ValueKind);
        if (succeeds) Assert.Equal(JsonValueKind.Null, root.GetProperty("message").ValueKind);
        else Assert.Equal(JsonValueKind.String, root.GetProperty("message").ValueKind);
        Assert.False(root.TryGetProperty("albums", out _));
        if (succeeds)
        {
            var track = root.GetProperty("tracks")[0];
            Assert.Equal(JsonValueKind.Null, track.GetProperty("album").ValueKind);
            Assert.Equal(JsonValueKind.Null, track.GetProperty("explicit").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("reasonCode").ValueKind);
        }
    }

    [Fact]
    public async Task Execute_BoundsProviderAndOverallConcurrency()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pending = new TaskCompletionSource<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>())).Returns(pending.Task);
        var first = fixture.Service.ExecuteAsync(fixture.Actor, "fixture", Search(), "first", CancellationToken.None);
        var same = await fixture.Service.ExecuteAsync(fixture.Actor, "fixture", Search(), "same", CancellationToken.None);
        Assert.Equal(429, same.StatusCode);
        var second = fixture.Service.ExecuteAsync(fixture.Actor, "second", Search(), "second", CancellationToken.None);
        var third = fixture.Service.ExecuteAsync(fixture.Actor, "third", Search(), "third", CancellationToken.None);
        Assert.Equal(429, (await fixture.Service.ExecuteAsync(fixture.Actor, "fourth", Search(), "fourth", CancellationToken.None)).StatusCode);
        pending.SetResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", [Fixture.Track()])));
        Assert.All(await Task.WhenAll(first, second, third), item => Assert.Equal(200, item.StatusCode));
        Assert.Equal(200, (await fixture.Service.ExecuteAsync(fixture.Actor, "fixture", Search(), "again", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Execute_TimeoutIsAnObservedProviderOutcome()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pending = new TaskCompletionSource<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>())).Returns(pending.Task);
        var resultTask = fixture.Service.ExecuteAsync(fixture.Actor, "fixture", Search(), "timeout", CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("timeout", result.Result!.ReasonCode);
        Assert.Equal(ProviderHealthState.Degraded, fixture.Status.GetAccountFreeStatus("fixture", "metadata").Health);
        pending.TrySetResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", [])));
    }

    [Fact]
    public async Task Stream_UsesCtsAndProviderOnlyMediaWithRealRangeLength()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns((ProviderExecutionContext context, ProviderStreamLeaseRequest request) =>
            {
                Assert.Equal("track", request.TrackId.Value);
                Assert.False(context.Policy.AllowFallback);
                Assert.Equal(["fixture"], context.Policy.AllowedProviderIds);
                return Task.FromResult(ProviderOutcome<ProviderStreamLease>.Success(new("lease", new Uri("https://media.example.test/file?token=private"),
                    DateTimeOffset.UtcNow.AddMinutes(5), true, true, new("audio/flac", "flac", "flac", 1411000, 44100, 16),
                    ProviderStreamRetryBehavior.DoNotRetry, (message, _) =>
                    {
                        var range = message.Headers.Range;
                        var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                        { Content = new ByteArrayContent(range == null ? new byte[100000] : new byte[65536]) };
                        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
                        response.Headers.AcceptRanges.Add("bytes");
                        if (range != null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 65535, 100000);
                        return Task.FromResult(response);
                    })));
            });
        var result = await fixture.Service.ExecuteAsync(fixture.Actor, "fixture", new()
        { Capability = "streaming", Operation = "stream", TrackId = "track", Quality = "lossless" }, "stream", CancellationToken.None);
        Assert.True(result.Result!.Succeeded);
        var json = JsonSerializer.SerializeToElement(result.Result);
        var media = json.GetProperty("media");
        Assert.Equal("stream", media.GetProperty("kind").GetString());
        Assert.Equal(100000, media.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("byte-range", media.GetProperty("seekMode").GetString());
        Assert.True(media.GetProperty("startMs").GetDouble() >= 0);
        Assert.StartsWith("/api/admin/providers/try/media/", media.GetProperty("url").GetString());
        Assert.DoesNotContain("private", json.GetRawText());
    }

    [Fact]
    public async Task Stream_WithoutByteRangesOpensFromTheStartWithoutARangeHeader()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ranges = new List<RangeHeaderValue?>();
        fixture.Streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns(() => Task.FromResult(ProviderOutcome<ProviderStreamLease>.Success(new("lease", new Uri("https://media.example.test/file"),
                DateTimeOffset.UtcNow.AddMinutes(5), false, false, new("audio/mpeg", "mp3", "mp3", 320000, 44100, null),
                ProviderStreamRetryBehavior.DoNotRetry, (message, _) =>
                {
                    ranges.Add(message.Headers.Range);
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100000]) };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                    return Task.FromResult(response);
                }))));
        var result = await fixture.Service.ExecuteAsync(fixture.Actor, "fixture", new()
        { Capability = "streaming", Operation = "stream", TrackId = "track", Quality = "lossless" }, "stream", CancellationToken.None);
        Assert.True(result.Result!.Succeeded);
        var url = JsonSerializer.SerializeToElement(result.Result).GetProperty("media").GetProperty("url").GetString()!;
        ranges.Clear();
        var entry = fixture.Tickets.Find(fixture.Actor.UserId!.Value, url[(url.LastIndexOf('/') + 1)..]);
        using var opened = await entry!.OpenAsync("bytes=1000-1999", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal([null], ranges);
    }

    [Fact]
    public async Task Download_UsesTransientArtifactAndDeletesWorkspaceAtTicketExpiry()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Download.Setup(item => item.DownloadAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderDownloadRequest>(), null))
            .Returns(async (ProviderExecutionContext context, ProviderDownloadRequest request, IProgress<ProviderDownloadProgress>? _) =>
            {
                Assert.False(context.Policy.AllowFallback);
                var write = await fixture.Artifacts.WriteAsync(new(request.Workspace, request.DurableJobId, "fixture", "song.flac",
                    new MemoryStream([1, 2, 3, 4]), 1024));
                return ProviderOutcome<ProviderDownloadedArtifact>.Success(new(write.ArtifactId, write.Sha256, write.SizeBytes,
                    new("audio/flac", "flac", "flac", 1411000, 44100, 16), true));
            });
        fixture.Metadata.Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(Fixture.Track()));
        var result = await fixture.Service.ExecuteAsync(fixture.Actor, "fixture", new()
        { Capability = "download", Operation = "download", TrackId = "track", Quality = "lossless" }, "download", CancellationToken.None);
        Assert.True(result.Result!.Succeeded);
        var media = JsonSerializer.SerializeToElement(result.Result).GetProperty("media");
        Assert.Equal("Artist - Song.flac", media.GetProperty("fileName").GetString());
        Assert.Equal(4, media.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(1411, media.GetProperty("bitrateKbps").GetDouble());
        Assert.Single(Directory.EnumerateDirectories(fixture.WorkspaceRoot));
        fixture.DeleteTickets();
        Assert.Empty(Directory.EnumerateDirectories(fixture.WorkspaceRoot));
        fixture.Store.Verify(item => item.CreateWorkspaceAsync(It.IsAny<ProviderDownloadWorkspaceEntity>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Store.Verify(item => item.AddVerifiedAsync(It.IsAny<ProviderDownloadArtifactEntity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadTimeout_RetainsQuotaUntilProviderWorkStops()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pending = new TaskCompletionSource<ProviderOutcome<ProviderDownloadedArtifact>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Download.Setup(item => item.DownloadAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderDownloadRequest>(), null))
            .Returns(pending.Task);
        var resultTask = fixture.Service.ExecuteAsync(fixture.Actor, "fixture", new()
        { Capability = "download", Operation = "download", TrackId = "track" }, "timeout-download", CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("timeout", result.Result!.ReasonCode);
        Assert.False(fixture.CanReserveDownload());
        pending.SetResult(ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.Canceled)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!fixture.CanReserveDownload()) await Task.Delay(10, timeout.Token);
        Assert.Empty(Directory.EnumerateDirectories(fixture.WorkspaceRoot));
    }

    [Fact]
    public async Task StreamTimeout_ReturnsAnObservedOutcome()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pending = new TaskCompletionSource<ProviderOutcome<ProviderStreamLease>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns(pending.Task);
        var resultTask = fixture.Service.ExecuteAsync(fixture.Actor, "fixture", new()
        { Capability = "streaming", Operation = "stream", TrackId = "track" }, "timeout-stream", CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("timeout", result.Result!.ReasonCode);
        pending.TrySetResult(ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.Canceled)));
    }

    [Theory]
    [InlineData("download", ProviderErrorKind.NotFound, "not_found")]
    [InlineData("download", ProviderErrorKind.TransientFailure, "transient-failure")]
    [InlineData("streaming", ProviderErrorKind.NotFound, "not_found")]
    [InlineData("streaming", ProviderErrorKind.TransientFailure, "TransientFailure")]
    public async Task MediaFailures_AreProviderOutcomes(string capability, ProviderErrorKind kind, string reason)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Download.Setup(item => item.DownloadAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderDownloadRequest>(), null))
            .ReturnsAsync(ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(kind)));
        fixture.Streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Failure(new(kind)));
        var result = await fixture.Service.ExecuteAsync(fixture.Actor, "fixture", new()
        { Capability = capability, Operation = capability == "download" ? "download" : "stream", TrackId = "track" }, "failure", CancellationToken.None);
        Assert.Equal(200, result.StatusCode);
        Assert.False(result.Result!.Succeeded);
        Assert.Equal(reason, result.Result.ReasonCode);
        Assert.Null(result.Result.Media);
    }

    [Fact]
    public async Task Controller_SignedSessionConflictAndBusyHaveExactBodies()
    {
        await using var fixture = await Fixture.CreateAsync(signed: true);
        var controller = fixture.Controller();
        var conflict = Assert.IsType<ConflictObjectResult>(await controller.Execute("fixture", Search()));
        Assert.Equal("sign_in_needed", JsonSerializer.SerializeToElement(conflict.Value).GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Controller_BusyIncludesRetryAfter()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pending = new TaskCompletionSource<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>())).Returns(pending.Task);
        var first = fixture.Service.ExecuteAsync(fixture.Actor, "fixture", Search(), "occupied", CancellationToken.None);
        var controller = fixture.Controller();
        var busy = Assert.IsType<ObjectResult>(await controller.Execute("fixture", Search()));
        Assert.Equal(429, busy.StatusCode);
        Assert.Equal(5, JsonSerializer.SerializeToElement(busy.Value).GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal("5", controller.Response.Headers.RetryAfter);
        pending.SetResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", [])));
        await first;
    }

    private static ProviderTryRequest Search() => new() { Capability = "metadata", Operation = "search", Query = "sample", Kind = "tracks" };

    private sealed class Fixture : IAsyncDisposable
    {
        public Mock<IProviderMetadataCapability> Metadata { get; } = new();
        public Mock<IProviderStreamingCapability> Streaming { get; } = new();
        public Mock<IProviderDownloadCapability> Download { get; } = new();
        public Mock<IProviderDownloadArtifactStore> Store { get; } = new();
        public ManualTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
        public ProviderTryService Service { get; private set; } = null!;
        public ProviderStatusManager Status { get; private set; } = null!;
        public ProviderDownloadArtifactResolver Artifacts { get; private set; } = null!;
        private readonly string root = Path.Combine(Path.GetTempPath(), "provider-try-tests", Guid.NewGuid().ToString("N"));
        public string WorkspaceRoot => Path.Combine(root, "workspaces");
        public string TicketRoot => Path.Combine(root, "tickets");
        private ProviderTryMediaStore tickets = null!;
        public ProviderTryMediaStore Tickets => tickets;
        private ProviderCtsTrackSelector selector = null!;
        private SqliteTestDatabase database = null!;
        private Factory factory = null!;
        private readonly Mock<IProviderRegistry> registry = new();
        public ProviderActorContext Actor { get; } = new(ProviderActorKind.Administrator, Guid.NewGuid(), new("jellyfin", "backend", "principal"));

        public static async Task<Fixture> CreateAsync(bool signed = false)
        {
            var fixture = new Fixture();
            fixture.database = await SqliteTestDatabase.CreateAsync();
            fixture.factory = new(fixture.database.Options);
            await using (var db = await fixture.factory.CreateDbContextAsync())
            {
                db.Users.Add(new()
                {
                    Id = fixture.Actor.UserId!.Value,
                    Enabled = true,
                    IsAdmin = true,
                    DisplayName = "Administrator",
                    BackendType = "jellyfin",
                    BackendInstanceId = "backend",
                    BackendPrincipalId = "principal"
                });
                await db.SaveChangesAsync();
            }
            ProviderDescriptor Descriptor(string id) => new(id, "Fixture", "Fixture provider", ProviderOrigin.Extension, "1", "1",
                [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, signed ? ProviderAccountRequirement.Optional : ProviderAccountRequirement.None,
                    "1", ["searchTracks", "getTrack"], signed ? [ProviderAccountScope.Shared] : []),
                 new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["getStreamLease"]),
                 new(ProviderCapabilityKind.Download, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["download"])], new())
            { UsesSignedSession = signed };
            fixture.registry.SetupGet(item => item.Providers).Returns([Descriptor("fixture")]);
            fixture.registry.Setup(item => item.GetRequired(It.IsAny<string>())).Returns((string id) => Descriptor(id));
            fixture.registry.Setup(item => item.TryGet(It.IsAny<string>(), out It.Ref<ProviderDescriptor?>.IsAny))
                .Returns((string id, out ProviderDescriptor? descriptor) => { descriptor = Descriptor(id); return true; });
            fixture.registry.Setup(item => item.GetRequiredCapability<IProviderMetadataCapability>(It.IsAny<string>(), ProviderCapabilityKind.Metadata)).Returns(fixture.Metadata.Object);
            fixture.registry.Setup(item => item.GetRequiredCapability<IProviderStreamingCapability>(It.IsAny<string>(), ProviderCapabilityKind.Streaming)).Returns(fixture.Streaming.Object);
            fixture.registry.Setup(item => item.GetRequiredCapability<IProviderDownloadCapability>(It.IsAny<string>(), ProviderCapabilityKind.Download)).Returns(fixture.Download.Object);
            var metadata = fixture.Metadata.Object;
            var streaming = fixture.Streaming.Object;
            fixture.registry.Setup(item => item.TryGetCapability("fixture", ProviderCapabilityKind.Metadata, out metadata)).Returns(true);
            fixture.registry.Setup(item => item.TryGetCapability("fixture", ProviderCapabilityKind.Streaming, out streaming)).Returns(true);
            fixture.Artifacts = new(fixture.Store.Object, new() { RootPath = fixture.WorkspaceRoot, MaximumArtifactBytes = 1024 });
            fixture.tickets = new(temporaryDirectory: fixture.TicketRoot);
            fixture.selector = new(fixture.factory);
            var execution = new ProviderDiagnosticExecutionFactory(fixture.registry.Object, Mock.Of<IProviderRouteAccountResolver>());
            var operations = new ProviderTryOperations(fixture.registry.Object);
            var services = new ServiceCollection().AddSingleton(fixture.registry.Object).BuildServiceProvider();
            fixture.Status = new(new ConfigurationBuilder().Build(), Mock.Of<IHttpClientFactory>(), NullLogger<ProviderStatusManager>.Instance,
                Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()), Options.Create(new DeezerSettings()),
                Options.Create(new QobuzSettings()), services: services);
            var cts = new ProviderCtsDiagnosticRunner(fixture.registry.Object, Mock.Of<IProviderRouteAccountResolver>(), fixture.selector,
                Mock.Of<IProviderOutcomeObserver>(), timeProvider: fixture.Clock);
            fixture.Service = new(fixture.registry.Object, execution, operations, fixture.Status, fixture.tickets, cts,
                fixture.Artifacts, fixture.factory, null!, timeProvider: fixture.Clock);
            return fixture;
        }

        public bool CanReserveDownload()
        {
            using var reservation = tickets.TryReserveDownload();
            return reservation != null;
        }
        public void DeleteTickets() => tickets.Dispose();

        public ProviderTryController Controller()
        {
            var controller = new ProviderTryController(registry.Object, Service, factory, NullLogger<ProviderTryController>.Instance)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
            controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
            {
                SessionId = "session",
                UserId = "principal",
                UserName = "Administrator",
                JellyfinAccessToken = "fixture",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                IsAdministrator = true,
                AllstarrUserId = Actor.UserId
            };
            return controller;
        }
        public static ProviderTrackMetadata Track() => new(new("fixture", ProviderResourceKind.Track, "track"), "Song", [new("Artist")]);
        public async ValueTask DisposeAsync()
        {
            tickets.Dispose();
            selector.Dispose();
            await database.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
