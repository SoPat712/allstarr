using System.Net;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Core.Health;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ExtensionStreamingDeadlineTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-stream-deadline", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider clock = new(DateTimeOffset.UtcNow);
    private readonly WaitingHandler handler = new();
    private ExtensionDownloadStreamingCapabilityAdapter? downloadAdapter;

    [Theory]
    [InlineData(31, null, false)]
    [InlineData(119, null, false)]
    [InlineData(80, "90", false)]
    [InlineData(31, null, true)]
    public async Task ObservedDownloadStream_OpensAfterTheOperationDeadline(int seconds, string? configuredSeconds, bool segments)
    {
        handler.WaitForAudioBody = true;
        handler.DownloadSegments = segments;
        var (gateway, protocol, provider, options) = await GatewayAsync(configuredSeconds);
        var pending = gateway.OpenStreamAsync(protocol, provider, "track", ProviderAudioQuality.Any, null);
        await Task.WhenAny(handler.Entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(10));
        if (pending.IsCompleted) await pending;
        Assert.True(handler.Entered.Task.IsCompleted);
        Assert.False(handler.Canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(seconds));
        handler.Release.TrySetResult();

        var stream = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(stream);
        Assert.False(handler.Canceled.Task.IsCompleted);
        using (stream.Response)
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await stream.Response.Content.ReadAsByteArrayAsync());
        Assert.Single(Directory.GetDirectories(options.RootPath));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.GetDirectories(options.RootPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedDownloadStream_HonorsItsDeadlineAndCallerCancellation(bool cancelCaller)
    {
        handler.WaitForAudioBody = true;
        var (gateway, protocol, provider, options) = await GatewayAsync("60");
        using var caller = new CancellationTokenSource();
        protocol = new(protocol.Protocol, protocol.BackendInstanceId, protocol.VerifiedBackendPrincipalId,
            protocol.Principal, protocol.CorrelationId, protocol.Deadline, caller.Token);
        var pending = gateway.OpenStreamAsync(protocol, provider, "track", ProviderAudioQuality.Any, null);
        await Task.WhenAny(handler.Entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(10));
        if (pending.IsCompleted) await pending;
        Assert.True(handler.Entered.Task.IsCompleted);
        Assert.False(handler.Canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(handler.Canceled.Task.IsCompleted);
        if (cancelCaller) caller.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        if (cancelCaller)
        {
            Assert.False(handler.Canceled.Task.IsCompleted);
            Assert.Single(Directory.GetDirectories(options.RootPath));
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await downloadAdapter!.DisposeAsync();
        Assert.Empty(Directory.GetDirectories(options.RootPath));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(15, false)]
    [InlineData(1, true)]
    public async Task ObservedDownloadStream_BoundsDescriptorResolutionBeforeMediaHeaders(int seconds, bool artwork)
    {
        handler.WaitForArtworkBody = artwork;
        var (gateway, protocol, provider, options) = await GatewayAsync(resolutionSeconds: seconds);
        var pending = gateway.OpenStreamAsync(protocol, provider, "track", ProviderAudioQuality.Any, null);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(seconds));
        await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(error);
        Assert.IsNotType<TimeoutException>(error);
        await downloadAdapter!.DisposeAsync();
        Assert.Empty(Directory.GetDirectories(options.RootPath));
    }

    [Theory]
    [InlineData("image/jpeg", "cover.jpg", false)]
    [InlineData("application/json", "track.flac", false)]
    [InlineData("audio/flac", "track.audio", true)]
    [InlineData("application/octet-stream", "track.flac", true)]
    public void ResolutionBudget_OnlyAudioHeadersReleaseThePhaseCap(string mime, string target, bool audio)
    {
        using var budget = new ExtensionInvocationBudget(TimeSpan.FromSeconds(150), default, clock, isDownload: true,
            resolutionDeadline: clock.GetUtcNow().AddSeconds(1));
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.ContentType = new(mime);
        var token = budget.Token;
        budget.AcceptMediaResponse(response, target);
        Assert.Equal(token, budget.Token);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(!audio, token.IsCancellationRequested);
        Assert.Equal(0, budget.ResolutionRemainingMilliseconds);
        if (audio)
        {
            budget.Check();
            clock.Advance(TimeSpan.FromSeconds(148));
            Assert.True(token.IsCancellationRequested);
        }
    }

    [Fact]
    public void ExpiredResolutionCannotBeRearmedByLateAudioHeaders()
    {
        using var budget = new ExtensionInvocationBudget(TimeSpan.FromSeconds(150), default, clock, isDownload: true,
            resolutionDeadline: clock.GetUtcNow().AddSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.ContentType = new("audio/flac");
        Assert.ThrowsAny<OperationCanceledException>(() => budget.AcceptMediaResponse(response, "track.flac"));
    }

    [Fact]
    public void FailedAudioHeadersDoNotReleaseResolutionBudget()
    {
        using var budget = new ExtensionInvocationBudget(TimeSpan.FromSeconds(150), default, clock, isDownload: true,
            resolutionDeadline: clock.GetUtcNow().AddSeconds(1));
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.ContentType = new("audio/flac");
        budget.AcceptMediaResponse(response, "track.flac");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(budget.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedMetadataAndSearch_KeepTheOperationDeadline(bool search)
    {
        handler.WaitForCatalog = true;
        var (gateway, protocol, provider, _) = await GatewayAsync();
        Task pending = search
            ? gateway.SearchAsync(protocol, "track", 1, 0, 0, provider)
            : gateway.GetSongAsync(protocol, provider, "track");
        await Task.WhenAny(handler.Entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(10));
        if (pending.IsCompleted) await pending;
        Assert.True(handler.Entered.Task.IsCompleted);
        Assert.False(handler.Canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(handler.Canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.IsNotType<TimeoutException>(error);
        Assert.True(handler.Canceled.Task.IsCompleted);
    }

    [Fact]
    public async Task ObservedDirectStream_StopsAtTheOperationDeadline()
    {
        var (gateway, protocol, provider, _) = await GatewayAsync(direct: true);
        var pending = gateway.OpenStreamAsync(protocol, provider, "track", ProviderAudioQuality.Any, null);
        await Task.WhenAny(handler.Entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(10));
        if (pending.IsCompleted) await pending;
        Assert.True(handler.Entered.Task.IsCompleted);
        Assert.False(handler.Canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(handler.Canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(handler.Canceled.Task.IsCompleted);
    }

    private async Task<(ProtocolProviderGateway Gateway, ProtocolExecutionContext Protocol, string Provider,
        ProviderDownloadWorkspaceOptions Options)> GatewayAsync(string? configuredSeconds = null, bool direct = false, int resolutionSeconds = 15)
    {
        var script = """
            function catalog() {
              var response = session.signedFetch('GET','/catalog',null,{});
              if (response.ok === false) throw new Error('Catalog request failed');
            }
            function track(id) { return {id:id,name:'Track',artists:'Artist'}; }
            registerExtension({
              customSearch:function(){catalog();return [track('track')];},
              getTrack:function(id){catalog();return track(id);},
              checkAvailability:function(){return {available:true};},
              download:function(id,quality,path){
                session.signedFetch('POST','/prepare',{},{});
                return file.download('https://api.zarz.moe/audio',path,{});
              }
            });
            """;
        if (handler.WaitForArtworkBody)
            script = script.Replace("return file.download", "file.download('https://api.zarz.moe/artwork','cover.jpg',{}); return file.download", StringComparison.Ordinal);
        if (handler.DownloadSegments)
            script = script.Replace("return file.download('https://api.zarz.moe/audio',path,{});",
                "return file.downloadSegments(['https://api.zarz.moe/audio'],path);", StringComparison.Ordinal);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln"))) directory = directory.Parent;
        var original = await File.ReadAllTextAsync(Path.Combine(directory!.FullName,
            "allstarr.Tests", "Fixtures", "Extensions", "Registry", "tidal-web.json"));
        var source = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
        source["capabilities"]!["networkTimeoutSeconds"] = 300;
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(source.ToJsonString(), script);
        var manifest = ExtensionSdkV1.ParseManifest(normalized);
        var clients = new Clients(handler);
        var sandbox = new ExtensionSandbox(root, normalized, script, clients, NullLogger.Instance,
            new ExtensionRuntimePermissionSet(new HashSet<string>(["https://api.zarz.moe/"]),
                new HashSet<string>(), new HashSet<string>()), Path.Combine(root, "runtime"),
            new EphemeralDataProtectionProvider().CreateProtector("test"), hookTimeout: TimeSpan.FromMinutes(1), timeProvider: clock,
            executionOptions: new ExtensionExecutionOptions { StreamResolutionSeconds = resolutionSeconds });
        var account = new ProviderAccountContext(Guid.CreateVersion7(), manifest.Id, ProviderAccountScope.Shared, 1);
        sandbox.BindSignedSessionAccounts(account.AccountId, (_, _) => Task.FromResult(true));
        using (sandbox.OpenSignedSessionAccount(account.AccountId))
            Assert.Equal(ExtensionSessionStates.SignedIn, (await sandbox.CompleteSignedSessionGrantAsync("fixture-grant")).State);
        var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = 1024 };
        var artifacts = new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(), options, audioVerifier: FixtureAudioVerifier.Instance);
        var values = new AccountValues();
        IProviderStreamingCapability streaming = downloadAdapter = new ExtensionDownloadStreamingCapabilityAdapter(sandbox, manifest, values, artifacts, options);
        if (direct)
        {
            var capability = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
            capability.SetupGet(item => item.ProviderId).Returns(manifest.Id);
            capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
            capability.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
                .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new("fixture", new Uri("https://api.zarz.moe/audio"),
                    clock.GetUtcNow().AddMinutes(5), false, false, new("audio/flac", "flac", "flac"),
                    ProviderStreamRetryBehavior.DoNotRetry, async (_, token) =>
                    {
                        await handler.WaitAsync(token);
                        return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
                    })));
            streaming = capability.Object;
        }
        var metadata = new ExtensionMetadataCapabilityAdapter(sandbox, manifest, values);
        var descriptor = new ProviderDescriptor(manifest.Id, manifest.DisplayName, "Fixture", ProviderOrigin.Extension, "1", "1.0",
            manifest.Capabilities.Where(item => item.Kind is ProviderCapabilityKind.Metadata or ProviderCapabilityKind.Streaming)
                .Select(item => new ProviderCapabilityDescriptor(item.Kind, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required, "1.0", item.Hooks, [ProviderAccountScope.Shared])),
            new ProviderPermissionDescriptor(), entryPoint: manifest.EntryPoint);
        var registry = new ProviderRegistry([new(descriptor, [streaming, metadata])]);
        var accounts = new Mock<IProviderRouteAccountResolver>();
        accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderRouteAccountResolution(account, 1));
        var router = new ProviderRouter(registry, accounts.Object,
            new RuntimeProviderRouteHealthSource(new ProviderRuntimeHealth()), Mock.Of<IProviderRouteSidecarSource>(),
            Mock.Of<ITrackIdentityService>(), Mock.Of<IProviderOutcomeObserver>());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProtocolExecution:DownloadStreamTimeoutSeconds"] = configuredSeconds ?? "150"
        }).Build();
        var gateway = new ProtocolProviderGateway(router, registry, clients, configuration, timeProvider: clock, audioVerifier: FixtureAudioVerifier.Instance);
        var protocol = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener",
            new AllstarrPrincipal(Guid.CreateVersion7(), "jellyfin", "fixture", "listener", "Listener", false),
            "stream-deadline", clock.GetUtcNow().AddSeconds(30), default);
        return (gateway, protocol, manifest.Id, options);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        handler.Release.TrySetResult();
        if (downloadAdapter != null) await downloadAdapter.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class AccountValues : IProviderAccountSecretAccessor
    {
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation,
            CancellationToken cancellationToken) => operation(Encoding.UTF8.GetBytes("{}"));
    }

    private sealed class Clients(WaitingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public bool WaitForAudioBody { get; set; }
        public bool WaitForArtworkBody { get; set; }
        public bool DownloadSegments { get; set; }
        public bool WaitForCatalog { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => Canceled.TrySetResult());
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult();
                throw;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!WaitForAudioBody && !WaitForArtworkBody && path.EndsWith(WaitForCatalog ? "/catalog" : "/prepare", StringComparison.Ordinal))
                await WaitAsync(cancellationToken);
            if (path is "/audio" or "/artwork")
            {
                HttpContent content = WaitForAudioBody || WaitForArtworkBody
                    ? new WaitingAudio(this, ExtensionInvocationBudget.Current?.Token ?? cancellationToken)
                    : new ByteArrayContent([1, 2, 3, 4]);
                content.Headers.ContentType = new(path == "/audio" ? "audio/flac" : "image/jpeg");
                return new(HttpStatusCode.OK) { Content = content, RequestMessage = request };
            }
            var body = path.EndsWith("/bootstrap", StringComparison.Ordinal) ? """{"challenge_id":"fixture-challenge"}"""
                : path.EndsWith("/exchange", StringComparison.Ordinal)
                    ? """{"session_id":"fixture-session","session_secret":"fixture-secret","expires_at":"2099-01-01T00:00:00Z"}""" : "{}";
            return new(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }

    private sealed class WaitingAudio(WaitingHandler handler, CancellationToken token) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 4; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await handler.WaitAsync(token);
            await stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, token);
        }
    }
}
