using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Models.Search;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Playlists;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolRouteFixtureTests
{
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
    public async Task JellyfinSearchAdapter_PreservesFixtureStatusBodyAndPaging()
    {
        using var fixture = ReadFixture("jellyfin-search-shaping.json");
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        metadata
            .Setup(service => service.SearchAllAsync(
                "fixture",
                3,
                0,
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult());
        metadata
            .Setup(service => service.SearchPlaylistsAsync(
                "fixture",
                3,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        using var factory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath.Equals("/Users/Me", StringComparison.Ordinal)
                ? Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""")
                : Json(StatusCodes.Status200OK, fixture.RootElement.GetProperty("upstream").GetProperty("body").GetRawText()),
            services =>
            {
                services.RemoveAll<ParallelMetadataService>();
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
            3,
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
                services.RemoveAll<ParallelMetadataService>();
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
                services.RemoveAll<ParallelMetadataService>();
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

                    if (request.RequestUri.AbsolutePath.StartsWith("/Items/", StringComparison.Ordinal) &&
                        fixture.TryGetProperty("itemBody", out var itemBody))
                    {
                        return Json(StatusCodes.Status200OK, itemBody.GetRawText());
                    }

                    throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
                },
                services =>
                {
                    services.RemoveAll<ParallelMetadataService>();
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
                Assert.Equal(["/Users/Me", "/Audio/local-song/Lyrics"], observedPaths);
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
                if (request.RequestUri.AbsolutePath.Equals("/Users/Me", StringComparison.Ordinal))
                {
                    return Json(StatusCodes.Status200OK, """{"Id":"verified-user","Name":"Fixture User"}""");
                }

                Assert.Equal(upstream.GetProperty("pathAndQuery").GetString(), request.RequestUri.PathAndQuery);
                return Json(upstream.GetProperty("status").GetInt32(), upstream.GetProperty("body").GetRawText());
            },
            services =>
            {
                services.RemoveAll<ParallelMetadataService>();
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
    public async Task JellyfinInstantMix_PreservesAllSixPinnedRouteClassesAndBackendResponses()
    {
        using var fixtures = ReadFixture("jellyfin-instant-mix-paths.json");
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var observedPaths = new List<string>();
            using var factory = new ProtocolFactory("Jellyfin", request =>
            {
                observedPaths.Add(request.RequestUri!.PathAndQuery);
                return request.RequestUri.AbsolutePath.Equals("/Users/Me", StringComparison.Ordinal)
                    ? Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""")
                    : Json(fixture.GetProperty("status").GetInt32(), fixture.GetProperty("body").GetRawText());
            });
            using var client = factory.CreateClient();
            var path = fixture.GetProperty("path").GetString()!;

            using var response = await client.GetAsync($"{path}?api_key=fixture-key&Limit=2");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(fixture.GetProperty("status").GetInt32(), (int)response.StatusCode);
            Assert.Equal(
                CanonicalJson(fixture.GetProperty("body")),
                CanonicalJson(JsonDocument.Parse(body).RootElement));
            Assert.Equal(
                ["/Users/Me?api_key=fixture-key", $"{path}?api_key=fixture-key&Limit=2"],
                observedPaths);
        }

        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        using var unresolvedFactory = new ProtocolFactory(
            "Jellyfin",
            request => request.RequestUri!.AbsolutePath.Equals("/Users/Me", StringComparison.Ordinal)
                ? Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<ParallelMetadataService>();
                services.RemoveAll<IMusicMetadataService>();
                services.AddSingleton(metadata.Object);
            });
        using var unresolvedClient = unresolvedFactory.CreateClient();
        using var unresolvedResponse = await unresolvedClient.GetAsync(
            "/Songs/ext-deezer-song-42/InstantMix?api_key=fixture-key&userId=spoofed-route-user");
        Assert.Equal(HttpStatusCode.OK, unresolvedResponse.StatusCode);
        Assert.Equal(
            "{\"Items\":[],\"TotalRecordCount\":0}",
            await unresolvedResponse.Content.ReadAsStringAsync());
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
            gateway.Setup(service => service.SearchPlaylistsAsync(
                    It.IsAny<ProtocolExecutionContext>(),
                    "window",
                    2))
                .ReturnsAsync([]);

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
                        return Json(StatusCodes.Status200OK, """{"Id":"user-1","Name":"Fixture User"}""");
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
            Assert.Equal(2, observedRequests.Count);
            Assert.Equal(fixture.GetProperty("verificationPath").GetString(), observedRequests[0].PathAndQuery);
            Assert.Equal(fixture.GetProperty("method").GetString(), observedRequests[1].Method);
            Assert.Equal(fixture.GetProperty("streamPath").GetString(), observedRequests[1].PathAndQuery);
            Assert.Equal(fixture.GetProperty("range").GetString(), observedRequests[1].Range);
            Assert.Equal(fixture.GetProperty("ifRange").GetString(), observedRequests[1].IfRange);
        }
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
            Assert.Equal(
                fixture.GetProperty("request").TryGetProperty("body", out var expectedRequestBody)
                    ? expectedRequestBody.GetString()
                    : null,
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
        private readonly string _stateRoot = Path.Combine(
            Path.GetTempPath(),
            "allstarr-tests",
            Guid.NewGuid().ToString("N"));

        public ProtocolFactory(
            string backend,
            Func<HttpRequestMessage, HttpResponseMessage> responder,
            Action<IServiceCollection>? configureServices = null)
        {
            _backend = backend;
            _responder = responder;
            _configureServices = configureServices;
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
                    ["Redis:Enabled"] = "false",
                    ["SpotifyApi:Enabled"] = "false",
                    ["SpotifyImport:Enabled"] = "false",
                    ["Storage:EnforceMutationGuard"] = "false",
                    ["Extensions:Directory"] = Path.Combine(_stateRoot, "extensions"),
                    ["Admin:SessionStorePath"] = Path.Combine(_stateRoot, "sessions.protected"),
                    ["Cache:GenreDirectory"] = Path.Combine(_stateRoot, "genres"),
                    ["MULTI_PROVIDER_DISABLED_PROVIDERS"] = "applemusic,deezer,qobuz,squidwtf,spotify"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(
                    new StubHttpClientFactory(new StubHttpMessageHandler(_responder)));
                _configureServices?.Invoke(services);
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
