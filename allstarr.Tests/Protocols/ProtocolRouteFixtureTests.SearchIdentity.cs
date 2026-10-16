using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task Search_SameIsrcProvidersKeepOneFirstSeenIdAcrossDialectsRecreationAndMerge()
    {
        const string firstPublicId = "ext-deezer-first";
        const string isrc = "USRC17607839";
        await using var database = await SqliteTestDatabase.CreateAsync();
        var contexts = new SearchIdentityDbFactory(database.Options);
        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        var clock = new SystemPlatformClock();
        var jellyfinUser = Guid.CreateVersion7();
        var subsonicUser = Guid.CreateVersion7();
        await using (var db = await contexts.CreateDbContextAsync())
        {
            db.Users.AddRange(
                SearchIdentityUser(jellyfinUser, "jellyfin", "user-1"),
                SearchIdentityUser(subsonicUser, "subsonic", "fixture"));
            await db.SaveChangesAsync();
        }

        var identities = new TrackIdentityService(contexts, state, clock);
        var actor = new ProviderActorContext(ProviderActorKind.User, jellyfinUser,
            new ProviderBackendPrincipal("jellyfin", "primary", "user-1"));
        var policy = new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow, true, true, false,
            allowedProviderIds: ["deezer", "qobuz"]);
        ProviderExecutionContext IdentityContext(string provider) => new(actor, provider, null, policy,
            "search-identity", "search-identity", DateTimeOffset.UtcNow.AddMinutes(5), default);

        var first = await identities.RememberSourceAsync(IdentityContext("deezer"),
            new("deezer", ProviderResourceKind.Track, "first"), firstPublicId,
            ProviderIdentityScope.Catalog, isrc);
        Assert.Equal(first, await identities.RememberSourceAsync(IdentityContext("qobuz"),
            new("qobuz", ProviderResourceKind.Track, "second"), "ext-qobuz-song-second",
            ProviderIdentityScope.Catalog, isrc));
        await AssertStoredRecordingAsync(merged: false);

        await AssertBothDialectsAsync();
        var later = await identities.CreateRecordingAsync(actor, "later-recording");
        var merged = await identities.MergeRecordingsAsync(actor, later.Recording.Id, first, "search-merge");
        Assert.Equal(first, merged.Id);
        Assert.Equal(firstPublicId, merged.PublicId);
        await AssertBothDialectsAsync();
        await AssertStoredRecordingAsync(merged: true);

        async Task AssertBothDialectsAsync()
        {
            foreach (var (backend, format, user, principal) in new[]
            {
                ("Jellyfin", "json", jellyfinUser, "user-1"),
                ("Subsonic", "json", subsonicUser, "fixture"),
                ("Subsonic", "xml", subsonicUser, "fixture")
            })
            {
                // Each request uses new identity, gateway and host instances over the same SQLite state.
                var restarted = new TrackIdentityService(contexts, state, clock);
                var metadata = new[] { SearchIdentityMetadata("deezer", "first", "US-RC1-76-07839"),
                    SearchIdentityMetadata("qobuz", "second", "usrc17607839") };
                var registry = new ProviderRegistry(metadata.Select(capability => new ProviderRegistration(
                    new(capability.Object.ProviderId, capability.Object.ProviderId, "Fixture provider",
                        ProviderOrigin.BuiltIn, "1", "1", [
                            new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                                ProviderAccountRequirement.None, "1", ["searchTracks", "getTrack"]),
                            new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                                ProviderAccountRequirement.None, "1", ["getStreamLease"])
                        ], new()),
                    [capability.Object, Mock.Of<IProviderStreamingCapability>(stream =>
                        stream.ProviderId == capability.Object.ProviderId && stream.Capability == ProviderCapabilityKind.Streaming)])));
                var health = new Mock<IProviderRouteHealthSource>(MockBehavior.Strict);
                health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
                    .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
                var router = new ProviderRouter(registry, Mock.Of<IProviderRouteAccountResolver>(MockBehavior.Strict),
                    health.Object, Mock.Of<IProviderRouteSidecarSource>(MockBehavior.Strict), restarted);
                // Prefer the later provider: choosing a serving route must not change the public ID.
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Providers:MetadataOrder"] = "qobuz,deezer",
                    ["Providers:StreamingOrder"] = "qobuz,deezer"
                }).Build();
                var gateway = new ProtocolProviderGateway(router, registry,
                    Mock.Of<IHttpClientFactory>(MockBehavior.Strict), configuration, identities: restarted);
                var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
                policies.Setup(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                        new Dictionary<ProviderCapabilityKind, ImmutableArray<string>>
                        {
                            [ProviderCapabilityKind.Metadata] = ["qobuz", "deezer"],
                            [ProviderCapabilityKind.Streaming] = ["qobuz", "deezer"]
                        }.ToImmutableDictionary(), ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
                    { UserId = user });
                using var host = new ProtocolFactory(backend, request =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    if (path == "/Users/Me") return Json(200, "{\"Id\":\"user-1\"}");
                    if (path.Contains("ping", StringComparison.Ordinal))
                        return Json(200, "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\"}}");
                    if (path == "/Items") return Json(200, "{\"Items\":[],\"TotalRecordCount\":0}");
                    if (path.Contains("search3", StringComparison.Ordinal))
                        return format == "json"
                            ? Json(200, "{\"subsonic-response\":{\"status\":\"ok\",\"searchResult3\":{}}}")
                            : new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(
                                "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><searchResult3 /></subsonic-response>",
                                Encoding.UTF8, "application/xml")
                            };
                    throw new InvalidOperationException("Unexpected search fixture route: " + path);
                }, services =>
                {
                    services.AddSingleton<IStartupFilter>(new ArtworkPrincipalStartupFilter(
                        new AllstarrPrincipal(user, backend.ToLowerInvariant(), "primary", principal, "Listener", false)));
                    services.RemoveAll<IEffectiveProviderPolicyResolver>();
                    services.AddSingleton(policies.Object);
                    services.RemoveAll<IProtocolProviderGateway>();
                    services.AddSingleton<IProtocolProviderGateway>(gateway);
                    services.RemoveAll<MusicSearchService>();
                    services.AddSingleton(new MusicSearchService(gateway));
                }, new Dictionary<string, string?> { [$"{backend}:EnableExternalPlaylists"] = "false" });
                using var client = host.CreateClient();
                using var response = await client.GetAsync(backend == "Jellyfin"
                    ? "/Items?SearchTerm=Song&IncludeItemTypes=Audio&api_key=fixture-key"
                    : $"/rest/search3?u=fixture&p=secret&v=1.16.1&c=fixture&query=Song&songCount=10&albumCount=0&artistCount=0&f={format}");
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.IsSuccessStatusCode, body);
                if (format == "xml")
                {
                    var song = Assert.Single(XDocument.Parse(body).Descendants(), item => item.Name.LocalName == "song");
                    Assert.Equal(firstPublicId, song.Attribute("id")?.Value);
                }
                else
                {
                    using var document = JsonDocument.Parse(body);
                    var songs = backend == "Jellyfin" ? document.RootElement.GetProperty("Items")
                        : document.RootElement.GetProperty("subsonic-response").GetProperty("searchResult3").GetProperty("song");
                    Assert.Equal(firstPublicId, Assert.Single(songs.EnumerateArray())
                        .GetProperty(backend == "Jellyfin" ? "Id" : "id").GetString());
                }
                foreach (var capability in metadata)
                    capability.Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                        It.IsAny<ProviderMetadataSearchRequest>()), Times.Once);
            }
        }

        async Task AssertStoredRecordingAsync(bool merged)
        {
            await using var db = await contexts.CreateDbContextAsync();
            Assert.Equal(merged ? 2 : 1, await db.CanonicalRecordings.CountAsync());
            var recording = Assert.Single(await db.CanonicalRecordings.Where(item => item.MergedIntoId == null).ToListAsync());
            Assert.Equal(first, recording.Id);
            Assert.Equal(firstPublicId, recording.PublicId);
            var routes = await db.ProviderTrackIdentities.ToListAsync();
            Assert.Equal(2, routes.Count);
            Assert.All(routes, route => Assert.Equal(first, route.CanonicalRecordingId));
        }
    }

    private static UserRecord SearchIdentityUser(Guid user, string backend, string principal) => new()
    {
        Id = user,
        BackendType = backend,
        BackendInstanceId = "primary",
        BackendPrincipalId = principal,
        DisplayName = "Listener",
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static Mock<IProviderMetadataCapability> SearchIdentityMetadata(string provider, string externalId, string isrc)
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns(provider);
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new(provider,
                [new(new(provider, ProviderResourceKind.Track, externalId), "Song", [new("Artist")], isrc: isrc)])));
        capability.Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new(provider, [])));
        capability.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new(provider, [])));
        return capability;
    }

    private sealed class SearchIdentityDbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
