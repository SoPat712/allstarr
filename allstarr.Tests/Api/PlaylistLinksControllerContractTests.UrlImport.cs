using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed partial class PlaylistLinksControllerContractTests
{
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
