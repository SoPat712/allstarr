using System.Net;
using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Settings;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Favorites;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("star", "json", "query", false)]
    [InlineData("star", "json", "query", true)]
    [InlineData("star", "xml", "form", false)]
    [InlineData("unstar", "json", "json", false)]
    [InlineData("unstar", "xml", "query", false)]
    [InlineData("star", "xml", "form", true)]
    [InlineData("unstar", "json", "form", true)]
    [InlineData("star", "json", "json", true)]
    [InlineData("unstar", "xml", "json", true)]
    public async Task SubsonicFavorites_RecordProviderIdsLocallyAndRelayOnlyNativeIds(
        string operation, string format, string transport, bool mixed)
    {
        var recorded = new List<FavoriteMutationRequest>();
        var pipeline = new Mock<IFavoriteActionPipeline>();
        pipeline.Setup(item => item.RecordAsync(It.IsAny<FavoriteMutationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FavoriteMutationRequest, CancellationToken>((request, _) => recorded.Add(request))
            .ReturnsAsync(new FavoriteEventReceipt(Guid.NewGuid(), Guid.NewGuid(), true, default));
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view")
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            observed.Add(Observe(request));
            return CatalogNative(format, """{"subsonic-response":{"status":"ok","future":true}}""",
                """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" future="true"/>""");
        }, services =>
        {
            services.AddSingleton<IStartupFilter, SonicPrincipalStartupFilter>();
            var policies = new Mock<IEffectiveProviderPolicyResolver>();
            policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                    ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                    ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
                { UserId = user });
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
            services.RemoveAll<IFavoriteActionPipeline>();
            services.AddSingleton(pipeline.Object);
        });
        using var client = factory.CreateClient();
        var path = $"/rest/{operation}.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}";
        var values = "id=ext-deezer-song-1&albumId=ext-deezer-album-2&artistId=ext-deezer-artist-3&id=ext-playlist-deezer-4" +
                     (mixed ? "&id=native-song&albumId=native-album&artistId=native-artist" : "");
        using var request = new HttpRequestMessage(transport == "query" ? HttpMethod.Get : HttpMethod.Post,
            path + (transport == "query" ? "&" + values : ""));
        if (transport == "form") request.Content = new StringContent(values + "&untouched=hello+world", Encoding.UTF8, "application/x-www-form-urlencoded");
        if (transport == "json") request.Content = new StringContent(("""
            {"id":["ext-deezer-song-1","ext-playlist-deezer-4","native-song"],
             "albumId":["ext-deezer-album-2","native-album"],"artistId":["ext-deezer-artist-3","native-artist"],
             "nested":{"array":[true,7,{"keep":"value"}]}}
            """).Replace(",\"native-song\"", mixed ? ",\"native-song\"" : "")
                .Replace(",\"native-album\"", mixed ? ",\"native-album\"" : "")
                .Replace(",\"native-artist\"", mixed ? ",\"native-artist\"" : ""), Encoding.UTF8, "application/json");
        request.Headers.Add("Idempotency-Key", "favorite-fixture");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal("ok", document.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString());
        }
        else Assert.Equal("ok", XDocument.Parse(body).Root!.Attribute("status")?.Value);
        var external = recorded.Where(item => item.ItemId.StartsWith("ext-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "ext-deezer-song-1", "ext-deezer-album-2", "ext-deezer-artist-3", "ext-playlist-deezer-4" }.Order(),
            external.Select(item => item.ItemId).Order());
        Assert.All(external, item =>
        {
            Assert.Equal(operation == "star" ? FavoriteOperation.Favorite : FavoriteOperation.Unfavorite, item.Operation);
            Assert.Equal("favorite-fixture", item.SourceRevision);
            Assert.Equal("fixture", item.ExecutionContext.VerifiedBackendPrincipalId);
            Assert.True(item.ExecutionContext.CanRunUserScopedWork);
        });
        if (!mixed) Assert.Empty(observed);
        else
        {
            var relay = Assert.Single(observed);
            Assert.DoesNotContain("ext-", relay.PathAndQuery + relay.Body, StringComparison.Ordinal);
            var relayedValues = relay.PathAndQuery + relay.Body;
            Assert.Contains("native-song", relayedValues, StringComparison.Ordinal);
            Assert.Contains("native-album", relayedValues, StringComparison.Ordinal);
            Assert.Contains("native-artist", relayedValues, StringComparison.Ordinal);
            if (transport == "json")
            {
                using var relayed = JsonDocument.Parse(relay.Body!);
                Assert.True(relayed.RootElement.GetProperty("nested").GetProperty("array")[0].GetBoolean());
                Assert.Equal(7, relayed.RootElement.GetProperty("nested").GetProperty("array")[1].GetInt32());
            }
        }
    }
    [Theory]
    [InlineData("star", "json")]
    [InlineData("unstar", "xml")]
    public async Task SubsonicFavorites_UnlinkedProviderRequestCannotReachTheBackend(string operation, string format)
    {
        var pipeline = new Mock<IFavoriteActionPipeline>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Subsonic", request =>
            request.RequestUri!.AbsolutePath == "/rest/ping.view"
                ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
                : throw new InvalidOperationException("Unexpected native relay"), services =>
        {
            services.RemoveAll<IFavoriteActionPipeline>();
            services.AddSingleton(pipeline.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/{operation}.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id=ext-deezer-song-1&id=native");
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.GetProperty("subsonic-response");
            Assert.Equal("failed", root.GetProperty("status").GetString());
            Assert.Equal(50, root.GetProperty("error").GetProperty("code").GetInt32());
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            Assert.Equal("failed", root.Attribute("status")?.Value);
            Assert.Equal("50", Assert.Single(root.Elements()).Attribute("code")?.Value);
        }
        pipeline.VerifyNoOtherCalls();
    }

}
