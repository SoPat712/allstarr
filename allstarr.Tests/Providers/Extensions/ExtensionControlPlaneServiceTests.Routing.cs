using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Core.Health;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ExtensionControlPlaneServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedConnections_ListenerRoutesOnlySignedInAudience(bool personal)
    {
        var fixture = await SignedRoutesAsync("tidal-web", personal);
        foreach (var capability in new[] { ProviderCapabilityKind.Metadata, ProviderCapabilityKind.Streaming, ProviderCapabilityKind.Download })
        {
            var resolved = await fixture.Accounts.ResolveAsync(new(fixture.Protocol.RequireActor(), fixture.Signed.ExtensionId,
                capability, null, [ProviderAccountScope.Shared, ProviderAccountScope.Personal]));
            Assert.Equal(fixture.Account.Id, resolved?.Account.AccountId);
            Assert.Equal(fixture.Account.Revision, resolved?.Account.Revision);
            Assert.Null(await fixture.Accounts.ResolveAsync(new(fixture.Protocol.RequireActor(), fixture.Expired.ExtensionId,
                capability, null, [ProviderAccountScope.Shared, ProviderAccountScope.Personal])));
        }
        Assert.Equal(0, fixture.Handler.CatalogCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedConnections_ListenerSearchAndMetadataUseSettingsWithoutASecret(bool personal)
    {
        var fixture = await SignedRoutesAsync("tidal-web", personal);
        var gateway = new ProtocolProviderGateway(fixture.Router, fixture.Registry, fixture.Clients);
        var result = await gateway.SearchAsync(fixture.Protocol, "The Infamous Stringdusters Get Lucky", 40, 0, 0);
        var song = Assert.Single(result.Songs);
        Assert.Equal(fixture.Signed.ExtensionId, song.ExternalProvider);
        Assert.Equal("Get Lucky GB", song.Title);
        Assert.Equal("Get Lucky GB", (await gateway.GetSongAsync(fixture.Protocol, fixture.Signed.ExtensionId, "detail"))?.Title);
        Assert.True(fixture.Handler.CatalogCalls >= 2);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync(item => item.Id == fixture.Account.Id);
            var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(account.SettingsJson)!;
            settings["countryCode"] = JsonSerializer.SerializeToElement("CA");
            account.SettingsJson = JsonSerializer.Serialize(settings);
            account.Revision++;
            await db.SaveChangesAsync();
        }
        var updated = await gateway.SearchAsync(fixture.Protocol, "Get Lucky", 40, 0, 0);
        Assert.Equal("Get Lucky CA", Assert.Single(updated.Songs).Title);
    }

    [Theory]
    [InlineData("amazon", false, true)]
    [InlineData("amazon", true, true)]
    [InlineData("tidal-web", false, true)]
    [InlineData("tidal-web", true, true)]
    [InlineData("tidal-web", false, false)]
    [InlineData("tidal-web", true, false)]
    public async Task SignedConnections_ListenerStreamingPreparesDownloadWithTheSelectedSession(
        string name, bool personal, bool requiresPreparation)
    {
        var fixture = await SignedRoutesAsync(name, personal, requiresPreparation);
        var gateway = new ProtocolProviderGateway(fixture.Router, fixture.Registry, fixture.Clients);
        var stream = await gateway.OpenStreamAsync(fixture.Protocol, fixture.Signed.ExtensionId, "track",
            ProviderAudioQuality.Any, null);
        Assert.NotNull(stream);
        using (stream.Response)
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await stream.Response.Content.ReadAsByteArrayAsync());
        Assert.Empty(Directory.GetDirectories(fixture.Options.RootPath));
        Assert.True(fixture.Handler.CatalogCalls >= (requiresPreparation ? 2 : 1));
    }

    [Theory]
    [InlineData("amazon")]
    [InlineData("tidal-web")]
    public async Task SignedConnections_AutomaticStreamingCheckUsesTheSharedSession(string name)
    {
        var fixture = await SignedRoutesAsync(name, false);
        using var selector = new ProviderCtsTrackSelector(_factory);
        var runner = new ProviderCtsDiagnosticRunner(fixture.Registry, fixture.Accounts, selector, new ProviderRuntimeHealth(),
            sessions: new ProviderCheckSessions(_factory, fixture.Runtime));
        var result = await runner.MeasureAsync(fixture.Protocol.RequireActor(), fixture.Signed.ExtensionId,
            fixture.Account.Id, ProviderAudioQuality.Any, "signed-streaming-check");
        Assert.True(result.Succeeded, result.Stage + ": " + result.Error);
        Assert.Equal("search", result.SelectionMode);
        Assert.Equal(4, result.SampleBytes);
        Assert.Empty(Directory.GetDirectories(fixture.Options.RootPath));
    }

    [Fact]
    public async Task SignedConnections_ExpiredPersonalConnectionFallsBackToSignedInSharedConnection()
    {
        var fixture = await SignedRoutesAsync("tidal-web", false);
        var personal = await AddAccountAsync(fixture.Signed.ExtensionId);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.ProviderAccounts.SingleAsync(item => item.Id == personal.Id)).OwnerUserId = fixture.Protocol.RequireActor().EffectiveUserId;
            await db.SaveChangesAsync();
        }
        fixture.Handler.ExpiresAt = "2020-01-01T00:00:00Z";
        await fixture.Runtime.CompleteSignedSessionGrantAsync(fixture.Signed.Id, fixture.Signed.Revision, "grant-1", accountId: personal.Id);
        Assert.Equal(ExtensionSessionStates.Expired,
            (await fixture.Runtime.SignedSessionStatusAsync(fixture.Signed.Id, accountId: personal.Id)).State);
        var resolved = await fixture.Accounts.ResolveAsync(new(fixture.Protocol.RequireActor(), fixture.Signed.ExtensionId,
            ProviderCapabilityKind.Metadata, null));
        Assert.Equal(fixture.Account.Id, resolved?.Account.AccountId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedConnections_ListenerDownloadUsesTheSelectedAccount(bool personal)
    {
        var fixture = await SignedRoutesAsync("tidal-web", personal);
        var provider = fixture.Signed.ExtensionId;
        var track = new ProviderExternalResourceId(provider, ProviderResourceKind.Track, "track");
        var plan = await fixture.Router.PlanAsync<IProviderDownloadCapability>(new(
            ProviderCapabilityKind.Download, fixture.Protocol.RequireActor(),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, false, true, true, [provider]),
            "download", "fixture", fixture.Protocol.Deadline, [provider],
            [new(provider, availableQualities: [ProviderAudioQuality.Any])], track, "download"));
        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(fixture.Account.Id, candidate.Context.Account?.AccountId);
        var availability = await candidate.Implementation.CheckAvailabilityAsync(candidate.Context, new(track));
        Assert.True(availability.IsSuccess);
        var job = Guid.CreateVersion7();
        var workspace = fixture.Artifacts.CreateTransientWorkspace(new(fixture.Protocol.RequireActor().EffectiveUserId,
            job, provider, fixture.Account.Id, "download"));
        try
        {
            var result = await candidate.Implementation.DownloadAsync(candidate.Context,
                new(track, job, workspace.Reference, ProviderAudioQuality.Any));
            Assert.True(result.IsSuccess, result.Error?.Kind.ToString());
            var artifact = await fixture.Artifacts.ResolveTransientAsync(workspace, result.RequireValue());
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(artifact.SourcePath));
        }
        finally { fixture.Artifacts.DeleteTransientWorkspace(workspace); }
    }

    [Fact]
    public async Task RemovedSignedConnection_CannotBeUsedByAnExistingRoute()
    {
        var fixture = await SignedRoutesAsync("tidal-web", false);
        var provider = fixture.Signed.ExtensionId;
        var track = new ProviderExternalResourceId(provider, ProviderResourceKind.Track, "track");
        var plan = await fixture.Router.PlanAsync<IProviderDownloadCapability>(new(
            ProviderCapabilityKind.Download, fixture.Protocol.RequireActor(),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, false, true, true, [provider]),
            "download", "fixture", fixture.Protocol.Deadline, [provider],
            [new(provider, availableQualities: [ProviderAudioQuality.Any])], track, "download"));
        var candidate = Assert.Single(plan.Candidates);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync(item => item.Id == fixture.Account.Id);
            account.SettingsJson = """{"__removed":true,"__extensionSessionSource":"local"}""";
            await db.SaveChangesAsync();
        }
        var calls = fixture.Handler.CatalogCalls;

        var result = await candidate.Implementation.CheckAvailabilityAsync(candidate.Context, new(track));

        Assert.Equal(ProviderErrorKind.Forbidden, result.Error?.Kind);
        Assert.Equal(calls, fixture.Handler.CatalogCalls);
    }

    private async Task<SignedRouteFixture> SignedRoutesAsync(string signedName, bool personal, bool requiresPreparation = true)
    {
        var listener = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(new UserRecord
            {
                Id = listener,
                Enabled = true,
                DisplayName = "Listener",
                BackendType = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = "listener"
            });
            await db.SaveChangesAsync();
        }
        var registry = new ProviderRegistry([]);
        var handler = new RoutingSessionHandler();
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "route-downloads"), MaximumArtifactBytes = 1024 };
        var artifacts = new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(), options);
        var runtime = new ExtensionRuntimeCoordinator(_factory, _service, registry, registry, clients.Object,
            Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(MockBehavior.Strict),
            artifacts, options,
            _configuration, NullLogger<ExtensionRuntimeCoordinator>.Instance);
        var signed = await RoutingPackageAsync(signedName, requiresPreparation);
        signed = await runtime.ActivateAsync(signed.Id, signed.Revision);
        var view = await runtime.CompleteSignedSessionGrantAsync(signed.Id, signed.Revision, "grant-1");
        Assert.Equal(ExtensionSessionStates.SignedIn, view.State);
        ProviderAccountRecord account;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            account = await db.ProviderAccounts.SingleAsync(item => item.Id == view.AccountId);
            if (personal) account.OwnerUserId = listener;
            var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(account.SettingsJson)!;
            settings["countryCode"] = JsonSerializer.SerializeToElement("GB");
            account.SettingsJson = JsonSerializer.Serialize(settings);
            account.Revision++;
            await db.SaveChangesAsync();
        }
        var expired = await RoutingPackageAsync(signedName == "amazon" ? "tidal-web" : "amazon");
        expired = await runtime.ActivateAsync(expired.Id, expired.Revision);
        handler.ExpiresAt = "2020-01-01T00:00:00Z";
        await runtime.CompleteSignedSessionGrantAsync(expired.Id, expired.Revision, "grant-1");
        Assert.Equal(ExtensionSessionStates.Expired, (await runtime.SignedSessionStatusAsync(expired.Id)).State);
        var other = await AddAccountAsync(expired.ExtensionId);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.ProviderAccounts.SingleAsync(item => item.Id == other.Id)).OwnerUserId = _reviewer;
            await db.SaveChangesAsync();
        }
        handler.ExpiresAt = "2099-01-01T00:00:00Z";
        await runtime.CompleteSignedSessionGrantAsync(expired.Id, expired.Revision, "grant-1", accountId: other.Id);
        var health = new RuntimeProviderRouteHealthSource(new ProviderRuntimeHealth());
        var accounts = new DurableProviderRouteAccountResolver(new ProviderAccountResolver(_factory), health,
            registry, new ProviderCheckSessions(_factory, runtime));
        var router = new ProviderRouter(registry, accounts, health, Mock.Of<IProviderRouteSidecarSource>(), Mock.Of<ITrackIdentityService>());
        var protocol = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener",
            new AllstarrPrincipal(listener, "jellyfin", "fixture", "listener", "Listener", false), "signed-routing",
            DateTimeOffset.UtcNow.AddMinutes(1), default);
        return new(signed, expired, account, accounts, router, registry, clients.Object, handler, protocol, options, artifacts, runtime);
    }

    private async Task<ExtensionPackageRecord> RoutingPackageAsync(string name, bool requiresPreparation = true)
    {
        const string script = """
            var country = '';
            function authorize() {
              var response = session.signedFetch('GET', '/catalog', null, {});
              if (response.status !== 200) throw new Error('session unavailable');
            }
            function track(id) { return {id:id,name:'Get Lucky' + (country ? ' ' + country : ''),artists:'The Infamous Stringdusters'}; }
            registerExtension({
              initialize:function(settings){ country = settings.countryCode || ''; },
              customSearch:function(query,options){ authorize(); return options.filter === 'track' ? [track('track')] : []; },
              getTrack:function(id){ authorize(); return track(id); },
              checkAvailability:function(){
                if (!requiresPreparation) throw new Error('availability must not run');
                authorize(); return {available:true,prepared_context:{ready:true}};
              },
              download:function(id,quality,path,progress,options){
                authorize();
                if (requiresPreparation && !options.preparedContext.ready) throw new Error('preparation missing');
                return file.download('https://api.zarz.moe/audio',path,{});
              }
            });
            """;
        var original = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "allstarr.Tests", "Fixtures", "Extensions", "Registry", name + ".json")))!;
        var features = original["requiredRuntimeFeatures"]!.AsArray();
        if (requiresPreparation && !features.Any(item => item!.GetValue<string>() == "preparedContext@1"))
            features.Add("preparedContext@1");
        var runtimeScript = "var requiresPreparation = " + (requiresPreparation ? "true" : "false") + ";\n" + script;
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(original.ToJsonString(), runtimeScript);
        var manifest = ExtensionSdkV1.ParseManifest(normalized);
        var path = Path.Combine(_root, "extensions", ".staging", name);
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "manifest.json"), normalized);
        await File.WriteAllTextAsync(Path.Combine(path, "index.js"), runtimeScript);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        var package = await _service.StageAsync(new(manifest, hash, 100, 100, 2, path, ExtensionSdkV1.ComputePackageContentSha256(path)));
        return await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
            manifest.Permissions.Select(item => new ExtensionPermissionDecisionInput(item.Kind.ToString().ToLowerInvariant(), item.Value, true)).ToArray());
    }

    private sealed record SignedRouteFixture(ExtensionPackageRecord Signed, ExtensionPackageRecord Expired,
        ProviderAccountRecord Account, DurableProviderRouteAccountResolver Accounts, ProviderRouter Router,
        ProviderRegistry Registry, IHttpClientFactory Clients, RoutingSessionHandler Handler,
        ProtocolExecutionContext Protocol, ProviderDownloadWorkspaceOptions Options, ProviderDownloadArtifactResolver Artifacts,
        ExtensionRuntimeCoordinator Runtime);

    private sealed class RoutingSessionHandler : HttpMessageHandler
    {
        public string ExpiresAt { get; set; } = "2099-01-01T00:00:00Z";
        public int CatalogCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/audio") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([1, 2, 3, 4]), RequestMessage = request });
            var body = path.EndsWith("/bootstrap", StringComparison.Ordinal) ? "{\"challenge_id\":\"challenge-1\"}"
                : path.EndsWith("/session/exchange", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new { session_id = "session-1", session_secret = "secret-1", expires_at = ExpiresAt }) : "{}";
            if (path.EndsWith("/catalog", StringComparison.Ordinal))
            {
                Assert.True(request.Headers.Contains("X-Zarz-Signature"));
                CatalogCalls++;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request });
        }
    }
}
