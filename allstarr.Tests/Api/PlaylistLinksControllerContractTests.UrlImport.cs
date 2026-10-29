using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Moq;
using allstarr.Core.Jobs;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.ListenBrainz;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using System.Net;
using System.Text;

namespace allstarr.Tests;

public sealed partial class PlaylistLinksControllerContractTests
{
    [Fact]
    public async Task FollowNewestCreationUsesTheScopedRouteAndPersistsAStableSubscription()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var user = Guid.NewGuid(); var account = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var clock = Mock.Of<allstarr.Core.Operations.IPlatformClock>(item => item.UtcNow == now);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = user, BackendType = "jellyfin", BackendInstanceId = "backend", BackendPrincipalId = "owner",
                DisplayName = "Owner", Enabled = true, CreatedAt = now, UpdatedAt = now, LastSeenAt = now });
            db.ProviderAccounts.Add(new() { Id = account, ProviderId = "listenbrainz", OwnerUserId = user,
                DisplayName = "ListenBrainz", Enabled = true, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        var adapter = new ListenBrainzPlaylistCapabilityAdapter(new HttpClient(new PlaylistImportHandler()), new PlaylistImportSecrets());
        var registration = ListenBrainzPlaylistCapabilityAdapter.CreateRegistration(adapter);
        var registry = new ProviderRegistry([registration]);
        var wrapped = ObservedProviderCapabilities.Wrap<IProviderPlaylistCapability>(adapter, new allstarr.Core.Health.ProviderRuntimeHealth());
        var router = new Mock<IProviderRouter>();
        router.Setup(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()))
            .Returns<ProviderRouteRequest>(request => Task.FromResult(new ProviderRoutePlan<IProviderPlaylistCapability>(request,
                [new(0, registration.Descriptor, registration.Descriptor.Capabilities.Single(), wrapped,
                    new(request.Actor, "listenbrainz", new(account, "listenbrainz", ProviderAccountScope.Personal, 0, ownerUserId: user),
                        request.Policy, request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken), null)],
                new(request.CorrelationId, request.Capability, "listenbrainz", account, []))));
        PlaylistLinkInput? captured = null;
        var persistence = new Mock<IPlaylistPersistenceService>();
        persistence.Setup(item => item.CreateLinkAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<PlaylistLinkInput>(), It.IsAny<CancellationToken>()))
            .Returns<ProtocolExecutionContext, PlaylistLinkInput, CancellationToken>((_, input, _) =>
            {
                captured = input;
                return Task.FromResult(new PlaylistLinkRecord { Id = Guid.NewGuid(), OwnerUserId = user, ProviderAccountId = account,
                    SourceProviderId = input.SourceProviderId, SourcePlaylistId = input.SourcePlaylistId, CreatedAt = now });
            });
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new JobPayloadPolicy(options), clock);
        var controller = Controller(factory, persistence.Object, clock, new(factory, clock), registry, router.Object, jobs: queue);
        controller.HttpContext.TraceIdentifier = "playlist-import";
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            IsAdministrator = false, SessionId = "session", UserId = "owner", UserName = "Owner", BackendType = "jellyfin", BackendInstanceId = "backend",
            JellyfinAccessToken = "fixture-token", AllstarrUserId = user, ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var response = Assert.IsType<CreatedAtActionResult>(await controller.Create(new(account, "listenbrainz",
            "11111111-1111-4111-8111-111111111111", "jellyfin", "backend", "hybrid", "reconcile", FollowNewest: true), default));
        Assert.Equal("createdfor:weekly-jams", captured!.SourcePlaylistId);
        Assert.True(JsonSerializer.SerializeToElement(response.Value).GetProperty("followNewest").GetBoolean());
        Assert.Equal(account, captured.ProviderAccountId);
    }

    private sealed class PlaylistImportSecrets : IProviderAccountSecretAccessor
    {
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken) =>
            operation(Encoding.UTF8.GetBytes("{\"token\":\"fixture-token\"}"));
    }
    private sealed class PlaylistImportHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("validate-token")
                ? "{\"valid\":true,\"user_name\":\"listener\"}" : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ListenBrainz", "playlist.json"))) });
    }

    [Theory]
    [InlineData("spotify", "linked", true)]
    [InlineData("listenbrainz", "oneTime", true)]
    [InlineData("listenbrainz", "linked", false)]
    public async Task FollowNewestRequiresALinkedListenBrainzAccount(string provider, string importMode, bool account)
    {
        var controller = Controller();
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            IsAdministrator = false, SessionId = "session", UserId = "owner", UserName = "Owner", JellyfinAccessToken = "fixture-token",
            AllstarrUserId = Guid.NewGuid(), ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var response = Assert.IsType<BadRequestObjectResult>(await controller.Create(new(
            account ? Guid.NewGuid() : null, provider, "11111111-1111-4111-8111-111111111111", "jellyfin", "backend",
            "hybrid", "reconcile", ImportMode: importMode, FollowNewest: true), default));
        Assert.Contains("Follow newest", JsonSerializer.Serialize(response.Value));
    }

    [Fact]
    public async Task ReadOnlyPlaylistSourcesRejectDiscoveryWithoutInvokingTheReader()
    {
        var reader = new Mock<IProviderPlaylistCapability>(MockBehavior.Strict);
        reader.SetupGet(item => item.ProviderId).Returns("reader-fixture");
        reader.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Playlist);
        var registry = new ProviderRegistry([new(new("reader-fixture", "Reader", "Fixture", ProviderOrigin.Extension,
            "1", "1", [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["getPlaylistTracks"], supportsPublicRead: true,
                supportsPlaylistDiscovery: false)], new(), entryPoint: "index.js"), [reader.Object])]);
        var controller = Controller(registry: registry);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            IsAdministrator = false,
            JellyfinAccessToken = "fixture-token",
            SessionId = "session",
            UserId = "user",
            UserName = "Listener",
            AllstarrUserId = Guid.CreateVersion7(),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var result = Assert.IsType<ConflictObjectResult>(await controller.BrowsePublicUserPlaylists("reader-fixture", "listener", null));
        Assert.Contains("playlist links only", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        reader.Verify(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()), Times.Never());
    }

    [Theory]
    [InlineData("playlist", 200, null)]
    [InlineData("album", 400, "Albums are not supported yet.")]
    [InlineData("track", 400, "Use a playlist or album link.")]
    [InlineData("user", 400, "No source can read this link.")]
    public async Task PastedLinksRequireASessionAndReturnAPreviewOrAnExplicitResourceError(string kind, int status, string? error)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var capability = new Mock<IProviderPlaylistCapability>();
        capability.SetupGet(item => item.ProviderId).Returns("spotify");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Playlist);
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
                new(new("spotify", ProviderResourceKind.Playlist, "mix"), "Morning mix", new("curator", "A curator"), "1", artwork: new ProviderArtworkReference(publicUri: new Uri("https://images.example.test/cover.png")), trackCount: 42),
                new("spotify", []))));
        var registry = PublicPlaylistTestRegistry.Create("spotify");
        var router = PlaylistUrlResolverTests.Router(registry, capability.Object);
        var controller = Controller(factory, clock: Mock.Of<allstarr.Core.Operations.IPlatformClock>(item => item.UtcNow == DateTimeOffset.UtcNow), registry: registry, router: router.Object);
        var request = new ResolvePlaylistUrlRequest($"https://open.spotify.com/{kind}/mix");
        Assert.IsType<UnauthorizedObjectResult>(await controller.ResolveUrl(request, default));
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            IsAdministrator = false,
            JellyfinAccessToken = "fixture-token",
            BackendInstanceId = "fixture",
            SessionId = "session",
            UserId = "user",
            UserName = "Listener",
            AllstarrUserId = Guid.CreateVersion7(),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow
        };
        var result = Assert.IsAssignableFrom<ObjectResult>(await controller.ResolveUrl(request, default));
        Assert.Equal(status, result.StatusCode);
        var payload = JsonSerializer.Serialize(result.Value);
        if (error != null) Assert.Contains(error, payload, StringComparison.Ordinal);
        else
        {
            using var document = JsonDocument.Parse(payload);
            Assert.Equal("Morning mix", document.RootElement.GetProperty("title").GetString());
            Assert.Equal("A curator", document.RootElement.GetProperty("owner").GetString());
            Assert.Equal(42, document.RootElement.GetProperty("trackCount").GetInt32());
            Assert.Equal("spotify", document.RootElement.GetProperty("source").GetProperty("id").GetString());
            Assert.Equal("mix", document.RootElement.GetProperty("id").GetString());
            Assert.Equal("/api/admin/playlist-sources/public/spotify/playlists/mix/artwork?revision=1",
                document.RootElement.GetProperty("artworkUrl").GetString());
            Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        }
        capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()),
            kind == "playlist" ? Times.Once() : Times.Never());
    }
}
