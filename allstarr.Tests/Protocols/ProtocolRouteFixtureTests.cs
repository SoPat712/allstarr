using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Models.Search;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using allstarr.Core.Intelligence;
using allstarr.Core.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using SkiaSharp;

namespace allstarr.Tests;

public sealed class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("u=fixture&t=hash&s=salt&v=1.16.1&c=fixture&f=json", "application/json")]
    [InlineData("u=fixture&p=secret&v=1.16.1&c=fixture&f=xml", "application/xml")]
    public async Task SubsonicPing_PostFormCredentialsReachBackend(
        string body,
        string responseContentType)
    {
        ObservedRequest? observed = null;
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            observed = Observe(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("response", Encoding.UTF8, responseContentType)
            };
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/rest/ping.view")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(observed);
        Assert.Equal("POST", observed.Method);
        Assert.Equal("/rest/ping.view", observed.PathAndQuery);
        Assert.Equal(body, observed.Body);
    }

    [Fact]
    public async Task JellyfinAuthentication_PreservesEveryFixtureStatusAndBody()
    {
        using var fixtures = ReadFixture("jellyfin-authentication.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var upstreamStatus = fixture.GetProperty("upstreamStatus").GetInt32();
            var upstreamBody = fixture.GetProperty("upstreamBody").GetRawText();
            var observedRequests = new List<string>();
            using var factory = new ProtocolFactory("Jellyfin", request =>
            {
                observedRequests.Add(request.RequestUri!.AbsolutePath);
                return Json(upstreamStatus, upstreamBody);
            });
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName")
            {
                Content = new StringContent(
                    """{"Username":"fixture","Pw":"wrong"}""",
                    Encoding.UTF8,
                    "application/json")
            };

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(fixture.GetProperty("expectedStatus").GetInt32(), (int)response.StatusCode);
            Assert.Equal(
                JsonDocument.Parse(upstreamBody).RootElement.GetRawText(),
                JsonDocument.Parse(body).RootElement.GetRawText());
            Assert.Equal(["/Users/AuthenticateByName"], observedRequests);
        }
    }

    [Fact]
    public async Task JellyfinAuthBoundary_MissingCredentialsStopsBeforeBackendAndAction()
    {
        var observedRequests = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observedRequests.Add(request.RequestUri!.PathAndQuery);
            return Json(StatusCodes.Status200OK, """{"Id":"unexpected"}""");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/Items?SearchTerm=fixture&IncludeItemTypes=Audio");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(observedRequests);
    }

    [Fact]
    public async Task JellyfinPublicSystemInfo_BypassesCurrentUserVerification()
    {
        var observedRequests = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observedRequests.Add(request.RequestUri!.AbsolutePath);
            return Json(
                StatusCodes.Status200OK,
                """{"ServerName":"Fixture Server","Version":"12.0.0"}""");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/System/Info/Public");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["/System/Info/Public"], observedRequests);
    }

    [Fact]
    public async Task JellyfinAuthenticatedSystemInfo_VerifiesAndRelaysWithoutReshaping()
    {
        const string systemInfo = """{"Id":"server-1","ServerName":"Fixture Server","Version":"12.0.0"}""";
        var observedRequests = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observedRequests.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""")
                : Json(StatusCodes.Status200OK, systemInfo);
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/System/Info?api_key=fixture-key");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["/Users/Me?api_key=fixture-key", "/System/Info?api_key=fixture-key"],
            observedRequests);
        Assert.Equal(
            JsonDocument.Parse(systemInfo).RootElement.GetRawText(),
            JsonDocument.Parse(body).RootElement.GetRawText());
    }

    [Fact]
    public async Task JellyfinLocalArtistDetail_PreservesTheEntireUpstreamObject()
    {
        const string artistId = "00112233445566778899aabbccddeeff";
        const string artist = """
            {
              "Id":"00112233445566778899aabbccddeeff",
              "Name":"Fixture Artist",
              "Type":"MusicArtist",
              "ProviderIds":{"MusicBrainzArtist":"artist-1"},
              "UserData":{"IsFavorite":true,"PlayCount":7},
              "ImageBlurHashes":{"Primary":{"tag":"hash"}},
              "UnknownFutureField":{"Keep":[1,2,3]}
            }
            """;
        var observedRequests = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observedRequests.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1"}""")
                : Json(StatusCodes.Status200OK, artist);
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/Artists/{artistId}?api_key=fixture-key");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(artist), JsonNode.Parse(body)));
        Assert.Equal(2, observedRequests.Count);
        Assert.Equal("/Users/Me?api_key=fixture-key", observedRequests[0]);
        Assert.Equal($"/Items/{artistId}", observedRequests[1]);
    }

    [Fact]
    public async Task JellyfinArtistSearch_PreservesTheEntireLocalArtistObject()
    {
        const string artist = """
            {
              "Id":"00112233445566778899aabbccddeeff",
              "Name":"Fixture Artist",
              "Type":"MusicArtist",
              "ProviderIds":{"MusicBrainzArtist":"artist-1"},
              "UserData":{"IsFavorite":true,"PlayCount":7},
              "UnknownFutureField":{"Keep":[1,2,3]}
            }
            """;
        using var factory = new ProtocolFactory("Jellyfin", request =>
            request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1"}""")
                : Json(StatusCodes.Status200OK, $$"""{"Items":[{{artist}}],"TotalRecordCount":1,"StartIndex":0}"""));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Artists?SearchTerm=Fixture%20Artist&Limit=10&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(artist),
            JsonNode.Parse(body.RootElement.GetProperty("Items")[0].GetRawText())));
    }

    [Fact]
    public async Task JellyfinAuthenticatedSystemInfo_FallsBackForUnboundApiKeyOnPreTwelveServers()
    {
        var observedRequests = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observedRequests.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath switch
            {
                "/Users/Me" => Json(StatusCodes.Status400BadRequest, """{"error":"API key has no current user"}"""),
                "/Users/user-1" => Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}"""),
                _ => Json(StatusCodes.Status200OK, """{"Id":"server-1","Version":"10.11.11"}""")
            };
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/System/Info");
        request.Headers.TryAddWithoutValidation(
            "X-Emby-Authorization",
            """MediaBrowser Client="Fixture", Device="Tests", DeviceId="test-1", Version="1", UserId="user-1", Token="fixture-token" """);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["/Users/Me", "/Users/user-1", "/System/Info"], observedRequests);
    }

    [Fact]
    public async Task JellyfinApiKeyFallback_DoesNotBindDeclaredUserToProviderActor()
    {
        var interaction = new RecordingInteractionAdapter();
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath switch
            {
                "/Users/Me" => Json(StatusCodes.Status400BadRequest, """{"error":"API key has no current user"}"""),
                "/Users/user-1" => Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}"""),
                _ => throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}")
            },
            services =>
            {
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
                services.RemoveAll<IJellyfinInteractionProtocolAdapter>();
                services.AddSingleton<IJellyfinInteractionProtocolAdapter>(interaction);
            });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/Items/ext-deezer-song-1/InstantMix");
        request.Headers.TryAddWithoutValidation(
            "X-Emby-Authorization",
            """MediaBrowser Client="Fixture", Device="Tests", DeviceId="test-1", Version="1", UserId="user-1", Token="fixture-token" """);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(interaction.LastContext);
        Assert.Equal("user-1", interaction.LastContext!.VerifiedBackendPrincipalId);
        Assert.Null(interaction.LastContext.Actor);
        metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JellyfinAuthorizationHeader_UsesUsersMeInsteadOfDeclaredVictim()
    {
        var interaction = new RecordingInteractionAdapter();
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"attacker","Name":"Current User"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IJellyfinInteractionProtocolAdapter>();
                services.AddSingleton<IJellyfinInteractionProtocolAdapter>(interaction);
            });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/Items/ext-deezer-song-1/InstantMix");
        request.Headers.TryAddWithoutValidation(
            "X-Emby-Authorization",
            """MediaBrowser Client="Fixture", Device="Tests", DeviceId="test-1", Version="1", UserId="victim", Token="fixture-token" """);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attacker", interaction.LastContext?.VerifiedBackendPrincipalId);
        Assert.NotEqual("victim", interaction.LastContext?.VerifiedBackendPrincipalId);
    }

    [Theory]
    [InlineData("/Search/Hints?SearchTerm=fixture&Limit=2&api_key=fixture-key")]
    [InlineData("/Users/user-1/Search/Hints?SearchTerm=fixture&Limit=2&api_key=fixture-key")]
    public async Task JellyfinSearchHints_AppliesOneGlobalLimitAfterMerging(string path)
    {
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        metadata.Setup(service => service.SearchAllAsync(
                "fixture", 2, 2, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult
            {
                Songs = [new Song { Id = "external-song", Title = "Song", Artist = "Artist" }],
                Albums = [new Album { Id = "external-album", Title = "Album", Artist = "Artist" }],
                Artists = [new Artist { Id = "external-artist", Name = "Artist" }]
            });
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1"}""")
                : Json(StatusCodes.Status200OK,
                    """{"SearchHints":[{"Id":"native-1","Type":"Audio"},{"Id":"native-2","Type":"Audio"}],"TotalRecordCount":2}"""),
            services =>
            {
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
                services.RemoveAll<IProtocolProviderGateway>();
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body.RootElement.GetProperty("SearchHints").GetArrayLength());
        Assert.Equal(2, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        metadata.VerifyAll();
    }

    [Fact]
    public async Task JellyfinAuthBoundary_RejectsBeforeSearchCacheProviderOrExternalActions()
    {
        using var fixtures = ReadFixture("jellyfin-auth-boundary.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var verification = fixture.GetProperty("verification");
            var expected = fixture.GetProperty("expected");
            var observedRequests = new List<string>();
            var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
            var downloads = new Mock<IDownloadService>(MockBehavior.Strict);

            using var factory = new ProtocolFactory(
                "Jellyfin",
                request =>
                {
                    observedRequests.Add(request.RequestUri!.PathAndQuery);
                    return Json(
                        verification.GetProperty("status").GetInt32(),
                        verification.GetProperty("body").GetRawText());
                },
                services =>
                {
                    services.RemoveAll<IMusicMetadataService>();
                    services.AddSingleton(metadata.Object);
                    services.RemoveAll<IDownloadService>();
                    services.AddSingleton(downloads.Object);
                });
            using var client = factory.CreateClient();
            var requestFixture = fixture.GetProperty("request");
            using var request = new HttpRequestMessage(
                new HttpMethod(requestFixture.GetProperty("method").GetString()!),
                requestFixture.GetProperty("path").GetString());
            if (requestFixture.TryGetProperty("header", out var header))
            {
                request.Headers.TryAddWithoutValidation(
                    header.GetString()!,
                    requestFixture.GetProperty("credential").GetString());
            }

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(expected.GetProperty("status").GetInt32(), (int)response.StatusCode);
            Assert.Equal(
                expected.GetProperty("body").GetRawText(),
                JsonDocument.Parse(body).RootElement.GetRawText());
            Assert.Equal(
                [verification.GetProperty("pathAndQuery").GetString()!],
                observedRequests);
            metadata.VerifyNoOtherCalls();
            downloads.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task JellyfinAuthBoundary_AllowsVerifiedClientAndForwardsOnlyCredentialQuery()
    {
        var observedRequests = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observedRequests.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath.Equals("/Users/Me", StringComparison.Ordinal)
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""")
                : Json(StatusCodes.Status200OK, """{"Items":[],"TotalRecordCount":0,"StartIndex":0}""");
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/Items?IncludeItemTypes=Audio&api_key=fixture-valid-key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            [
                "/Users/Me?api_key=fixture-valid-key",
                "/Items?IncludeItemTypes=Audio&api_key=fixture-valid-key&Fields=MediaSources"
            ],
            observedRequests);
    }

    [Fact]
    public async Task JellyfinApiKey_UserPathFallsBackBeforeNativeRelay()
    {
        var observedRequests = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observedRequests.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status400BadRequest, """{"error":"API key has no current user"}""")
                : Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User","Policy":{"IsDisabled":false}}""");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/Users/user-1?api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            [
                "/Users/Me?api_key=fixture-key",
                "/Users/user-1?api_key=fixture-key",
                "/Users/user-1?api_key=fixture-key"
            ],
            observedRequests);
    }

    [Fact]
    public async Task JellyfinStaticMusicRoute_BeatsParameterizedItemRoute()
    {
        var observedRequests = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observedRequests.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""")
                : Json(StatusCodes.Status200OK, """[{"Id":"album-1","Type":"MusicAlbum"}]""");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/Items/Latest?api_key=fixture-key&Limit=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, observedRequests.Count);
        Assert.StartsWith("/Items/Latest?", observedRequests[1], StringComparison.Ordinal);
        Assert.Contains("IncludeItemTypes=", observedRequests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task JellyfinMusicRoot_UsesConfiguredUserScope()
    {
        var observedRequests = new List<string>();
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request =>
            {
                observedRequests.Add(request.RequestUri!.PathAndQuery);
                return request.RequestUri.AbsolutePath == "/Users/Me"
                    ? Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""")
                    : ItemLookup("""{"Id":"music-1","Type":"CollectionFolder","CollectionType":"music"}""");
            },
            configuration: new Dictionary<string, string?>
            {
                ["Jellyfin:LibraryId"] = "music-1",
                ["Jellyfin:UserId"] = "user-1"
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/Items/Root?api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["/Users/Me?api_key=fixture-key", "/Items?ids=music-1&limit=1&userId=user-1"],
            observedRequests);
    }

    [Fact]
    public async Task JellyfinSearchAdapter_PreservesFixtureStatusBodyAndPaging()
    {
        using var fixture = ReadFixture("jellyfin-search-shaping.json");
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        metadata
            .Setup(service => service.SearchAllAsync(
                "fixture",
                20,
                0,
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult());
        metadata
            .Setup(service => service.SearchPlaylistsAsync(
                "fixture",
                20,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath.Equals("/Users/Me", StringComparison.Ordinal)
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""")
                : Json(StatusCodes.Status200OK, fixture.RootElement.GetProperty("upstream").GetProperty("body").GetRawText()),
            services =>
            {
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            fixture.RootElement.GetProperty("request").GetProperty("path").GetString());
        var body = await response.Content.ReadAsStringAsync();
        var expected = fixture.RootElement.GetProperty("expected");

        Assert.Equal(expected.GetProperty("status").GetInt32(), (int)response.StatusCode);
        Assert.Equal(
            expected.GetProperty("contentType").GetString(),
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            CanonicalJson(expected.GetProperty("body")),
            CanonicalJson(JsonDocument.Parse(body).RootElement));
        metadata.Verify(service => service.SearchAllAsync(
            "fixture",
            20,
            0,
            0,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JellyfinItemAndImageAdapters_PreserveExternalShapingPlaceholderAndConditionalResponse()
    {
        using var fixture = ReadFixture("jellyfin-item-image-shaping.json");
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        metadata.Setup(service => service.GetSongAsync("deezer", "42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Song
            {
                Id = "ext-deezer-song-42",
                ExternalId = "42",
                ExternalProvider = "deezer",
                IsLocal = false,
                Title = "Fixture Song",
                Artist = "Fixture Artist",
                Album = "Fixture Album",
                Duration = 123
            });
        metadata.Setup(service => service.GetSongAsync("deezer", "no-art", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Song
            {
                Id = "ext-deezer-song-no-art",
                ExternalId = "no-art",
                ExternalProvider = "deezer",
                IsLocal = false,
                Title = "No Art"
            });

        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath.Equals("/Users/Me", StringComparison.Ordinal)
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
            });
        using var client = factory.CreateClient();

        var itemFixture = fixture.RootElement.GetProperty("item");
        using var itemResponse = await client.GetAsync(itemFixture.GetProperty("requestPath").GetString());
        using var itemBody = JsonDocument.Parse(await itemResponse.Content.ReadAsStringAsync());
        var expectedItem = itemFixture.GetProperty("expected");
        Assert.Equal(expectedItem.GetProperty("status").GetInt32(), (int)itemResponse.StatusCode);
        Assert.Equal(expectedItem.GetProperty("contentType").GetString(), itemResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedItem.GetProperty("id").GetString(), itemBody.RootElement.GetProperty("Id").GetString());
        Assert.Equal(expectedItem.GetProperty("name").GetString(), itemBody.RootElement.GetProperty("Name").GetString());
        Assert.Equal(expectedItem.GetProperty("type").GetString(), itemBody.RootElement.GetProperty("Type").GetString());
        Assert.Equal(expectedItem.GetProperty("providerId").GetString(),
            itemBody.RootElement.GetProperty("ProviderIds").GetProperty("deezer").GetString());

        var imageFixture = fixture.RootElement.GetProperty("image");
        using var imageResponse = await client.GetAsync(imageFixture.GetProperty("requestPath").GetString());
        var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync();
        var expectedImage = imageFixture.GetProperty("expected");
        Assert.Equal(expectedImage.GetProperty("status").GetInt32(), (int)imageResponse.StatusCode);
        Assert.Equal(expectedImage.GetProperty("contentType").GetString(), imageResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedImage.GetProperty("length").GetInt32(), imageBytes.Length);
        Assert.NotNull(imageResponse.Headers.ETag);

        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, imageFixture.GetProperty("requestPath").GetString());
        conditionalRequest.Headers.IfNoneMatch.Add(imageResponse.Headers.ETag);
        using var conditionalResponse = await client.SendAsync(conditionalRequest);
        Assert.Equal(expectedImage.GetProperty("conditionalStatus").GetInt32(), (int)conditionalResponse.StatusCode);
        Assert.Empty(await conditionalResponse.Content.ReadAsByteArrayAsync());

        using var headRequest = new HttpRequestMessage(
            HttpMethod.Head,
            imageFixture.GetProperty("requestPath").GetString());
        using var headResponse = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Equal(expectedImage.GetProperty("contentType").GetString(),
            headResponse.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync());

        using var pathImageResponse = await client.GetAsync(
            "/Items/ext-deezer-song-no-art/Images/Primary/0/no-art/png/300/300/0/0?api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, pathImageResponse.StatusCode);
        Assert.Equal(
            expectedImage.GetProperty("length").GetInt32(),
            (await pathImageResponse.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task JellyfinExternalAlbumImage_ParsesHyphenatedProviderAndReturnsPositiveArtwork()
    {
        var artworkBytes = new byte[] { 10, 20, 30, 40 };
        var observedPaths = new List<string>();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetAlbumAsync(
                It.Is<ProtocolExecutionContext>(context => context.Protocol == ProtocolKind.Jellyfin),
                "apple-musickit",
                "i.album"))
            .ReturnsAsync(new Album
            {
                Id = "ext-apple-musickit-album-i.album",
                ExternalProvider = "apple-musickit",
                ExternalId = "i.album",
                Title = "Library Album",
                Artist = "Library Artist",
                CoverArtUrl = "https://is1-ssl.mzstatic.com/image/thumb/library/1024x1024bb.jpg",
                IsLocal = false
            });
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request =>
            {
                observedPaths.Add(request.RequestUri!.PathAndQuery);
                if (request.RequestUri.AbsolutePath == "/Users/Me")
                    return Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""");
                if (request.RequestUri.Host == "is1-ssl.mzstatic.com")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(artworkBytes)
                        {
                            Headers = { ContentType = new("image/jpeg") }
                        }
                    };
                }
                throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
            },
            services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Items/ext-apple-musickit-album-i.album/Images/Primary?api_key=fixture-key&tag=revision-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(artworkBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.NotNull(response.Headers.ETag);
        Assert.Contains(observedPaths, path => path == "/Users/Me?api_key=fixture-key");
        Assert.Contains(observedPaths, path => path == "/image/thumb/library/1024x1024bb.jpg");
        gateway.VerifyAll();
    }

    [Fact]
    public async Task JellyfinSynthesizedLongImageRoute_HonorsSizeAndFormat()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            8, 4, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.Red);
        using var sourceImage = SKImage.FromBitmap(bitmap);
        using var sourceData = sourceImage.Encode(SKEncodedImageFormat.Png, 100);
        var sourceBytes = sourceData.ToArray();
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        metadata.Setup(service => service.GetAlbumAsync(
                "deezer", "42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Album
            {
                Id = "ext-deezer-album-42",
                ExternalProvider = "deezer",
                ExternalId = "42",
                Title = "Fixture Album",
                CoverArtUrl = "https://images.example.test/cover.png"
            });
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath switch
            {
                "/Users/Me" => Json(StatusCodes.Status200OK, """{"Id":"user-1"}"""),
                "/cover.png" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(sourceBytes)
                    {
                        Headers = { ContentType = new("image/png") }
                    }
                },
                _ => throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}")
            },
            services =>
            {
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
                services.RemoveAll<IProtocolProviderGateway>();
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Items/ext-deezer-album-42/Images/Primary/0/revision/jpg/2/2/0/0?api_key=fixture-key");
        var resultBytes = await response.Content.ReadAsByteArrayAsync();
        using var resultBitmap = SKBitmap.Decode(resultBytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(resultBitmap);
        Assert.Equal(2, resultBitmap.Width);
        Assert.Equal(1, resultBitmap.Height);
        metadata.VerifyAll();
    }

    [Fact]
    public async Task JellyfinExternalSongImage_WithoutPlayerToken_UsesMetadataFallback()
    {
        var artworkBytes = new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 };
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        metadata.Setup(service => service.GetSongAsync(
                "applemusic",
                "6768469976",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Song
            {
                Id = "ext-applemusic-song-6768469976",
                ExternalProvider = "applemusic",
                ExternalId = "6768469976",
                Title = "Artwork Track",
                CoverArtUrl = "https://is1-ssl.mzstatic.com/image/thumb/song/1024x1024bb.jpg",
                IsLocal = false
            });
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.Host == "is1-ssl.mzstatic.com"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(artworkBytes)
                    {
                        Headers = { ContentType = new("image/jpeg") }
                    }
                }
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Items/ext-applemusic-song-6768469976/Images/Primary?fillHeight=600&fillWidth=600");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(artworkBytes, await response.Content.ReadAsByteArrayAsync());
        metadata.VerifyAll();
    }

    [Fact]
    public async Task JellyfinVirtualPlaylistImage_WithoutPlayerToken_UsesPublicArtworkSource()
    {
        const string virtualId = "allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0";
        var artworkBytes = new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 };
        var virtualization = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        virtualization.Setup(service => service.ResolvePublicArtworkSourceAsync(
                virtualId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualPlaylistArtworkSource("deezer", "source-list"));
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        metadata.Setup(service => service.GetPlaylistAsync(
                "deezer",
                "source-list",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalPlaylist
            {
                Id = "ext-deezer-playlist-source-list",
                ExternalId = "source-list",
                Provider = "deezer",
                Name = "Source",
                CoverUrl = "https://fixture-cdn.example/playlist.jpg"
            });
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.Host == "fixture-cdn.example"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(artworkBytes)
                    {
                        Headers = { ContentType = new("image/jpeg") }
                    }
                }
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IPlaylistVirtualizationService>();
                services.AddSingleton(virtualization.Object);
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/Items/{virtualId}/Images/Primary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(artworkBytes, await response.Content.ReadAsByteArrayAsync());
        virtualization.VerifyAll();
        metadata.VerifyAll();
    }

    [Fact]
    public async Task JellyfinTargetPlaylistImage_RelaysNativeTargetWithoutPlayerToken()
    {
        const string virtualId = "allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0";
        var artworkBytes = new byte[] { 0xFF, 0xD8, 0x03, 0x04, 0xFF, 0xD9 };
        var virtualization = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        virtualization.Setup(service => service.ResolvePublicArtworkSourceAsync(
                virtualId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualPlaylistArtworkSource(
                "spotify", "source-list", "native-target"));
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath == "/Items/native-target/Images/Primary"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(artworkBytes)
                    {
                        Headers = { ContentType = new("image/jpeg") }
                    }
                }
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IPlaylistVirtualizationService>();
                services.AddSingleton(virtualization.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/Items/{virtualId}/Images/Primary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(artworkBytes, await response.Content.ReadAsByteArrayAsync());
        virtualization.VerifyAll();
    }

    [Fact]
    public async Task JellyfinApplePlaybackInfo_AdvertisesImmediateFlacStream()
    {
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        metadata.Setup(service => service.GetSongAsync(
                "applemusic",
                "6768469976",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Song
            {
                Id = "ext-applemusic-song-6768469976",
                ExternalProvider = "applemusic",
                ExternalId = "6768469976",
                Title = "Playback Track",
                Artist = "Fixture Artist",
                Album = "Fixture Album",
                Duration = 180,
                IsLocal = false
            });
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IMusicMetadataService>();
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(metadata.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Items/ext-applemusic-song-6768469976/PlaybackInfo?api_key=fixture-key");
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, responseBody);
        using var document = JsonDocument.Parse(responseBody);
        var source = document.RootElement.GetProperty("MediaSources")[0];

        Assert.Equal("flac", source.GetProperty("Container").GetString());
        Assert.Equal(
            "/Audio/ext-applemusic-song-6768469976/stream?static=true",
            source.GetProperty("DirectStreamUrl").GetString());
        Assert.False(source.GetProperty("RequiresOpening").GetBoolean());
        metadata.VerifyAll();
    }

    [Fact]
    public async Task JellyfinLocalImage_ForwardsPlayerTokenAndReturnsArtwork()
    {
        var artworkBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var playerTokenReachedVerification = false;
        var playerTokenReachedArtwork = false;
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var hasPlayerToken = request.Headers.TryGetValues("X-Emby-Token", out var values) &&
                                 values.Contains("fixture-player-token", StringComparer.Ordinal);
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
            {
                playerTokenReachedVerification = hasPlayerToken;
                return Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""");
            }

            if (request.RequestUri.AbsolutePath == "/Items/local-song/Images/Primary")
            {
                playerTokenReachedArtwork = hasPlayerToken;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(artworkBytes)
                    {
                        Headers = { ContentType = new("image/jpeg") }
                    }
                };
            }

            if (IsItemLookup(request, "local-song"))
            {
                return ItemLookup("""{"Id":"local-song","Type":"Audio"}""");
            }

            throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/Items/local-song/Images/Primary?maxWidth=300&maxHeight=300&tag=art-v1");
        request.Headers.TryAddWithoutValidation("X-Emby-Token", "fixture-player-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(artworkBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.True(playerTokenReachedVerification);
        Assert.True(playerTokenReachedArtwork);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task JellyfinNativeLongImageRelay_PreservesRangeValidatorsAndDecimalPath(string method)
    {
        const string imagePath =
            "/Items/local-song/Images/Primary/0/art-v1/jpg/300/300/12.5/0?quality=90";
        var observed = new List<(string Method, string PathAndQuery, string? Range, string? IfRange)>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observed.Add((
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.TryGetValues("Range", out var ranges) ? ranges.Single() : null,
                request.Headers.TryGetValues("If-Range", out var validators) ? validators.Single() : null));
            if (IsItemLookup(request, "local-song"))
                return ItemLookup("""{"Id":"local-song","Type":"Audio"}""");
            if (request.RequestUri.AbsolutePath == "/Users/Me")
                return Json(StatusCodes.Status400BadRequest, """{"error":"API key has no current user"}""");
            if (request.RequestUri.AbsolutePath == "/Users/user-1")
                return Json(StatusCodes.Status200OK, """{"Id":"user-1"}""");

            Assert.Equal(imagePath, request.RequestUri.PathAndQuery);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([8, 9, 10, 11])
            };
            response.Content.Headers.ContentType = new("image/jpeg");
            response.Content.Headers.ContentRange = new(8, 11, 32);
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.ETag = new("\"art-v1\"");
            return response;
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), imagePath);
        request.Headers.TryAddWithoutValidation(
            "X-Emby-Authorization",
            """MediaBrowser Client="Fixture", Device="Tests", DeviceId="test-1", Version="1", UserId="user-1", Token="fixture-token" """);
        request.Headers.TryAddWithoutValidation("Range", "bytes=8-11");
        request.Headers.TryAddWithoutValidation("If-Range", "\"art-v1\"");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes 8-11/32", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal("\"art-v1\"", response.Headers.ETag?.Tag);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.Equal(method == "HEAD" ? Array.Empty<byte>() : new byte[] { 8, 9, 10, 11 },
            await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(["/Items?ids=local-song&limit=1", "/Users/Me", "/Users/user-1", imagePath],
            observed.Select(item => item.PathAndQuery));
        Assert.Equal(method, observed[^1].Method);
        Assert.Equal("bytes=8-11", observed[^1].Range);
        Assert.Equal("\"art-v1\"", observed[^1].IfRange);
    }

    [Fact]
    public async Task JellyfinLyrics_PreservesLocalFirstFallbackAndNotFoundFixtures()
    {
        using var fixtures = ReadFixture("jellyfin-lyrics.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var observedPaths = new List<string>();
            var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
            if (fixture.TryGetProperty("external", out var external) && external.GetBoolean())
            {
                metadata.Setup(service => service.GetSongAsync("deezer", "missing", It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Song?)null);
            }

            using var factory = new ProtocolFactory(
                "Jellyfin",
                request =>
                {
                    observedPaths.Add(request.RequestUri!.AbsolutePath);
                    if (request.RequestUri.AbsolutePath.Equals("/Users/Me", StringComparison.Ordinal))
                    {
                        return Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""");
                    }

                    if (request.RequestUri.AbsolutePath.StartsWith("/Audio/", StringComparison.Ordinal) &&
                        request.RequestUri.AbsolutePath.EndsWith("/Lyrics", StringComparison.Ordinal))
                    {
                        return Json(
                            fixture.GetProperty("upstreamLyricsStatus").GetInt32(),
                            fixture.GetProperty("upstreamLyricsBody").GetRawText());
                    }

                    if (request.RequestUri.AbsolutePath == "/Items" &&
                        fixture.TryGetProperty("itemBody", out var itemBody))
                    {
                        return ItemLookup(itemBody.GetRawText());
                    }

                    if (request.RequestUri.AbsolutePath == "/Items")
                    {
                        var id = QueryHelpers.ParseQuery(request.RequestUri.Query)["ids"].ToString();
                        return ItemLookup($$"""{"Id":"{{id}}","Type":"Audio"}""");
                    }

                    throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
                },
                services =>
                {
                    services.RemoveAll<IMusicMetadataService>();
                    services.AddSingleton(metadata.Object);
                });
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(fixture.GetProperty("requestPath").GetString());
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(fixture.GetProperty("expectedStatus").GetInt32(), (int)response.StatusCode);
            if (fixture.TryGetProperty("expectedBodyKind", out var expectedBodyKind) &&
                expectedBodyKind.GetString() == "upstream")
            {
                Assert.Equal(
                    CanonicalJson(fixture.GetProperty("upstreamLyricsBody")),
                    CanonicalJson(JsonDocument.Parse(body).RootElement));
                Assert.Equal(
                    ["/Items", "/Users/Me", "/Audio/local-song/Lyrics"],
                    observedPaths);
            }
            else
            {
                Assert.Equal(
                    CanonicalJson(fixture.GetProperty("expectedBody")),
                    CanonicalJson(JsonDocument.Parse(body).RootElement));
            }

            metadata.VerifyAll();
        }
    }

    [Fact]
    public async Task JellyfinFavorite_PreservesBackendResultAndRejectsRouteUserAuthorizationForOptionalWork()
    {
        using var fixture = ReadFixture("jellyfin-favorite-playback.json");
        var observedPaths = new List<string>();
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        var interaction = new RecordingInteractionAdapter();
        var local = fixture.RootElement.GetProperty("favorite").GetProperty("local");
        var upstream = local.GetProperty("upstream");

        using var factory = new ProtocolFactory(
            "Jellyfin",
            request =>
            {
                observedPaths.Add(request.RequestUri!.PathAndQuery);
                if (request.RequestUri.AbsolutePath.StartsWith("/Users/", StringComparison.Ordinal))
                {
                    return Json(StatusCodes.Status200OK, """{"Id":"verified-user","Name":"Fixture User"}""");
                }

                if (IsItemLookup(request, "local-song"))
                {
                    return ItemLookup("""{"Id":"local-song","Type":"Audio"}""");
                }

                Assert.Equal(upstream.GetProperty("pathAndQuery").GetString(), request.RequestUri.PathAndQuery);
                return Json(upstream.GetProperty("status").GetInt32(), upstream.GetProperty("body").GetRawText());
            },
            services =>
            {
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
                services.RemoveAll<IJellyfinInteractionProtocolAdapter>();
                services.AddSingleton<IJellyfinInteractionProtocolAdapter>(interaction);
            });
        using var client = factory.CreateClient();

        using var localRequest = new HttpRequestMessage(
            HttpMethod.Post,
            local.GetProperty("request").GetProperty("path").GetString());
        using var localResponse = await client.SendAsync(localRequest);
        Assert.Equal(upstream.GetProperty("status").GetInt32(), (int)localResponse.StatusCode);
        Assert.Equal(
            CanonicalJson(upstream.GetProperty("body")),
            CanonicalJson(JsonDocument.Parse(await localResponse.Content.ReadAsStringAsync()).RootElement));

        var external = fixture.RootElement.GetProperty("favorite").GetProperty("external");
        using var externalRequest = new HttpRequestMessage(
            HttpMethod.Post,
            external.GetProperty("request").GetProperty("path").GetString());
        using var externalResponse = await client.SendAsync(externalRequest);
        var expected = external.GetProperty("expected");
        Assert.Equal(expected.GetProperty("status").GetInt32(), (int)externalResponse.StatusCode);
        Assert.Equal(expected.GetProperty("contentType").GetString(), externalResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            CanonicalJson(expected.GetProperty("body")),
            CanonicalJson(JsonDocument.Parse(await externalResponse.Content.ReadAsStringAsync()).RootElement));
        Assert.NotNull(interaction.LastContext);
        Assert.Equal("verified-user", interaction.LastContext!.VerifiedBackendPrincipalId);
        Assert.Null(interaction.LastContext.Actor);
        metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JellyfinPlayback_PreservesCapabilitiesAndProgressCompatibilityFixtures()
    {
        using var fixture = ReadFixture("jellyfin-favorite-playback.json");
        foreach (var capability in fixture.RootElement.GetProperty("capabilities").EnumerateArray())
        {
            var observedPaths = new List<string>();
            using var factory = new ProtocolFactory("Jellyfin", request =>
            {
                observedPaths.Add(request.RequestUri!.AbsolutePath);
                return request.RequestUri.AbsolutePath.Equals("/Users/Me", StringComparison.Ordinal)
                    ? Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""")
                    : Json(capability.GetProperty("upstreamStatus").GetInt32(), "{}");
            });
            using var client = factory.CreateClient();
            using var response = await client.PostAsync(
                "/Sessions/Capabilities?api_key=fixture-key",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(capability.GetProperty("expectedStatus").GetInt32(), (int)response.StatusCode);
            Assert.Equal(["/Users/Me", "/Sessions/Capabilities"], observedPaths);
        }

        var progress = fixture.RootElement.GetProperty("progress");
        using var progressFactory = new ProtocolFactory("Jellyfin", request =>
            request.RequestUri!.AbsolutePath.Equals("/Users/Me", StringComparison.Ordinal)
                ? Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""")
                : Json(progress.GetProperty("upstreamStatus").GetInt32(), "{}"));
        using var progressClient = progressFactory.CreateClient();
        using var progressResponse = await progressClient.PostAsync(
            "/Sessions/Playing/Progress?api_key=fixture-key",
            new StringContent(progress.GetProperty("requestBody").GetRawText(), Encoding.UTF8, "application/json"));
        Assert.Equal(progress.GetProperty("expectedStatus").GetInt32(), (int)progressResponse.StatusCode);
    }

    [Fact]
    public async Task JellyfinInstantMix_PreservesPinnedRouteClassesAcrossSupportedVersions()
    {
        using var fixtures = ReadFixture("jellyfin-instant-mix-paths.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var observedPaths = new List<string>();
            using var factory = new ProtocolFactory("Jellyfin", request =>
            {
                observedPaths.Add(request.RequestUri!.PathAndQuery);
                if (IsItemLookup(request, "item-1"))
                {
                    return ItemLookup("""{"Id":"item-1","Type":"Audio"}""");
                }
                return request.RequestUri.AbsolutePath.Equals("/Users/Me", StringComparison.Ordinal)
                    ? Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""")
                    : Json(fixture.GetProperty("status").GetInt32(), fixture.GetProperty("body").GetRawText());
            });
            using var client = factory.CreateClient();
            var path = fixture.GetProperty("path").GetString()!;
            var query = fixture.TryGetProperty("query", out var fixtureQuery)
                ? fixtureQuery.GetString() + "&"
                : string.Empty;
            var requestPath = $"{path}?{query}api_key=fixture-key&Limit=2";

            using var response = await client.GetAsync(requestPath);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(fixture.GetProperty("status").GetInt32(), (int)response.StatusCode);
            Assert.Equal(
                CanonicalJson(fixture.GetProperty("body")),
                CanonicalJson(JsonDocument.Parse(body).RootElement));
            var expectedPaths = path.Equals("/Items/item-1/InstantMix", StringComparison.Ordinal)
                ? new[] { "/Items?ids=item-1&limit=1", "/Users/Me?api_key=fixture-key", requestPath }
                : new[] { "/Users/Me?api_key=fixture-key", requestPath };
            Assert.Equal(expectedPaths, observedPaths);
        }

        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        using var unresolvedFactory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath.StartsWith("/Users/", StringComparison.Ordinal)
                ? Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
            });
        using var unresolvedClient = unresolvedFactory.CreateClient();
        using var unresolvedResponse = await unresolvedClient.GetAsync(
            "/Songs/ext-deezer-song-42/InstantMix?api_key=fixture-key&userId=spoofed-route-user");
        Assert.Equal(HttpStatusCode.OK, unresolvedResponse.StatusCode);
        Assert.Equal(
            "{\"Items\":[],\"TotalRecordCount\":0,\"StartIndex\":0}",
            await unresolvedResponse.Content.ReadAsStringAsync());
        metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JellyfinExternalSimilarAndInstantMix_StayOnTheTypedProvider()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(
                It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new Song
            {
                Id = "ext-deezer-song-42",
                ExternalProvider = "deezer",
                ExternalId = "42",
                Title = "Seed",
                Artist = "Fixture Artist"
            });
        gateway.Setup(service => service.SearchAsync(
                It.IsAny<ProtocolExecutionContext>(), "Fixture Artist", 2, 0, 0, "deezer"))
            .ReturnsAsync(new SearchResult
            {
                Songs =
                [
                    new Song
                    {
                        Id = "ext-deezer-song-42",
                        ExternalProvider = "deezer",
                        ExternalId = "42",
                        Title = "Seed",
                        Artist = "Fixture Artist"
                    },
                    new Song
                    {
                        Id = "ext-deezer-song-43",
                        ExternalProvider = "deezer",
                        ExternalId = "43",
                        Title = "Related",
                        Artist = "Fixture Artist"
                    }
                ]
            });
        var interaction = new Mock<IJellyfinInteractionProtocolAdapter>(MockBehavior.Strict);
        var shaper = new JellyfinInteractionProtocolAdapter();
        interaction.Setup(adapter => adapter.CanRunOptionalUserWork(
                It.IsAny<ProtocolExecutionContext?>()))
            .Returns(true);
        interaction.Setup(adapter => adapter.ShapeInstantMix(
                It.IsAny<IReadOnlyList<Dictionary<string, object?>>>()))
            .Returns((IReadOnlyList<Dictionary<string, object?>> items) =>
                shaper.ShapeInstantMix(items));
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
                services.RemoveAll<IJellyfinInteractionProtocolAdapter>();
                services.AddSingleton(interaction.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Items/ext-deezer-song-42/Similar?Limit=2&api_key=fixture-key");
        using var mixResponse = await client.GetAsync(
            "/Items/ext-deezer-song-42/InstantMix?Limit=2&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var mixBody = JsonDocument.Parse(await mixResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mixResponse.StatusCode);
        Assert.Equal(0, body.RootElement.GetProperty("StartIndex").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(
            "ext-deezer-song-43",
            body.RootElement.GetProperty("Items")[0].GetProperty("Id").GetString());
        Assert.Equal(1, mixBody.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(
            "ext-deezer-song-43",
            mixBody.RootElement.GetProperty("Items")[0].GetProperty("Id").GetString());
        gateway.Verify(service => service.SearchAsync(
            It.IsAny<ProtocolExecutionContext>(), "Fixture Artist", 2, 0, 0, "deezer"),
            Times.Exactly(2));
        gateway.VerifyAll();
        interaction.VerifyAll();
    }

    [Theory]
    [InlineData("/Albums/ext-deezer-album-42/InstantMix?Limit=2&api_key=fixture-key", "album")]
    [InlineData("/Artists/ext-deezer-artist-42/InstantMix?Limit=2&api_key=fixture-key", "artist")]
    [InlineData("/Artists/InstantMix?id=ext-deezer-artist-42&Limit=2&api_key=fixture-key", "artist")]
    public async Task JellyfinExternalInstantMix_UsesTheTypedProviderResource(
        string path,
        string resourceType)
    {
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        var songs = new List<Song>
        {
            new() { Id = "mix-1", Title = "First", Artist = "Fixture Artist" },
            new() { Id = "mix-2", Title = "Second", Artist = "Fixture Artist" }
        };
        if (resourceType == "album")
        {
            metadata.Setup(service => service.GetAlbumAsync(
                    "deezer", "42", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Album
                {
                    Id = "ext-deezer-album-42",
                    ExternalProvider = "deezer",
                    ExternalId = "42",
                    Title = "Fixture Album",
                    Artist = "Fixture Artist",
                    Songs = songs
                });
        }
        else
        {
            metadata.Setup(service => service.GetArtistAsync(
                    "deezer", "42", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Artist
                {
                    Id = "ext-deezer-artist-42",
                    ExternalProvider = "deezer",
                    ExternalId = "42",
                    Name = "Fixture Artist"
                });
            metadata.Setup(service => service.GetArtistAlbumsAsync(
                    "deezer", "42", It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new Album
                    {
                        Id = "ext-deezer-album-a1",
                        ExternalProvider = "deezer",
                        ExternalId = "a1",
                        Title = "Fixture Album",
                        Artist = "Fixture Artist"
                    }
                ]);
            metadata.Setup(service => service.GetAlbumAsync(
                    "deezer", "a1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Album
                {
                    Id = "ext-deezer-album-a1",
                    ExternalProvider = "deezer",
                    ExternalId = "a1",
                    Title = "Fixture Album",
                    Artist = "Fixture Artist",
                    Songs = songs
                });
        }

        var interaction = new Mock<IJellyfinInteractionProtocolAdapter>(MockBehavior.Strict);
        var shaper = new JellyfinInteractionProtocolAdapter();
        interaction.Setup(adapter => adapter.CanRunOptionalUserWork(
                It.IsAny<ProtocolExecutionContext?>()))
            .Returns(true);
        interaction.Setup(adapter => adapter.ShapeInstantMix(
                It.IsAny<IReadOnlyList<Dictionary<string, object?>>>()))
            .Returns((IReadOnlyList<Dictionary<string, object?>> items) =>
                shaper.ShapeInstantMix(items));
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
                services.RemoveAll<IProtocolProviderGateway>();
                services.RemoveAll<IJellyfinInteractionProtocolAdapter>();
                services.AddSingleton(interaction.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.RootElement.GetProperty("StartIndex").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("Items").GetArrayLength());
        Assert.Equal(
            ["mix-1", "mix-2"],
            body.RootElement.GetProperty("Items").EnumerateArray()
                .Select(item => item.GetProperty("Id").GetString()!).Order().ToArray());
        metadata.VerifyAll();
        interaction.VerifyAll();
    }

    [Fact]
    public async Task JellyfinLocalInstantMix_UsesSelectedAudioMuseAndPreservesNativeItemOrder()
    {
        var audioMuse = new Mock<IAudioMuseRecommendationClient>(MockBehavior.Strict);
        audioMuse.SetupGet(client => client.IsAvailable).Returns(true);
        audioMuse.Setup(client => client.FindSimilarAsync(
                It.Is<IntelligenceScope>(scope => scope.Protocol == "jellyfin" && scope.LibraryScopeId == "music"),
                It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "seed" })),
                2,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                SonicTrack("mix-2", .9, "Second"),
                SonicTrack("mix-1", .8, "First")
            ]);
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request =>
            {
                if (request.RequestUri!.AbsolutePath == "/Users/Me")
                    return Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""");
                if (request.RequestUri.AbsolutePath == "/Items" &&
                    QueryHelpers.ParseQuery(request.RequestUri.Query)["Ids"] == "mix-2,mix-1")
                    return Json(StatusCodes.Status200OK,
                        """{"Items":[{"Id":"mix-1","Name":"First","RunTimeTicks":100},{"Id":"mix-2","Name":"Second","RunTimeTicks":200}]}""");
                throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
            },
            SonicServices(audioMuse.Object));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Songs/seed/InstantMix?Limit=2&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = body.RootElement.GetProperty("Items");
        Assert.Equal(["mix-2", "mix-1"], items.EnumerateArray()
            .Select(item => item.GetProperty("Id").GetString()!).ToArray());
        Assert.Equal(200, items[0].GetProperty("RunTimeTicks").GetInt64());
        audioMuse.VerifyAll();
    }

    [Fact]
    public async Task JellyfinLocalInstantMix_StaysNativeWhenAudioMuseIsNotSelected()
    {
        var audioMuse = new Mock<IAudioMuseRecommendationClient>(MockBehavior.Strict);
        audioMuse.SetupGet(client => client.IsAvailable).Returns(true);
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath switch
            {
                "/Users/Me" => Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}"""),
                "/Songs/seed/InstantMix" => Json(StatusCodes.Status200OK,
                    """{"Items":[{"Id":"native-1","Name":"Native mix"}],"TotalRecordCount":1}"""),
                _ => throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}")
            },
            SonicServices(audioMuse.Object, selected: false));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Songs/seed/InstantMix?Limit=2&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("native-1", body.RootElement.GetProperty("Items")[0].GetProperty("Id").GetString());
        audioMuse.Verify(client => client.FindSimilarAsync(
            It.IsAny<IntelligenceScope>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenSubsonicSonicSimilarTracks_PreserveNativeJsonAndScope()
    {
        var audioMuse = new Mock<IAudioMuseRecommendationClient>(MockBehavior.Strict);
        audioMuse.SetupGet(client => client.IsAvailable).Returns(true);
        audioMuse.Setup(client => client.FindSimilarAsync(
                It.Is<IntelligenceScope>(scope => scope.Protocol == "subsonic" && scope.LibraryScopeId == "music"),
                It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "seed" })),
                2,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                SonicTrack("song-2", .9, "Second"),
                SonicTrack("song-1", .8, "First")
            ]);
        using var factory = new ProtocolFactory(
            "Subsonic",
            request => SubsonicSonicBackend(request),
            SonicServices(audioMuse.Object));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/getSonicSimilarTracks.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f=json&id=seed&count=2");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var matches = body.RootElement.GetProperty("subsonic-response").GetProperty("sonicMatch");
        Assert.Equal(["song-2", "song-1"], matches.EnumerateArray()
            .Select(item => item.GetProperty("entry").GetProperty("id").GetString()!).ToArray());
        Assert.Equal(202, matches[0].GetProperty("entry").GetProperty("duration").GetInt32());
        Assert.Equal(.9, matches[0].GetProperty("similarity").GetDouble(), 5);
        audioMuse.VerifyAll();
    }

    [Fact]
    public async Task OpenSubsonicSonicPath_PreservesNativeXmlEndpointOrder()
    {
        var audioMuse = new Mock<IAudioMuseRecommendationClient>(MockBehavior.Strict);
        audioMuse.SetupGet(client => client.IsAvailable).Returns(true);
        audioMuse.Setup(client => client.FindPathAsync(
                It.Is<IntelligenceScope>(scope => scope.Protocol == "subsonic" && scope.LibraryScopeId == "music"),
                "start", "end", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudioMusePathResult([
                SonicTrack("start", .7, "Start"),
                SonicTrack("bridge", .6, "Bridge"),
                SonicTrack("end", .5, "End")
            ], 1.2));
        using var factory = new ProtocolFactory(
            "Subsonic",
            request => SubsonicSonicBackend(request),
            SonicServices(audioMuse.Object));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/findSonicPath.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&startSongId=start&endSongId=end&count=3");
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        XNamespace ns = "http://subsonic.org/restapi";
        var matches = document.Root!.Elements(ns + "sonicMatch").ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["start", "bridge", "end"], matches
            .Select(item => item.Element(ns + "entry")!.Attribute("id")!.Value).ToArray());
        Assert.Equal("1", matches[0].Attribute("similarity")!.Value);
        Assert.All(matches, item => Assert.NotNull(item.Element(ns + "entry")!.Attribute("duration")));
        audioMuse.VerifyAll();
    }

    [Fact]
    public async Task OpenSubsonicSonicPath_RejectsEndpointsFromDifferentLibraries()
    {
        var audioMuse = new Mock<IAudioMuseRecommendationClient>(MockBehavior.Strict);
        audioMuse.SetupGet(client => client.IsAvailable).Returns(true);
        using var factory = new ProtocolFactory(
            "Subsonic",
            request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
                ? Json(StatusCodes.Status200OK,
                    """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            SonicServices(audioMuse.Object, otherLibraryItem: "end"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/findSonicPath.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&f=json&startSongId=start&endSongId=end&count=3");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(70, body.RootElement.GetProperty("subsonic-response")
            .GetProperty("error").GetProperty("code").GetInt32());
        audioMuse.Verify(client => client.FindPathAsync(
            It.IsAny<IntelligenceScope>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenSubsonicExtensionDiscovery_IsPublicAndMergesSonicCapability()
    {
        var audioMuse = new Mock<IAudioMuseRecommendationClient>(MockBehavior.Strict);
        audioMuse.SetupGet(client => client.IsAvailable).Returns(true);
        using var factory = new ProtocolFactory(
            "Subsonic",
            request => request.RequestUri!.AbsolutePath == "/rest/getOpenSubsonicExtensions.view"
                ? Json(StatusCodes.Status200OK,
                    """{"subsonic-response":{"status":"ok","version":"1.16.1","openSubsonicExtensions":[{"name":"songLyrics","versions":[1]}]}}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            SonicServices(audioMuse.Object));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/getOpenSubsonicExtensions.view?f=json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["songLyrics", "sonicSimilarity"], body.RootElement
            .GetProperty("subsonic-response").GetProperty("openSubsonicExtensions")
            .EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray());
        audioMuse.VerifyAll();
    }

    [Fact]
    public async Task OpenSubsonicExtensionDiscovery_AdvertisesSonicWhenBackendDiscoveryIsDown()
    {
        var audioMuse = new Mock<IAudioMuseRecommendationClient>(MockBehavior.Strict);
        audioMuse.SetupGet(client => client.IsAvailable).Returns(true);
        using var factory = new ProtocolFactory(
            "Subsonic",
            _ => throw new HttpRequestException("Backend discovery unavailable"),
            SonicServices(audioMuse.Object));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/getOpenSubsonicExtensions.view?f=json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sonicSimilarity", body.RootElement
            .GetProperty("subsonic-response").GetProperty("openSubsonicExtensions")[0]
            .GetProperty("name").GetString());
        audioMuse.VerifyAll();
    }

    [Theory]
    [InlineData("/Artists/InstantMix?id=ext-deezer-album-42&api_key=fixture-key")]
    [InlineData("/MusicGenres/InstantMix?id=ext-deezer-song-42&api_key=fixture-key")]
    [InlineData("/Artists/InstantMix?id=allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0&api_key=fixture-key")]
    [InlineData("/MusicGenres/InstantMix?id=allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0&api_key=fixture-key")]
    public async Task JellyfinQueryInstantMix_RejectsMismatchedSynthesizedResourceTypes(string path)
    {
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
                services.RemoveAll<IProtocolProviderGateway>();
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.Forbidden,
            $"Expected 403, got {(int)response.StatusCode}: {responseBody}");
        metadata.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JellyfinExternalPlaylist_PreservesSourceOrderAndPlaylistParentIdentity()
    {
        using var fixture = ReadFixture("jellyfin-playlist-order.json");
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var tracks = fixture.RootElement.GetProperty("sourceTrackIds")
            .EnumerateArray()
            .Select((id, index) => new allstarr.Models.Domain.Song
            {
                Id = $"ext-deezer-{id.GetString()}",
                ExternalProvider = "deezer",
                ExternalId = id.GetString(),
                Title = $"Fixture {index}",
                Artist = "Fixture Artist",
                Album = "Fixture Album"
            })
            .ToList();
        gateway.Setup(service => service.GetPlaylistTracksAsync(
                It.IsAny<ProtocolExecutionContext>(),
                fixture.RootElement.GetProperty("provider").GetString()!,
                fixture.RootElement.GetProperty("externalPlaylistId").GetString()!))
            .ReturnsAsync(tracks);
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            fixture.RootElement.GetProperty("requestPath").GetString());
        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Playlist fixture returned {(int)response.StatusCode}: {rawBody}");
        using var body = JsonDocument.Parse(rawBody);
        var items = body.RootElement.GetProperty("Items").EnumerateArray().ToList();

        Assert.Equal(
            fixture.RootElement.GetProperty("expectedItemIds").EnumerateArray().Select(id => id.GetString()),
            items.Select(item => item.GetProperty("Id").GetString()));
        Assert.All(items, item => Assert.Equal(
            fixture.RootElement.GetProperty("expectedParentId").GetString(),
            item.GetProperty("ParentId").GetString()));
        Assert.Equal(items.Count, body.RootElement.GetProperty("TotalRecordCount").GetInt32());

        using var definitionResponse = await client.GetAsync(
            "/Playlists/ext-deezer-playlist-list-7?api_key=fixture-key");
        using var definition = JsonDocument.Parse(await definitionResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, definitionResponse.StatusCode);
        Assert.Empty(definition.RootElement.GetProperty("Shares").EnumerateArray());
        Assert.Equal(
            fixture.RootElement.GetProperty("expectedItemIds").EnumerateArray().Select(id => id.GetString()),
            definition.RootElement.GetProperty("ItemIds").EnumerateArray().Select(id => id.GetString()));
        gateway.VerifyAll();
    }

    [Fact]
    public async Task JellyfinExternalArtist_NasAmazonLabelRoutesToProviderAlbumsAndTracks()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetArtistAlbumsAsync(
                It.IsAny<ProtocolExecutionContext>(), "spotiflac-amazon", "artist-1"))
            .ReturnsAsync([
                new Album
                {
                    Id = "ext-spotiflac-amazon-album-album-1",
                    ExternalProvider = "spotiflac-amazon",
                    ExternalId = "album-1",
                    Title = "Illmatic",
                    Artist = "NAS",
                    ArtistId = "ext-spotiflac-amazon-artist-artist-1",
                    IsLocal = false
                }
            ]);
        gateway.Setup(service => service.GetArtistTracksAsync(
                It.IsAny<ProtocolExecutionContext>(), "spotiflac-amazon", "artist-1"))
            .ReturnsAsync([
                new Song
                {
                    Id = "ext-spotiflac-amazon-song-track-1",
                    ExternalProvider = "spotiflac-amazon",
                    ExternalId = "track-1",
                    Title = "N.Y. State of Mind",
                    Artist = "NAS",
                    Artists = ["NAS"],
                    ArtistId = "ext-spotiflac-amazon-artist-artist-1",
                    ArtistIds = ["ext-spotiflac-amazon-artist-artist-1"],
                    Album = "Illmatic",
                    AlbumId = "ext-spotiflac-amazon-album-album-1",
                    Duration = 210,
                    IsLocal = false
                }
            ]);
        gateway.Setup(service => service.GetArtistAsync(
                It.IsAny<ProtocolExecutionContext>(), "spotiflac-amazon", "artist-1"))
            .ReturnsAsync(new Artist
            {
                Id = "ext-spotiflac-amazon-artist-artist-1",
                ExternalProvider = "spotiflac-amazon",
                ExternalId = "artist-1",
                Name = "NAS",
                IsLocal = false
            });
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Items?ParentId=ext-spotiflac-amazon-artist-artist-1&IncludeItemTypes=MusicAlbum,Audio&Limit=10&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        var album = Assert.Single(items, item => item.GetProperty("Type").GetString() == "MusicAlbum");
        Assert.Equal("NAS [AmM]", album.GetProperty("AlbumArtist").GetString());
        var track = Assert.Single(items, item => item.GetProperty("Type").GetString() == "Audio");
        Assert.Equal(210 * TimeSpan.TicksPerSecond, track.GetProperty("RunTimeTicks").GetInt64());
        Assert.Equal("NAS [AmM]", track.GetProperty("Artists")[0].GetString());
        Assert.Equal("ext-spotiflac-amazon-artist-artist-1", track.GetProperty("ArtistItems")[0].GetProperty("Id").GetString());
        gateway.VerifyAll();
    }

    [Fact]
    public async Task SubsonicEmptySearch_PreservesCurrentFormAndResponseFixture()
    {
        using var fixture = ReadFixture("subsonic-empty-search.json");
        var currentRelay = fixture.RootElement.GetProperty("currentRelay");
        var expected = fixture.RootElement.GetProperty("expected");
        var observedMethod = string.Empty;
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            observedMethod = request.Method.Method;
            return Json(StatusCodes.Status200OK, currentRelay.GetProperty("body").GetRawText());
        });
        using var client = factory.CreateClient();
        var requestFixture = fixture.RootElement.GetProperty("request");
        var uri = $"{requestFixture.GetProperty("path").GetString()}?{requestFixture.GetProperty("query").GetString()}";
        using var content = new StringContent(
            requestFixture.GetProperty("body").GetString()!,
            Encoding.UTF8,
            requestFixture.GetProperty("contentType").GetString());

        using var response = await client.PostAsync(uri, content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expected.GetProperty("status").GetInt32(), (int)response.StatusCode);
        Assert.StartsWith(expected.GetProperty("contentType").GetString(), response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(currentRelay.GetProperty("upstreamMethod").GetString(), observedMethod);
        Assert.Equal(
            currentRelay.GetProperty("body").GetRawText(),
            JsonDocument.Parse(body).RootElement.GetRawText());
    }

    [Fact]
    public async Task SubsonicAuthBoundary_RejectsBeforeBackendActionsAndPreservesVerificationResponse()
    {
        using var fixtures = ReadFixture("subsonic-auth-boundary.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var observedRequests = new List<ObservedRequest>();
            var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
            var downloads = new Mock<IDownloadService>(MockBehavior.Strict);
            var verification = fixture.GetProperty("verification");

            using var factory = new ProtocolFactory(
                "Subsonic",
                request =>
                {
                    observedRequests.Add(Observe(request));
                    Assert.NotEqual(JsonValueKind.Null, verification.ValueKind);
                    return FixtureResponse(verification);
                },
                services =>
                {
                    services.RemoveAll<IMusicMetadataService>();
                    services.AddSingleton(metadata.Object);
                    services.RemoveAll<IDownloadService>();
                    services.AddSingleton(downloads.Object);
                });
            using var client = factory.CreateClient();
            using var request = CreateFixtureRequest(fixture.GetProperty("request"));

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            var expected = fixture.GetProperty("expected");

            Assert.Equal(expected.GetProperty("status").GetInt32(), (int)response.StatusCode);
            Assert.StartsWith(
                expected.GetProperty("contentType").GetString(),
                response.Content.Headers.ContentType?.MediaType,
                StringComparison.OrdinalIgnoreCase);

            if (expected.TryGetProperty("body", out var expectedJson))
            {
                Assert.Equal(
                    CanonicalJson(expectedJson),
                    CanonicalJson(JsonDocument.Parse(responseBody).RootElement));
            }
            else
            {
                Assert.Equal(expected.GetProperty("rawBody").GetString(), responseBody);
            }

            if (verification.ValueKind == JsonValueKind.Null)
            {
                Assert.Empty(observedRequests);
            }
            else
            {
                var observed = Assert.Single(observedRequests);
                Assert.Equal(verification.GetProperty("method").GetString(), observed.Method);
                Assert.Equal(verification.GetProperty("pathAndQuery").GetString(), observed.PathAndQuery);
                Assert.Equal(
                    verification.TryGetProperty("requestBody", out var verificationBody)
                        ? verificationBody.GetString()
                        : null,
                    observed.Body);
            }

            metadata.VerifyNoOtherCalls();
            downloads.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task SubsonicRelay_PreservesMethodSourcesOrderingRepetitionStatusAndBody()
    {
        using var fixtures = ReadFixture("subsonic-relay-fidelity.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var observedRequests = new List<ObservedRequest>();
            var relay = fixture.GetProperty("relay");
            using var factory = new ProtocolFactory("Subsonic", request =>
            {
                observedRequests.Add(Observe(request));
                if (observedRequests.Count == 1)
                {
                    return Json(
                        StatusCodes.Status200OK,
                        """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
                }

                return new HttpResponseMessage((HttpStatusCode)relay.GetProperty("status").GetInt32())
                {
                    Content = new StringContent(
                        relay.GetProperty("responseBody").GetRawText(),
                        Encoding.UTF8,
                        relay.GetProperty("contentType").GetString()!)
                };
            });
            using var client = factory.CreateClient();
            using var request = CreateFixtureRequest(fixture.GetProperty("request"));

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            Assert.True(
                relay.GetProperty("status").GetInt32() == (int)response.StatusCode,
                $"{fixture.GetProperty("name").GetString()} returned {(int)response.StatusCode}: {responseBody}; " +
                $"observed={string.Join(" | ", observedRequests)}");
            Assert.StartsWith(
                relay.GetProperty("contentType").GetString(),
                response.Content.Headers.ContentType?.MediaType,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                CanonicalJson(relay.GetProperty("responseBody")),
                CanonicalJson(JsonDocument.Parse(responseBody).RootElement));

            Assert.Equal(2, observedRequests.Count);
            AssertObservedRequest(fixture.GetProperty("verification"), observedRequests[0]);
            AssertObservedRequest(relay, observedRequests[1]);
        }
    }

    [Theory]
    [InlineData(
        "GET",
        "/rest/updatePlaylist.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&playlistId=allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0&songIdToAdd=song-a&songIdToAdd=song-b&songIndexToRemove=2&songIndexToRemove=0",
        null,
        null,
        "/rest/updatePlaylist.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&playlistId=backend-target&songIdToAdd=song-a&songIdToAdd=song-b&songIndexToRemove=2&songIndexToRemove=0",
        null)]
    [InlineData(
        "POST",
        "/rest/updatePlaylist.view?v=1.16.1&c=fixture&f=json",
        "application/x-www-form-urlencoded",
        "u=fixture&p=secret&playlistId=allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0&songIdToAdd=song-a&songIdToAdd=song-b&songIndexToRemove=1&songIndexToRemove=0",
        "/rest/updatePlaylist.view?v=1.16.1&c=fixture&f=json",
        "u=fixture&p=secret&playlistId=backend-target&songIdToAdd=song-a&songIdToAdd=song-b&songIndexToRemove=1&songIndexToRemove=0")]
    public async Task SubsonicUpdatePlaylist_RewritesOnlyScopedVirtualTargetAndPreservesRelayFidelity(
        string method,
        string path,
        string? contentType,
        string? body,
        string expectedPath,
        string? expectedBody)
    {
        var observedRequests = new List<ObservedRequest>();
        var resolver = new FixedPlaylistMutationResolver(
            new SubsonicPlaylistMutationRoute(true, "backend-target"));
        using var factory = new ProtocolFactory(
            "Subsonic",
            request =>
            {
                observedRequests.Add(Observe(request));
                return Json(
                    observedRequests.Count == 1 ? StatusCodes.Status200OK : StatusCodes.Status202Accepted,
                    """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            },
            services =>
            {
                services.RemoveAll<ISubsonicPlaylistMutationResolver>();
                services.AddSingleton<ISubsonicPlaylistMutationResolver>(resolver);
            });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType!);
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, observedRequests.Count);
        Assert.Equal(expectedPath, observedRequests[1].PathAndQuery);
        Assert.Equal(expectedBody, observedRequests[1].Body);
    }

    [Fact]
    public async Task SubsonicUpdatePlaylist_ReadOnlyVirtualLinkReturnsProtocolErrorWithoutMutation()
    {
        var observedRequests = new List<ObservedRequest>();
        using var factory = new ProtocolFactory(
            "Subsonic",
            request =>
            {
                observedRequests.Add(Observe(request));
                return Json(
                    StatusCodes.Status200OK,
                    """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            },
            services =>
            {
                services.RemoveAll<ISubsonicPlaylistMutationResolver>();
                services.AddSingleton<ISubsonicPlaylistMutationResolver>(
                    new FixedPlaylistMutationResolver(
                        new SubsonicPlaylistMutationRoute(false, null)));
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/updatePlaylist.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&playlistId=allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0&songIdToAdd=song-a");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("subsonic-response").GetProperty("error");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(50, error.GetProperty("code").GetInt32());
        Assert.Equal("Playlist is read-only", error.GetProperty("message").GetString());
        Assert.Single(observedRequests);
        Assert.Equal("/rest/ping.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json", observedRequests[0].PathAndQuery);
    }

    [Fact]
    public async Task JellyfinConfiguredNativePlaylist_UsesCompleteDurableProjection()
    {
        const string nativeId = "ddc3db277be524ad6f54e4b276cc619a";
        const string sourceId = "37i9dQZEVXbLRQDuF5jeBp";
        const string virtualId = "allstarr-vpl-019fa3ec414873ec9239a8114469c608";
        var tracks = Enumerable.Range(0, 50)
            .Select(index => index == 0
                ? new VirtualPlaylistTrack(
                    index, "local-song-a", "Babydoll", "Dominic Fike", "Don't Forget About Me, Demos",
                    "Dominic Fike", 102_000, "cover-a", allstarr.Core.Storage.TrackMatchState.Accepted,
                    SourceProviderId: "spotify")
                : new VirtualPlaylistTrack(
                    index, $"allstarr-unresolved-source-{index}", $"Source Track {index + 1}",
                    $"Source Artist {index + 1}", $"Source Album {index + 1}", null, 180_000, null,
                    allstarr.Core.Storage.TrackMatchState.Unresolved,
                    SourceProviderId: "spotify", RouteKind: TrackRouteKind.Unresolved))
            .ToArray();
        var model = new VirtualPlaylistReadModel(
            virtualId,
            Guid.Parse("019fa3ec-4148-73ec-9239-a8114469c608"),
            Guid.CreateVersion7(),
            "Top 50 - USA",
            "Spotify chart",
            "artwork-key",
            "spotify",
            sourceId,
            "revision-24",
            allstarr.Core.Storage.PlaylistLinkMode.Virtual,
            tracks);
        var virtualization = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        virtualization.Setup(service => service.ListAsync(
                It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([model]);
        virtualization.Setup(service => service.ReadBySourceAsync(
                It.IsAny<ProtocolExecutionContext>(),
                "spotify",
                sourceId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        var observed = new List<string>();
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request =>
            {
                observed.Add(request.RequestUri!.PathAndQuery);
                return request.RequestUri.AbsolutePath switch
                {
                    "/Users/Me" => Json(
                        StatusCodes.Status200OK,
                        """{"Id":"user-1","Name":"Fixture User"}"""),
                    "/Users/user-1/Items" => Json(
                        StatusCodes.Status200OK,
                        $$"""
                          {
                            "Items":[{
                              "Id":"{{nativeId}}",
                              "Name":"Top 50 - USA",
                              "Type":"Playlist",
                              "MediaType":"Audio",
                              "ChildCount":5,
                              "CanDelete":true,
                              "DateCreated":"2026-07-31T02:11:00Z",
                              "ImageTags":{"Primary":"native-artwork"},
                              "ExtraBrowseData":{"Sentinel":"keep-native-fields"}
                            }],
                            "TotalRecordCount":1,
                            "StartIndex":0
                          }
                          """),
                    $"/Items/{nativeId}" => Json(
                        StatusCodes.Status200OK,
                        $$"""{"Id":"{{nativeId}}","Name":"Top 50 - USA","Type":"Playlist","ChildCount":5}"""),
                    "/Items" => Json(
                        StatusCodes.Status200OK,
                        """
                        {
                          "Items":[{
                            "Id":"local-song-a",
                            "Name":"Babydoll",
                            "ServerId":"server-1",
                            "Type":"Audio",
                            "MediaType":"Audio",
                            "AlbumId":"album-a",
                            "MediaSources":[{"Id":"source-a","Path":"/music/babydoll.flac"}],
                            "ExtraNestedData":{"Sentinel":[{"Keep":"every-field"}]}
                          }],
                          "TotalRecordCount":1,
                          "StartIndex":0
                        }
                        """),
                    _ => throw new InvalidOperationException(
                        $"Unexpected upstream request: {request.RequestUri}")
                };
            },
            services =>
            {
                services.RemoveAll<IPlaylistVirtualizationService>();
                services.AddSingleton(virtualization.Object);
            },
            new Dictionary<string, string?>
            {
                ["SpotifyImport:Enabled"] = "true",
                ["SpotifyImport:Playlists:0:Name"] = "Top 50 - USA",
                ["SpotifyImport:Playlists:0:Id"] = sourceId,
                ["SpotifyImport:Playlists:0:JellyfinId"] = nativeId,
                ["SpotifyImport:Playlists:0:UserId"] = "user-1"
            });
        using var client = factory.CreateClient();

        using var browseResponse = await client.GetAsync(
            "/Users/user-1/Items?includeItemTypes=Playlist&recursive=true&startIndex=0&limit=200&api_key=fixture-key");
        using var browse = JsonDocument.Parse(await browseResponse.Content.ReadAsStringAsync());
        using var tracksResponse = await client.GetAsync(
            $"/Playlists/{nativeId}/Items?fields=SortName%2CCanDelete%2CMediaSources%2CDateCreated%2CCanDelete&userId=user-1&startIndex=0&limit=200&api_key=fixture-key");
        using var playlist = JsonDocument.Parse(await tracksResponse.Content.ReadAsStringAsync());
        using var parentResponse = await client.GetAsync(
            $"/Users/user-1/Items?parentId={nativeId}&includeItemTypes=Audio&startIndex=0&limit=200&api_key=fixture-key");
        using var parentItems = JsonDocument.Parse(await parentResponse.Content.ReadAsStringAsync());
        using var detailResponse = await client.GetAsync(
            $"/Items/{nativeId}?userId=user-1&api_key=fixture-key");
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        using var definitionResponse = await client.GetAsync(
            $"/Playlists/{nativeId}?userId=user-1&api_key=fixture-key");
        using var definition = JsonDocument.Parse(await definitionResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, browseResponse.StatusCode);
        Assert.Equal(1, browse.RootElement.GetProperty("TotalRecordCount").GetInt32());
        var browseItem = Assert.Single(browse.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(nativeId, browseItem.GetProperty("Id").GetString());
        Assert.Equal(50, browseItem.GetProperty("ChildCount").GetInt32());
        Assert.True(browseItem.GetProperty("CanDelete").GetBoolean());
        Assert.Equal(
            "keep-native-fields",
            browseItem.GetProperty("ExtraBrowseData").GetProperty("Sentinel").GetString());
        Assert.Equal(HttpStatusCode.OK, tracksResponse.StatusCode);
        Assert.Equal(50, playlist.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(50, playlist.RootElement.GetProperty("Items").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, parentResponse.StatusCode);
        Assert.Equal(50, parentItems.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(50, parentItems.RootElement.GetProperty("Items").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Equal(nativeId, detail.RootElement.GetProperty("Id").GetString());
        Assert.Equal(50, detail.RootElement.GetProperty("ChildCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, definitionResponse.StatusCode);
        Assert.Equal(50, definition.RootElement.GetProperty("ItemIds").GetArrayLength());
        Assert.All(playlist.RootElement.GetProperty("Items").EnumerateArray(),
            item => Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("Id").GetString())));
        var first = playlist.RootElement.GetProperty("Items")[0];
        Assert.False(first.TryGetProperty("ParentId", out _));
        Assert.Equal("source-a", first.GetProperty("MediaSources")[0].GetProperty("Id").GetString());
        Assert.Equal(
            "every-field",
            first.GetProperty("ExtraNestedData").GetProperty("Sentinel")[0].GetProperty("Keep").GetString());
        Assert.DoesNotContain(
            observed,
            path => path.StartsWith($"/Playlists/{nativeId}/Items", StringComparison.Ordinal));
        virtualization.VerifyAll();
    }

    [Fact]
    public async Task JellyfinWritableHybrid_ReadsInjectedProjectionInsteadOfNativeTarget()
    {
        const string virtualId = "allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0";
        const string originalItem = """
            {
              "Name": "Original Track",
              "ServerId": "original-server",
              "Id": "local-song-a",
              "ParentId": "original-parent",
              "AlbumId": "album-original",
              "Album": "Original Album",
              "AlbumArtist": "Original Artist",
              "Artists": ["Original Artist"],
              "ArtistItems": [{"Name": "Original Artist", "Id": "artist-original"}],
              "AlbumArtists": [{"Name": "Original Artist", "Id": "artist-original"}],
              "RunTimeTicks": 1800000000,
              "IndexNumber": 7,
              "Type": "Audio",
              "MediaType": "Audio",
              "CanDelete": true,
              "CanDownload": true,
              "MediaSources": [{
                "Id": "media-original",
                "Path": "/music/original.flac",
                "MediaStreams": [{"Index": 0, "Codec": "flac"}]
              }],
              "ImageTags": {"Primary": "image-original"},
              "ProviderIds": {"MusicBrainzTrack": "mb-original"},
              "UserData": {
                "ItemId": "local-song-a",
                "Key": "Audio-local-song-a",
                "IsFavorite": true
              },
              "ExtraNestedData": {"Sentinel": [{"Keep": "every-field"}]}
            }
            """;
        var model = new VirtualPlaylistReadModel(
            virtualId,
            Guid.Parse("0198a537-719c-7ea8-9e5a-17e1f2f963f0"),
            Guid.CreateVersion7(),
            "Injected Mix",
            "Projected playlist",
            "artwork-key",
            "spotify",
            "source-list",
            "revision-1",
            allstarr.Core.Storage.PlaylistLinkMode.Hybrid,
            [
                new VirtualPlaylistTrack(
                    0,
                    "local-song-a",
                    "Injected Track",
                    "Injected Artist",
                    "Injected Album",
                    "Injected Artist",
                    180_000,
                    "cover-a",
                    allstarr.Core.Storage.TrackMatchState.Accepted,
                    SourceProviderId: "spotify"),
                new VirtualPlaylistTrack(
                    1,
                    "allstarr-unresolved-source-hash",
                    "Unmatched Track",
                    "Unmatched Artist",
                    "Unmatched Album",
                    null,
                    200_000,
                    null,
                    allstarr.Core.Storage.TrackMatchState.Unresolved,
                    SourceProviderId: "spotify",
                    RouteKind: TrackRouteKind.Unresolved)
            ]);
        var virtualization = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        virtualization.Setup(service => service.ReadAsync(
                It.IsAny<ProtocolExecutionContext>(),
                virtualId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        var observed = new List<string>();
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request =>
            {
                observed.Add(request.RequestUri!.PathAndQuery);
                return request.RequestUri.AbsolutePath switch
                {
                    "/Users/Me" => Json(
                        StatusCodes.Status200OK,
                        """{"Id":"user-1","Name":"Fixture User"}"""),
                    "/Items" => Json(
                        StatusCodes.Status200OK,
                        $$"""{"Items":[{{originalItem}}],"TotalRecordCount":1,"StartIndex":0}"""),
                    _ => throw new InvalidOperationException(
                        $"Unexpected upstream request: {request.RequestUri}")
                };
            },
            services =>
            {
                services.RemoveAll<IPlaylistVirtualizationService>();
                services.AddSingleton(virtualization.Object);
                services.RemoveAll<IJellyfinPlaylistMutationResolver>();
                services.AddSingleton<IJellyfinPlaylistMutationResolver>(
                    new FixedJellyfinPlaylistMutationResolver(
                        new JellyfinPlaylistMutationRoute(true, "backend-target")));
            });
        using var client = factory.CreateClient();

        using var itemResponse = await client.GetAsync($"/Items/{virtualId}?api_key=fixture-key");
        using var definitionResponse = await client.GetAsync($"/Playlists/{virtualId}?api_key=fixture-key");
        using var tracksResponse = await client.GetAsync($"/Playlists/{virtualId}/Items?api_key=fixture-key");
        using var unresolvedFile = await client.GetAsync(
            "/Items/allstarr-unresolved-source-hash/File?api_key=fixture-key");
        using var unresolvedStream = await client.GetAsync(
            "/Audio/allstarr-unresolved-source-hash/stream?api_key=fixture-key");
        using var unresolvedUniversal = await client.GetAsync(
            "/Audio/allstarr-unresolved-source-hash/universal?api_key=fixture-key");
        using var unresolvedPlayback = await client.GetAsync(
            "/Items/allstarr-unresolved-source-hash/PlaybackInfo?api_key=fixture-key");
        using var item = JsonDocument.Parse(await itemResponse.Content.ReadAsStringAsync());
        using var definition = JsonDocument.Parse(await definitionResponse.Content.ReadAsStringAsync());
        using var tracks = JsonDocument.Parse(await tracksResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, definitionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tracksResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unresolvedFile.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unresolvedStream.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unresolvedUniversal.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unresolvedPlayback.StatusCode);
        Assert.Equal(virtualId, item.RootElement.GetProperty("Id").GetString());
        Assert.Equal("local-song-a", definition.RootElement.GetProperty("ItemIds")[0].GetString());
        var actual = JsonNode.Parse(
            tracks.RootElement.GetProperty("Items")[0].GetRawText())!.AsObject();
        var expected = JsonNode.Parse(originalItem)!.AsObject();
        expected["PlaylistItemId"] = "local-song-a";

        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"Expected full source DTO with playlist overlays.\nExpected: {expected}\nActual: {actual}");
        Assert.Equal("Original Track", actual["Name"]!.GetValue<string>());
        Assert.Equal("original-parent", actual["ParentId"]!.GetValue<string>());
        Assert.False(actual["ProviderIds"]!.AsObject().ContainsKey("AllstarrSource"));
        Assert.Equal("local-song-a", actual["Id"]!.GetValue<string>());
        Assert.Equal("local-song-a", actual["PlaylistItemId"]!.GetValue<string>());
        Assert.Equal("album-original", actual["AlbumId"]!.GetValue<string>());
        Assert.Equal("media-original", actual["MediaSources"]![0]!["Id"]!.GetValue<string>());
        Assert.Equal("artist-original", actual["ArtistItems"]![0]!["Id"]!.GetValue<string>());
        Assert.Equal("artist-original", actual["AlbumArtists"]![0]!["Id"]!.GetValue<string>());
        var unresolved = tracks.RootElement.GetProperty("Items")[1];
        Assert.Equal("allstarr-unresolved-source-hash",
            unresolved.GetProperty("Id").GetString());
        Assert.Equal("None", unresolved.GetProperty("PlayAccess").GetString());
        Assert.False(unresolved.GetProperty("CanDownload").GetBoolean());
        Assert.Empty(unresolved.GetProperty("MediaSources").EnumerateArray());
        Assert.Equal(3, observed.Count(path => path == "/Users/Me?api_key=fixture-key"));
        var hydration = Assert.Single(observed, path =>
            path.StartsWith("/Items?", StringComparison.Ordinal));
        Assert.Contains("Ids=local-song-a", hydration, StringComparison.Ordinal);
        Assert.Contains("UserId=user-1", hydration, StringComparison.Ordinal);
        Assert.Contains("api_key=fixture-key", hydration, StringComparison.Ordinal);
        Assert.Contains("MediaSources", Uri.UnescapeDataString(hydration), StringComparison.Ordinal);
        virtualization.VerifyAll();
    }

    [Theory]
    [InlineData("POST", "/Playlists/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0?api_key=fixture-key",
        """{"Name":"Renamed"}""", "/Playlists/backend-target?api_key=fixture-key")]
    [InlineData("POST", "/Playlists/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0/Items?ids=song-a&ids=song-b&api_key=fixture-key",
        null, "/Playlists/backend-target/Items?ids=song-a&ids=song-b&api_key=fixture-key")]
    [InlineData("DELETE", "/Playlists/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0/Items?entryIds=entry-a&api_key=fixture-key",
        null, "/Playlists/backend-target/Items?entryIds=entry-a&api_key=fixture-key")]
    [InlineData("POST", "/Playlists/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0/Items/entry-a/Move/2?api_key=fixture-key",
        null, "/Playlists/backend-target/Items/entry-a/Move/2?api_key=fixture-key")]
    [InlineData("GET", "/Playlists/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0/Users?api_key=fixture-key",
        null, "/Playlists/backend-target/Users?api_key=fixture-key")]
    [InlineData("POST", "/Playlists/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0/Users/user-2?api_key=fixture-key",
        """{"CanEdit":true}""", "/Playlists/backend-target/Users/user-2?api_key=fixture-key")]
    [InlineData("DELETE", "/Playlists/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0/Users/user-2?api_key=fixture-key",
        null, "/Playlists/backend-target/Users/user-2?api_key=fixture-key")]
    [InlineData("GET", "/Playlists/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0/InstantMix?Limit=12&api_key=fixture-key",
        null, "/Playlists/backend-target/InstantMix?Limit=12&api_key=fixture-key")]
    public async Task JellyfinLinkedPlaylistOperations_RewriteOnlyTheScopedTargetAndPreserveRelay(
        string method,
        string path,
        string? body,
        string expectedPath)
    {
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request =>
            {
                observed.Add(Observe(request));
                return request.RequestUri!.AbsolutePath == "/Users/Me"
                    ? Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""")
                    : Json(StatusCodes.Status202Accepted, """{"accepted":true}""");
            },
            services =>
            {
                services.RemoveAll<IJellyfinPlaylistMutationResolver>();
                services.AddSingleton<IJellyfinPlaylistMutationResolver>(
                    new FixedJellyfinPlaylistMutationResolver(
                        new JellyfinPlaylistMutationRoute(true, "backend-target")));
            });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, observed.Count);
        Assert.Equal(method, observed[1].Method);
        Assert.Equal(expectedPath, observed[1].PathAndQuery);
        Assert.Equal(body ?? (method is "GET" or "HEAD" ? null : string.Empty), observed[1].Body);
    }

    [Theory]
    [InlineData("allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0")]
    [InlineData("ext-spotify-playlist-source-1")]
    public async Task JellyfinReadOnlyPlaylistMutation_ReturnsConflictWithoutBackendMutation(
        string playlistId)
    {
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request =>
            {
                observed.Add(Observe(request));
                return Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""");
            },
            services =>
            {
                services.RemoveAll<IJellyfinPlaylistMutationResolver>();
                services.AddSingleton<IJellyfinPlaylistMutationResolver>(
                    new FixedJellyfinPlaylistMutationResolver(
                        new JellyfinPlaylistMutationRoute(false, null)));
            });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/Playlists/{playlistId}/Items?ids=song-a&api_key=fixture-key",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(observed);
        Assert.Equal("/Users/Me?api_key=fixture-key", observed[0].PathAndQuery);
    }

    [Theory]
    [InlineData("DELETE", "Playlist", HttpStatusCode.NoContent, 3)]
    [InlineData("DELETE", "Audio", HttpStatusCode.Forbidden, 1)]
    [InlineData("DELETE", "Movie", HttpStatusCode.Forbidden, 1)]
    [InlineData("POST", "Playlist", HttpStatusCode.NoContent, 3)]
    [InlineData("POST", "Audio", HttpStatusCode.Forbidden, 1)]
    [InlineData("POST", "Movie", HttpStatusCode.Forbidden, 1)]
    public async Task JellyfinItemMutation_RelaysOnlyNativePlaylists(
        string method,
        string itemType,
        HttpStatusCode expectedStatus,
        int expectedRequestCount)
    {
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observed.Add(Observe(request));
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""");
            if (request.Method == HttpMethod.Get)
                return Json(StatusCodes.Status200OK,
                    $$"""{"Items":[{"Id":"item-1","Type":"{{itemType}}"}],"TotalRecordCount":1,"StartIndex":0}""");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var client = factory.CreateClient();
        var itemId = $"{itemType.ToLowerInvariant()}-item";
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            $"/Items/{itemId}?api_key=fixture-key")
        {
            Content = method == "POST"
                ? new StringContent(
                    $$"""{"Id":"{{itemId}}","Name":"Playlist","Type":"Playlist"}""",
                    Encoding.UTF8,
                    "application/json")
                : null
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedRequestCount, observed.Count);
        Assert.Equal($"/Items?ids={itemId}&limit=1", observed[0].PathAndQuery);
        if (itemType == "Playlist")
        {
            Assert.Equal("/Users/Me?api_key=fixture-key", observed[1].PathAndQuery);
            Assert.Equal(method, observed[2].Method);
            Assert.Equal($"/Items/{itemId}?api_key=fixture-key", observed[2].PathAndQuery);
            if (method == "POST")
                Assert.Equal(
                    $$"""{"Id":"{{itemId}}","Name":"Playlist","Type":"Playlist"}""",
                    observed[2].Body);
        }
    }

    [Fact]
    public async Task JellyfinPlaylistMutation_ClassifiesWithTheLoggedInUserToken()
    {
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observed.Add(Observe(request));
            if (request.RequestUri!.AbsolutePath == "/Items")
            {
                var hasClientToken = request.Headers.TryGetValues(
                        "X-Emby-Authorization", out var values) &&
                    values.Any(value => value.Contains("caller-token", StringComparison.Ordinal));
                return Json(StatusCodes.Status200OK, hasClientToken
                    ? """{"Items":[{"Id":"user-playlist","Type":"Playlist"}],"TotalRecordCount":1}"""
                    : """{"Items":[],"TotalRecordCount":0}""");
            }
            if (request.RequestUri.AbsolutePath == "/Users/Me")
                return Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Items/user-playlist")
        {
            Content = new StringContent(
                """{"Id":"user-playlist","Name":"Renamed","Type":"Playlist"}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation(
            "X-Emby-Authorization",
            "MediaBrowser Client=\"Fixture\", Device=\"Tests\", DeviceId=\"test-1\", Version=\"1\", UserId=\"user-1\", Token=\"caller-token\"");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(3, observed.Count);
        Assert.Equal("/Items?ids=user-playlist&limit=1&userId=user-1", observed[0].PathAndQuery);
        Assert.Equal("/Users/Me", observed[1].PathAndQuery);
        Assert.Equal("POST", observed[2].Method);
        Assert.Equal("/Items/user-playlist", observed[2].PathAndQuery);
    }

    [Fact]
    public async Task JellyfinMediaFolders_FallsBackToNonAdminUserViews()
    {
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observed.Add(Observe(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/Users/Me" => Json(
                    StatusCodes.Status200OK,
                    """{"Id":"user-1","Name":"Fixture User"}"""),
                "/Library/MediaFolders" => Json(
                    StatusCodes.Status403Forbidden,
                    """{"error":"administrator access required"}"""),
                "/UserViews" => Json(
                    StatusCodes.Status200OK,
                    """{"Items":[{"Id":"music","CollectionType":"music"},{"Id":"movies","CollectionType":"movies"}],"TotalRecordCount":2}"""),
                _ => throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}")
            };
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/Library/MediaFolders?UserId=user-1");
        request.Headers.TryAddWithoutValidation("X-Emby-Token", "caller-token");

        using var response = await client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(document.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal("music", item.GetProperty("CollectionType").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Contains(observed, candidate => candidate.PathAndQuery == "/Library/MediaFolders?UserId=user-1");
        Assert.Contains(observed, candidate => candidate.PathAndQuery == "/UserViews?UserId=user-1");
    }

    [Fact]
    public async Task SubsonicApiKey_ResolvesPrincipalBeforeRelaying()
    {
        var observedRequests = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            observedRequests.Add(Observe(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/rest/ping.view" => Json(
                    StatusCodes.Status200OK,
                    """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""),
                "/rest/tokenInfo.view" => Json(
                    StatusCodes.Status200OK,
                    """{"subsonic-response":{"status":"ok","version":"1.16.1","tokenInfo":{"username":"fixture-user"}}}"""),
                _ => Json(
                    StatusCodes.Status200OK,
                    """{"subsonic-response":{"status":"ok","version":"1.16.1","license":{"valid":true}}}""")
            };
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/getLicense.view?apiKey=valid-key&v=1.16.1&c=fixture&f=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            [
                "/rest/ping.view?apiKey=valid-key&v=1.16.1&c=fixture&f=json",
                "/rest/tokenInfo.view?apiKey=valid-key&v=1.16.1&c=fixture&f=json",
                "/rest/getLicense.view?apiKey=valid-key&v=1.16.1&c=fixture&f=json"
            ],
            observedRequests.Select(request => request.PathAndQuery).ToList());
    }

    [Theory]
    [InlineData(
        "GET",
        "/rest/getPlaylist?u=password-user&p=secret-pass&v=1.16.1&c=fixture&f=json&id=allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0",
        null,
        "password-user",
        "json",
        1)]
    [InlineData(
        "POST",
        "/rest/getPlaylist.view?v=1.16.1&c=fixture",
        "u=token-user&t=token-hash&s=token-salt&f=xml&id=allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0",
        "token-user",
        "xml",
        1)]
    [InlineData(
        "GET",
        "/rest/getPlaylist.view?apiKey=valid-key&v=1.16.1&c=fixture&f=json&id=allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0",
        null,
        "api-user",
        "json",
        2)]
    public async Task SubsonicGetPlaylist_CoversSuccessfulAuthAliasesMethodsFormatsAndNativeFields(
        string method,
        string path,
        string? formBody,
        string expectedPrincipal,
        string format,
        int expectedAuthCalls)
    {
        const string virtualId = "allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0";
        var model = new VirtualPlaylistReadModel(
            virtualId,
            Guid.Parse("0198a537-719c-7ea8-9e5a-17e1f2f963f0"),
            Guid.CreateVersion7(),
            "Native Target",
            "Target description",
            "playlist-cover",
            "spotify",
            "source-list",
            "revision-1",
            allstarr.Core.Storage.PlaylistLinkMode.Hybrid,
            [
                new(0, "native-b", "ignored", "ignored", null, null, 2_000, null,
                    allstarr.Core.Storage.TrackMatchState.Unresolved,
                    RouteKind: TrackRouteKind.Local,
                    NativeEntryJson: "{\"id\":\"native-b\",\"title\":\"Native B\",\"duration\":2,\"artistId\":\"artist-b\",\"albumId\":\"album-b\",\"coverArt\":\"cover-b\",\"provider\":\"navidrome\"}"),
                new(1, "native-a", "ignored", "ignored", null, null, 1_000, null,
                    allstarr.Core.Storage.TrackMatchState.Unresolved,
                    RouteKind: TrackRouteKind.Local,
                    NativeEntryJson: "{\"id\":\"native-a\",\"title\":\"Native A\",\"duration\":1,\"artistId\":\"artist-a\",\"albumId\":\"album-a\",\"coverArt\":\"cover-a\",\"provider\":\"navidrome\"}")
            ],
            allstarr.Core.Storage.PlaylistProjectionMode.Target,
            "backend-target");
        ProtocolExecutionContext? observedContext = null;
        var virtualization = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        virtualization.Setup(service => service.ReadAsync(
                It.IsAny<ProtocolExecutionContext>(),
                virtualId,
                It.IsAny<CancellationToken>()))
            .Callback<ProtocolExecutionContext, string, CancellationToken>((context, _, _) =>
                observedContext = context)
            .ReturnsAsync(model);
        var observedRequests = new List<ObservedRequest>();
        using var factory = new ProtocolFactory(
            "Subsonic",
            request =>
            {
                observedRequests.Add(Observe(request));
                return request.RequestUri!.AbsolutePath switch
                {
                    "/rest/ping.view" => Json(
                        StatusCodes.Status200OK,
                        """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""),
                    "/rest/tokenInfo.view" => Json(
                        StatusCodes.Status200OK,
                        """{"subsonic-response":{"status":"ok","version":"1.16.1","tokenInfo":{"username":"api-user"}}}"""),
                    _ => throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}")
                };
            },
            services =>
            {
                services.RemoveAll<IPlaylistVirtualizationService>();
                services.AddSingleton(virtualization.Object);
            });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = formBody == null
                ? null
                : new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded")
        };

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedPrincipal, observedContext?.VerifiedBackendPrincipalId);
        Assert.Equal(expectedAuthCalls, observedRequests.Count);
        Assert.All(observedRequests, item => Assert.True(
            item.PathAndQuery.StartsWith("/rest/ping.view?", StringComparison.Ordinal) ||
            item.PathAndQuery.StartsWith("/rest/tokenInfo.view?", StringComparison.Ordinal),
            item.PathAndQuery));
        Assert.DoesNotContain("secret-pass", body, StringComparison.Ordinal);
        Assert.DoesNotContain("token-hash", body, StringComparison.Ordinal);
        Assert.DoesNotContain("valid-key", body, StringComparison.Ordinal);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var playlist = document.RootElement.GetProperty("subsonic-response").GetProperty("playlist");
            var entries = playlist.GetProperty("entry");
            Assert.Equal(2, playlist.GetProperty("songCount").GetInt32());
            Assert.Equal(3, playlist.GetProperty("duration").GetInt64());
            Assert.Equal(["native-b", "native-a"],
                entries.EnumerateArray().Select(item => item.GetProperty("id").GetString()));
            Assert.Equal("artist-b", entries[0].GetProperty("artistId").GetString());
            Assert.Equal("album-b", entries[0].GetProperty("albumId").GetString());
            Assert.Equal("cover-b", entries[0].GetProperty("coverArt").GetString());
            Assert.Equal("navidrome", entries[0].GetProperty("provider").GetString());
        }
        else
        {
            var document = System.Xml.Linq.XDocument.Parse(body);
            var ns = document.Root!.Name.Namespace;
            var playlist = document.Descendants(ns + "playlist").Single();
            var entries = playlist.Elements(ns + "entry").ToArray();
            Assert.Equal("2", playlist.Attribute("songCount")!.Value);
            Assert.Equal("3", playlist.Attribute("duration")!.Value);
            Assert.Equal(["native-b", "native-a"], entries.Select(item => item.Attribute("id")!.Value));
            Assert.Equal("artist-b", entries[0].Attribute("artistId")!.Value);
            Assert.Equal("album-b", entries[0].Attribute("albumId")!.Value);
            Assert.Equal("cover-b", entries[0].Attribute("coverArt")!.Value);
            Assert.Equal("navidrome", entries[0].Attribute("provider")!.Value);
        }
        virtualization.VerifyAll();
    }

    [Theory]
    [InlineData(
        "GET",
        "/rest/getPlaylists?u=password-user&p=secret-pass&v=1.16.1&c=fixture&f=json",
        null,
        "json",
        "/rest/getPlaylists")]
    [InlineData(
        "POST",
        "/rest/getPlaylists.view?v=1.16.1&c=fixture",
        "u=token-user&t=token-hash&s=token-salt&v=1.16.1&c=fixture&f=xml",
        "xml",
        "/rest/getPlaylists.view")]
    public async Task SubsonicGetPlaylists_MergesNativeAndVirtualSummariesWithoutCredentialEcho(
        string method,
        string path,
        string? formBody,
        string format,
        string expectedRelayPath)
    {
        const string virtualId = "allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0";
        var model = new VirtualPlaylistReadModel(
            virtualId,
            Guid.Parse("0198a537-719c-7ea8-9e5a-17e1f2f963f0"),
            Guid.CreateVersion7(),
            "Virtual Mix",
            "Virtual comment",
            "virtual-cover",
            "spotify",
            "source-list",
            "revision-1",
            PlaylistLinkMode.Virtual,
            [new(0, "song-1", "Song", "Artist", "Album", null, 4_000, null, TrackMatchState.Accepted)]);
        var virtualization = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        virtualization.Setup(service => service.ListAsync(
                It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([model]);
        var observedRequests = new List<ObservedRequest>();
        using var factory = new ProtocolFactory(
            "Subsonic",
            request =>
            {
                var observed = Observe(request);
                observedRequests.Add(observed);
                if (request.RequestUri!.AbsolutePath == "/rest/ping.view")
                    return Json(StatusCodes.Status200OK,
                        """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
                if (request.RequestUri.AbsolutePath != expectedRelayPath)
                    throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
                return format == "json"
                    ? Json(StatusCodes.Status200OK,
                        """{"subsonic-response":{"status":"ok","version":"1.16.1","playlists":{"playlist":[{"id":"native-id","name":"Native","owner":"backend","public":true,"songCount":1,"duration":2,"coverArt":"native-cover","unknownField":"preserved"}]}}}""")
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """<subsonic-response status="ok" version="1.16.1"><playlists><playlist id="native-id" name="Native" owner="backend" public="true" songCount="1" duration="2" coverArt="native-cover" unknownField="preserved" /></playlists></subsonic-response>""",
                            Encoding.UTF8,
                            "application/xml")
                    };
            },
            services =>
            {
                services.RemoveAll<IPlaylistVirtualizationService>();
                services.AddSingleton(virtualization.Object);
            });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = formBody == null
                ? null
                : new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded")
        };

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, observedRequests.Count);
        Assert.Equal(expectedRelayPath, observedRequests[1].PathAndQuery.Split('?')[0]);
        Assert.DoesNotContain("secret-pass", body, StringComparison.Ordinal);
        Assert.DoesNotContain("token-hash", body, StringComparison.Ordinal);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var playlists = document.RootElement.GetProperty("subsonic-response")
                .GetProperty("playlists").GetProperty("playlist");
            Assert.Equal(["native-id", virtualId],
                playlists.EnumerateArray().Select(item => item.GetProperty("id").GetString()));
            Assert.Equal("preserved", playlists[0].GetProperty("unknownField").GetString());
            Assert.Equal(1, playlists[1].GetProperty("songCount").GetInt32());
            Assert.Equal(4, playlists[1].GetProperty("duration").GetInt64());
            Assert.Equal("Virtual comment", playlists[1].GetProperty("comment").GetString());
            Assert.Equal("allstarr", playlists[1].GetProperty("owner").GetString());
            Assert.False(playlists[1].GetProperty("public").GetBoolean());
            Assert.Equal("virtual-cover", playlists[1].GetProperty("coverArt").GetString());
        }
        else
        {
            var document = XDocument.Parse(body);
            var ns = document.Root!.Name.Namespace;
            var playlists = document.Descendants(ns + "playlist").ToArray();
            Assert.Equal(["native-id", virtualId], playlists.Select(item => item.Attribute("id")!.Value));
            Assert.Equal("preserved", playlists[0].Attribute("unknownField")!.Value);
            Assert.Equal("1", playlists[1].Attribute("songCount")!.Value);
            Assert.Equal("4", playlists[1].Attribute("duration")!.Value);
            Assert.Equal("Virtual comment", playlists[1].Attribute("comment")!.Value);
            Assert.Equal("allstarr", playlists[1].Attribute("owner")!.Value);
            Assert.Equal("false", playlists[1].Attribute("public")!.Value);
            Assert.Equal("virtual-cover", playlists[1].Attribute("coverArt")!.Value);
        }
        virtualization.VerifyAll();
    }

    [Fact]
    public async Task SubsonicStructuredLyrics_CoversExternalAndRelayGetPostXmlJsonFixtures()
    {
        using var fixtures = ReadFixture("subsonic-structured-lyrics.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var observedRequests = new List<ObservedRequest>();
            var external = fixture.GetProperty("external").GetBoolean();
            var lookup = new Mock<ISubsonicLyricsLookup>(MockBehavior.Strict);
            if (external)
            {
                var expectedLookup = fixture.GetProperty("lookup");
                lookup.Setup(service => service.FindAsync(
                        It.IsAny<ProtocolExecutionContext>(),
                        expectedLookup.GetProperty("provider").GetString()!,
                        expectedLookup.GetProperty("externalId").GetString()!,
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new SubsonicStructuredLyrics(
                        "Fixture Artist",
                        "Fixture Title",
                        "eng",
                        0,
                        true,
                        [
                            new SubsonicLyricLine(1_000, "first"),
                            new SubsonicLyricLine(2_000, "second")
                        ]));
            }

            using var factory = new ProtocolFactory(
                "Subsonic",
                request =>
                {
                    observedRequests.Add(Observe(request));
                    if (observedRequests.Count == 1)
                    {
                        return Json(
                            StatusCodes.Status200OK,
                            """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
                    }

                    var relay = fixture.GetProperty("relay");
                    return new HttpResponseMessage((HttpStatusCode)relay.GetProperty("status").GetInt32())
                    {
                        Content = new StringContent(
                            relay.GetProperty("responseBody").GetString()!,
                            Encoding.UTF8,
                            relay.GetProperty("contentType").GetString()!)
                    };
                },
                services =>
                {
                    services.RemoveAll<ISubsonicLyricsLookup>();
                    services.AddSingleton(lookup.Object);
                });
            using var client = factory.CreateClient();
            using var request = CreateFixtureRequest(fixture.GetProperty("request"));

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            AssertObservedRequest(fixture.GetProperty("verification"), observedRequests[0]);
            if (external)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Single(observedRequests);
                AssertStructuredLyrics(body, fixture.GetProperty("format").GetString()!);
                lookup.VerifyAll();
            }
            else
            {
                var relay = fixture.GetProperty("relay");
                Assert.Equal(relay.GetProperty("status").GetInt32(), (int)response.StatusCode);
                Assert.Equal(relay.GetProperty("responseBody").GetString(), body);
                Assert.Equal(2, observedRequests.Count);
                AssertObservedRequest(relay, observedRequests[1]);
                lookup.VerifyNoOtherCalls();
            }
        }
    }

    [Fact]
    public async Task SubsonicSearch3_AppliesIndependentExternalWindowsForGetPostJsonXml()
    {
        using var fixtures = ReadFixture("subsonic-search-windows.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var observedRequests = new List<ObservedRequest>();
            var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
            gateway.Setup(service => service.SearchAsync(
                    It.IsAny<ProtocolExecutionContext>(),
                    "window",
                    2,
                    2,
                    2))
                .ReturnsAsync(new SearchResult
                {
                    Songs = [
                        new() { Id = "song-1", Title = "one", ExternalProvider = "deezer" },
                        new() { Id = "song-2", Title = "two", ExternalProvider = "deezer" }
                    ],
                    Albums = [
                        new() { Id = "album-1", Title = "one", ExternalProvider = "deezer" },
                        new() { Id = "album-2", Title = "two", ExternalProvider = "deezer" }
                    ],
                    Artists = [
                        new() { Id = "artist-1", Name = "one", ExternalProvider = "deezer" },
                        new() { Id = "artist-2", Name = "two", ExternalProvider = "deezer" }
                    ]
                });
            using var factory = new ProtocolFactory(
                "Subsonic",
                request =>
                {
                    observedRequests.Add(Observe(request));
                    if (request.RequestUri!.AbsolutePath is "/rest/ping.view")
                    {
                        return Json(
                            StatusCodes.Status200OK,
                            """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
                    }

                    var xml = fixture.GetProperty("format").GetString() == "xml";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            xml
                                ? """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1"><searchResult3 /></subsonic-response>"""
                                : """{"subsonic-response":{"status":"ok","version":"1.16.1","searchResult3":{}}}""",
                            Encoding.UTF8,
                            xml ? "application/xml" : "application/json")
                    };
                },
                services =>
                {
                    services.RemoveAll<IProtocolProviderGateway>();
                    services.AddSingleton(gateway.Object);
                });
            using var client = factory.CreateClient();
            using var request = CreateFixtureRequest(fixture.GetProperty("request"));

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{fixture.GetProperty("name").GetString()} returned {(int)response.StatusCode}: {body}");
            Assert.Equal(2, observedRequests.Count);
            Assert.Equal(fixture.GetProperty("expectedMethod").GetString(), observedRequests[1].Method);
            if (fixture.GetProperty("format").GetString() == "json")
            {
                using var document = JsonDocument.Parse(body);
                var search = document.RootElement.GetProperty("subsonic-response").GetProperty("searchResult3");
                Assert.Equal(fixture.GetProperty("expectedSong").GetString(), search.GetProperty("song")[0].GetProperty("id").GetString());
                Assert.Equal(fixture.GetProperty("expectedAlbum").GetString(), search.GetProperty("album")[0].GetProperty("id").GetString());
                Assert.Equal(fixture.GetProperty("expectedArtist").GetString(), search.GetProperty("artist")[0].GetProperty("id").GetString());
            }
            else
            {
                var document = System.Xml.Linq.XDocument.Parse(body);
                var search = document.Root!.Elements().Single();
                Assert.Equal(fixture.GetProperty("expectedSong").GetString(), search.Elements().Single(element => element.Name.LocalName == "song").Attribute("id")?.Value);
                Assert.Equal(fixture.GetProperty("expectedAlbum").GetString(), search.Elements().Single(element => element.Name.LocalName == "album").Attribute("id")?.Value);
                Assert.Equal(fixture.GetProperty("expectedArtist").GetString(), search.Elements().Single(element => element.Name.LocalName == "artist").Attribute("id")?.Value);
            }

            gateway.VerifyAll();
        }
    }

    [Fact]
    public async Task SubsonicRelay_PreservesConditionalRequestsResponseHeadersAndErrors()
    {
        using var fixtures = ReadFixture("subsonic-item-relay.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var observed = new List<ObservedRequest>();
            string? conditional = null;
            using var factory = new ProtocolFactory("Subsonic", request =>
            {
                observed.Add(Observe(request));
                if (request.RequestUri!.AbsolutePath is "/rest/ping.view")
                {
                    return Json(
                        StatusCodes.Status200OK,
                        """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
                }

                conditional = request.Headers.TryGetValues("If-None-Match", out var values)
                    ? values.Single()
                    : null;
                var status = fixture.GetProperty("status").GetInt32();
                var response = new HttpResponseMessage((HttpStatusCode)status)
                {
                    Content = status == StatusCodes.Status304NotModified
                        ? new ByteArrayContent([])
                        : new StringContent(
                            fixture.GetProperty("body").GetString()!,
                            Encoding.UTF8,
                            fixture.GetProperty("contentType").GetString()!)
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"cover-v2\"");
                response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                response.Headers.TryAddWithoutValidation("X-Subsonic-Revision", "fixture-r2");
                return response;
            });
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                fixture.GetProperty("requestPath").GetString());
            request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"cover-v1\""));

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            Assert.Equal(fixture.GetProperty("status").GetInt32(), (int)response.StatusCode);
            Assert.Equal(fixture.GetProperty("body").GetString(), responseBody);
            Assert.Equal("\"cover-v1\"", conditional);
            Assert.Equal("\"cover-v2\"", response.Headers.ETag?.Tag);
            Assert.True(response.Headers.CacheControl?.NoCache);
            Assert.Equal("fixture-r2", response.Headers.GetValues("X-Subsonic-Revision").Single());
            Assert.Equal(2, observed.Count);
            Assert.Equal(fixture.GetProperty("upstreamPath").GetString(), observed[1].PathAndQuery);
        }
    }

    [Fact]
    public async Task StreamingAdapters_PreserveGetHeadRangeValidatorsStatusAndHeaders()
    {
        using var fixtures = ReadFixture("protocol-streaming-ranges.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var observedRequests = new List<(string Method, string PathAndQuery, string? Range, string? IfRange)>();
            using var factory = new ProtocolFactory(
                fixture.GetProperty("protocol").GetString()!,
                request =>
                {
                    observedRequests.Add((
                        request.Method.Method,
                        request.RequestUri!.PathAndQuery,
                        request.Headers.TryGetValues("Range", out var ranges) ? ranges.Single() : null,
                        request.Headers.TryGetValues("If-Range", out var validators) ? validators.Single() : null));

                    if (request.RequestUri.AbsolutePath is "/Users/Me")
                    {
                        if (fixture.TryGetProperty("verificationFallbackPath", out _))
                        {
                            return Json(
                                StatusCodes.Status400BadRequest,
                                """{"error":"API key has no current user"}""");
                        }
                        return Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""");
                    }

                    if (request.RequestUri.AbsolutePath is "/System/Info")
                    {
                        return Json(StatusCodes.Status200OK, """{"Id":"server-1","Version":"10.11.11"}""");
                    }

                    if (IsItemLookup(request, "local-song"))
                    {
                        return ItemLookup("""{"Id":"local-song","Type":"Audio"}""");
                    }

                    if (request.RequestUri.AbsolutePath is "/rest/ping.view")
                    {
                        return Json(
                            StatusCodes.Status200OK,
                            """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
                    }

                    var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                    {
                        Content = new ByteArrayContent([8, 9, 10, 11, 12, 13, 14, 15])
                    };
                    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/flac");
                    response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(8, 15, 32);
                    response.Headers.AcceptRanges.Add("bytes");
                    response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"fixture-etag\"");
                    return response;
                });
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(
                new HttpMethod(fixture.GetProperty("method").GetString()!),
                fixture.GetProperty("path").GetString());
            request.Headers.TryAddWithoutValidation("Range", fixture.GetProperty("range").GetString());
            request.Headers.TryAddWithoutValidation("If-Range", fixture.GetProperty("ifRange").GetString());

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("audio/flac", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
            Assert.Equal("\"fixture-etag\"", response.Headers.ETag?.Tag);
            Assert.Equal("bytes 8-15/32", response.Content.Headers.ContentRange?.ToString());
            var streamIndex = fixture.TryGetProperty("verificationFallbackPath", out _)
                ? 3
                : fixture.GetProperty("protocol").GetString() == "jellyfin" ? 2 : 1;
            Assert.Equal(streamIndex + 1, observedRequests.Count);
            if (streamIndex == 2)
                Assert.Equal("/Items?ids=local-song&limit=1", observedRequests[0].PathAndQuery);
            if (streamIndex == 3)
            {
                Assert.Equal("/Items?ids=local-song&limit=1", observedRequests[0].PathAndQuery);
                Assert.Equal(fixture.GetProperty("verificationPath").GetString(), observedRequests[1].PathAndQuery);
                Assert.Equal(
                    fixture.GetProperty("verificationFallbackPath").GetString(),
                    observedRequests[2].PathAndQuery);
            }
            else
            {
                Assert.Equal(
                    fixture.GetProperty("verificationPath").GetString(),
                    observedRequests[streamIndex - 1].PathAndQuery);
            }
            Assert.Equal(
                fixture.TryGetProperty("upstreamMethod", out var upstreamMethod)
                    ? upstreamMethod.GetString()
                    : fixture.GetProperty("method").GetString(),
                observedRequests[streamIndex].Method);
            Assert.Equal(fixture.GetProperty("streamPath").GetString(), observedRequests[streamIndex].PathAndQuery);
            Assert.Equal(fixture.GetProperty("range").GetString(), observedRequests[streamIndex].Range);
            Assert.Equal(fixture.GetProperty("ifRange").GetString(), observedRequests[streamIndex].IfRange);
        }
    }

    [Fact]
    public async Task JellyfinUnboundFileApiKeyFallback_RejectsInvalidCredentialBeforeRelay()
    {
        var observed = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observed.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath switch
            {
                "/Items" when IsItemLookup(request, "local-song") =>
                    ItemLookup("""{"Id":"local-song","Type":"Audio"}"""),
                "/Users/Me" => Json(StatusCodes.Status400BadRequest, """{"error":"API key has no current user"}"""),
                "/System/Info" => Json(StatusCodes.Status401Unauthorized, """{"error":"invalid token"}"""),
                _ => throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}")
            };
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/Items/local-song/File?ApiKey=invalid-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            [
                "/Items?ids=local-song&limit=1",
                "/Users/Me?ApiKey=invalid-key",
                "/System/Info?ApiKey=invalid-key"
            ],
            observed);
    }

    [Fact]
    public async Task JellyfinGenericRelay_PreservesMethodQueryBodyStatusHeadersAndMediaType()
    {
        using var fixtures = ReadFixture("jellyfin-generic-relay.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var observed = new List<ObservedRequest>();
            using var factory = new ProtocolFactory("Jellyfin", request =>
            {
                observed.Add(Observe(request));
                if (request.RequestUri!.AbsolutePath == "/Users/Me")
                {
                    return Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""");
                }

                var upstream = fixture.GetProperty("upstream");
                var response = new HttpResponseMessage(
                    (HttpStatusCode)upstream.GetProperty("status").GetInt32())
                {
                    Content = new StringContent(
                        upstream.GetProperty("body").GetString()!,
                        Encoding.UTF8,
                        upstream.GetProperty("contentType").GetString()!)
                };
                if (upstream.TryGetProperty("etag", out var etag))
                {
                    response.Headers.ETag =
                        new System.Net.Http.Headers.EntityTagHeaderValue(etag.GetString()!);
                }

                if (upstream.TryGetProperty("location", out var location))
                {
                    response.Headers.Location = new Uri(location.GetString()!, UriKind.Relative);
                }

                return response;
            });
            using var client = factory.CreateClient();
            using var request = CreateFixtureRequest(fixture.GetProperty("request"));

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            var upstreamFixture = fixture.GetProperty("upstream");

            if (fixture.TryGetProperty("blocked", out var blocked) && blocked.GetBoolean())
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Empty(observed);
                continue;
            }

            Assert.Equal(upstreamFixture.GetProperty("status").GetInt32(), (int)response.StatusCode);
            Assert.Equal(
                upstreamFixture.GetProperty("contentType").GetString(),
                response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(fixture.GetProperty("expectedBody").GetString(), body);
            Assert.Equal(2, observed.Count);
            Assert.Equal(fixture.GetProperty("verificationPath").GetString(), observed[0].PathAndQuery);
            Assert.Equal(
                fixture.GetProperty("request").GetProperty("method").GetString(),
                observed[1].Method);
            Assert.Equal(fixture.GetProperty("upstreamPath").GetString(), observed[1].PathAndQuery);
            var requestFixture = fixture.GetProperty("request");
            var requestMethod = requestFixture.GetProperty("method").GetString();
            Assert.Equal(
                requestFixture.TryGetProperty("body", out var expectedRequestBody)
                    ? expectedRequestBody.GetString()
                    : requestMethod is "GET" or "HEAD" ? null : string.Empty,
                observed[1].Body);
            if (upstreamFixture.TryGetProperty("etag", out var expectedEtag))
            {
                Assert.Equal(expectedEtag.GetString(), response.Headers.ETag?.Tag);
            }

            if (upstreamFixture.TryGetProperty("location", out var expectedLocation))
            {
                Assert.Equal(expectedLocation.GetString(), response.Headers.Location?.ToString());
            }
        }
    }

    [Theory]
    [InlineData("Jellyfin", "/Audio/local-song/stream?api_key=fixture-key")]
    [InlineData("Subsonic", "/rest/stream.view?u=fixture&p=fixture-password&v=1.16.1&c=fixture&id=local-song")]
    public async Task StreamingAdapters_PreserveUpstreamRangeErrors(
        string protocol,
        string path)
    {
        using var factory = new ProtocolFactory(protocol, request =>
        {
            if (request.RequestUri!.AbsolutePath is "/Users/Me")
            {
                return Json(StatusCodes.Status200OK, """{"Id":"user-1"}""");
            }

            if (IsItemLookup(request, "local-song"))
            {
                return ItemLookup("""{"Id":"local-song","Type":"Audio"}""");
            }

            if (request.RequestUri.AbsolutePath is "/rest/ping.view")
            {
                return Json(
                    StatusCodes.Status200OK,
                    """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(99, 100);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    private static void AssertStructuredLyrics(string body, string format)
    {
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var structured = document.RootElement.GetProperty("subsonic-response")
                .GetProperty("lyricsList").GetProperty("structuredLyrics")[0];
            Assert.Equal("Fixture Artist", structured.GetProperty("displayArtist").GetString());
            Assert.Equal(1_000, structured.GetProperty("line")[0].GetProperty("start").GetInt64());
            return;
        }

        var documentXml = System.Xml.Linq.XDocument.Parse(body);
        System.Xml.Linq.XNamespace ns = "http://subsonic.org/restapi";
        var structuredXml = documentXml.Root!.Element(ns + "lyricsList")!
            .Element(ns + "structuredLyrics")!;
        Assert.Equal("Fixture Artist", structuredXml.Attribute("displayArtist")?.Value);
        Assert.Equal("1000", structuredXml.Elements(ns + "line").First().Attribute("start")?.Value);
    }

    private static HttpRequestMessage CreateFixtureRequest(JsonElement fixture)
    {
        var request = new HttpRequestMessage(
            new HttpMethod(fixture.GetProperty("method").GetString()!),
            fixture.GetProperty("path").GetString());
        if (fixture.TryGetProperty("body", out var body))
        {
            request.Content = new StringContent(
                body.GetString()!,
                Encoding.UTF8,
                fixture.GetProperty("contentType").GetString());
        }

        return request;
    }

    private static ObservedRequest Observe(HttpRequestMessage request) => new(
        request.Method.Method,
        request.RequestUri!.PathAndQuery,
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult());

    private static void AssertObservedRequest(JsonElement expected, ObservedRequest actual)
    {
        Assert.Equal(expected.GetProperty("method").GetString(), actual.Method);
        Assert.Equal(expected.GetProperty("pathAndQuery").GetString(), actual.PathAndQuery);
        Assert.Equal(
            expected.TryGetProperty("body", out var body)
                ? body.GetString()
                : expected.TryGetProperty("requestBody", out var requestBody)
                    ? requestBody.GetString()
                    : null,
            actual.Body);
    }

    private static HttpResponseMessage FixtureResponse(JsonElement fixture)
    {
        var content = fixture.TryGetProperty("responseBody", out var body)
            ? body.GetRawText()
            : fixture.GetProperty("rawBody").GetString()!;
        return new HttpResponseMessage((HttpStatusCode)fixture.GetProperty("status").GetInt32())
        {
            Content = new StringContent(
                content,
                Encoding.UTF8,
                fixture.GetProperty("contentType").GetString()!)
        };
    }

    private static string CanonicalJson(JsonElement value) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<object>(value.GetRawText()));

    private sealed record ObservedRequest(string Method, string PathAndQuery, string? Body);

    private sealed class FixedPlaylistMutationResolver(SubsonicPlaylistMutationRoute? route)
        : ISubsonicPlaylistMutationResolver
    {
        public Task<SubsonicPlaylistMutationRoute?> ResolveAsync(
            ProtocolExecutionContext context,
            string protocolId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(route);
        }
    }

    private sealed class FixedJellyfinPlaylistMutationResolver(JellyfinPlaylistMutationRoute? route)
        : IJellyfinPlaylistMutationResolver
    {
        public Task<JellyfinPlaylistMutationRoute?> ResolveAsync(
            ProtocolExecutionContext context,
            string protocolId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(route);
    }

    private sealed class RecordingInteractionAdapter : IJellyfinInteractionProtocolAdapter
    {
        private readonly JellyfinInteractionProtocolAdapter _inner = new();

        public ProtocolExecutionContext? LastContext { get; private set; }

        public bool CanRunOptionalUserWork(ProtocolExecutionContext? context)
        {
            LastContext = context;
            return _inner.CanRunOptionalUserWork(context);
        }

        public JellyfinProtocolResponse ShapeFavorite(string itemId, bool isFavorite) =>
            _inner.ShapeFavorite(itemId, isFavorite);

        public int ShapeCapabilitiesStatus(int upstreamStatusCode) =>
            _inner.ShapeCapabilitiesStatus(upstreamStatusCode);

        public JellyfinProtocolResponse ShapeInstantMix(IReadOnlyList<Dictionary<string, object?>> items) =>
            _inner.ShapeInstantMix(items);
    }

    private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static bool IsItemLookup(HttpRequestMessage request, string itemId) =>
        request.RequestUri!.AbsolutePath == "/Items" &&
        QueryHelpers.ParseQuery(request.RequestUri.Query)["ids"] == itemId;

    private static HttpResponseMessage ItemLookup(string item) =>
        Json(
            StatusCodes.Status200OK,
            $$"""{"Items":[{{item}}],"TotalRecordCount":1,"StartIndex":0}""");

    private static RecommendationSourceItem SonicTrack(string id, double score, string title) => new(
        id,
        score,
        [new RecommendationSignal("sonic", score, "Similar sound")],
        new RecommendationTrackIdentity(Title: title, Artist: "Fixture Artist", BackendItemId: id));

    private static Action<IServiceCollection> SonicServices(
        IAudioMuseRecommendationClient audioMuse,
        bool selected = true,
        string? otherLibraryItem = null)
    {
        var scopes = new Mock<IProtocolLibraryScopeResolver>(MockBehavior.Strict);
        scopes.Setup(service => service.ResolveAsync(
                It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtocolExecutionContext context, string itemId, CancellationToken _) =>
                context.WithLibraryScope(itemId == otherLibraryItem ? "other" : "music"));
        var policies = new Mock<IIntelligencePolicyService>(MockBehavior.Strict);
        policies.Setup(service => service.GetAsync(
                It.IsAny<IntelligenceScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntelligencePolicyRecord
            {
                Enabled = true,
                EnabledProvidersJson = selected ? "[\"audiomuse-ai\"]" : "[]"
            });
        return services =>
        {
            services.AddSingleton<IStartupFilter, SonicPrincipalStartupFilter>();
            services.RemoveAll<IAudioMuseRecommendationClient>();
            services.AddSingleton(audioMuse);
            services.RemoveAll<IProtocolLibraryScopeResolver>();
            services.AddSingleton(scopes.Object);
            services.RemoveAll<IIntelligencePolicyService>();
            services.AddSingleton(policies.Object);
        };
    }

    private sealed class SonicPrincipalStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuePipeline) =>
            {
                var subsonic = context.Request.Path.StartsWithSegments("/rest");
                context.Items[BackendIdentityResolver.HttpContextPrincipalItemKey] = new AllstarrPrincipal(
                    Guid.Parse("018f1f6e-7db7-7ab0-8b32-f26f12ff6d6a"),
                    Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd"),
                    subsonic ? "subsonic" : "jellyfin",
                    "primary",
                    subsonic ? "fixture" : "user-1",
                    "Fixture User",
                    false);
                await continuePipeline();
            });
            next(app);
        };
    }

    private static HttpResponseMessage SubsonicSonicBackend(HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath == "/rest/ping.view")
            return Json(StatusCodes.Status200OK,
                """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
        if (request.RequestUri.AbsolutePath != "/rest/getSong.view")
            throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");

        var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
        var id = query["id"].ToString();
        var duration = id switch { "song-2" => 202, "song-1" => 101, "bridge" => 150, _ => 100 };
        if (query["f"] == "json")
            return Json(StatusCodes.Status200OK,
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["subsonic-response"] = new
                    {
                        status = "ok",
                        version = "1.16.1",
                        song = new { id, title = id, artist = "Fixture Artist", isDir = false, type = "music", duration }
                    }
                }));

        XNamespace ns = "http://subsonic.org/restapi";
        var xml = new XDocument(new XElement(ns + "subsonic-response",
            new XAttribute("status", "ok"),
            new XAttribute("version", "1.16.1"),
            new XElement(ns + "song",
                new XAttribute("id", id),
                new XAttribute("title", id),
                new XAttribute("artist", "Fixture Artist"),
                new XAttribute("isDir", "false"),
                new XAttribute("type", "music"),
                new XAttribute("duration", duration))));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml")
        };
    }

    private static JsonDocument ReadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Protocols", fileName);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private sealed class ProtocolFactory : WebApplicationFactory<Program>
    {
        private readonly string _backend;
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly Action<IServiceCollection>? _configureServices;
        private readonly IReadOnlyDictionary<string, string?>? _configuration;
        private readonly string _stateRoot = Path.Combine(
            Path.GetTempPath(),
            "allstarr-tests",
            Guid.NewGuid().ToString("N"));

        public ProtocolFactory(
            string backend,
            Func<HttpRequestMessage, HttpResponseMessage> responder,
            Action<IServiceCollection>? configureServices = null,
            IReadOnlyDictionary<string, string?>? configuration = null)
        {
            _backend = backend;
            _responder = responder;
            _configureServices = configureServices;
            _configuration = configuration;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Backend:Type", _backend);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Backend:Type"] = _backend,
                    ["SpotifyApi:Enabled"] = "false",
                    ["SpotifyImport:Enabled"] = "false",
                    ["Storage:EnforceMutationGuard"] = "false",
                    ["Extensions:Directory"] = Path.Combine(_stateRoot, "extensions"),
                    ["Cache:GenreDirectory"] = Path.Combine(_stateRoot, "genres"),
                    ["MULTI_PROVIDER_DISABLED_PROVIDERS"] = "applemusic,deezer,qobuz,squidwtf,spotify"
                });
                if (_configuration != null) configuration.AddInMemoryCollection(_configuration);
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IHttpClientFactory>();
                services.RemoveAll<IApplicationCache>();
                services.AddSingleton<IApplicationCache, TestMemoryApplicationCache>();
                services.AddSingleton<IHttpClientFactory>(
                    new StubHttpClientFactory(new StubHttpMessageHandler(_responder)));
                _configureServices?.Invoke(services);
                if (!services.Any(service => service.ServiceType == typeof(IProtocolProviderGateway)))
                {
                    services.RemoveAll<IProtocolLyricsResolver>();
                    services.AddSingleton(
                        new Mock<IProtocolLyricsResolver>(MockBehavior.Strict).Object);
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_stateRoot))
            {
                Directory.Delete(_stateRoot, recursive: true);
            }
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
