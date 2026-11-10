using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Controllers.ClientApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_DashboardPrefixCannotExposeClientRoutes(bool enabled)
    {
        var upstream = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            upstream.Add(request.RequestUri!.AbsolutePath);
            return Json(200, "{}");
        }, services => services.AddSingleton<IStartupFilter, ClientApiPortStartupFilter>(),
            new Dictionary<string, string?> { ["Admin:BasePath"] = "/dashboard" });
        using var client = CreateClientApiClient(factory, enabled: enabled);
        foreach (var path in new[] { "capabilities", "openapi.json", "does-not-exist" })
        {
            using var response = await client.GetAsync($"http://localhost:5275/dashboard/allstarr/v1/{path}");
            await AssertClientApiError(response, 404, "not_found");
        }
        Assert.Empty(upstream);
    }

    [Theory]
    [InlineData("/allstarr/v1/capabilities", 200)]
    [InlineData("/allstarr/v1/openapi.json", 200)]
    [InlineData("/allstarr/v1/does-not-exist", 404)]
    public async Task ClientApi_DefaultOffAndLiveSettingControlTheWholeNamespace(string path, int enabledStatus)
    {
        var upstream = new List<string>();
        using var factory = ClientApiFactory(request =>
        {
            upstream.Add(request.RequestUri!.AbsolutePath);
            return Json(200, """{"Id":"listener"}""");
        });
        using var client = CreateClientApiClient(factory, enabled: false);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var initial = await factory.Services.GetRequiredService<IDurableRuntimeSettings>().GetAsync("ClientApi:Enabled");
        Assert.False(Assert.IsType<bool>(initial.Value));
        Assert.Equal(RuntimeSettingOrigin.Bootstrap, initial.Origin);
        using var disabled = await client.GetAsync(path);
        await AssertClientApiError(disabled, 404, "not_found");
        Assert.Empty(upstream);
        await SetClientApiEnabledAsync(factory, true);
        using var enabled = await client.GetAsync(path);
        Assert.Equal((HttpStatusCode)enabledStatus, enabled.StatusCode);
        var count = upstream.Count;
        Assert.Equal(path.EndsWith("capabilities", StringComparison.Ordinal) ? 1 : 0, count);
        await SetClientApiEnabledAsync(factory, false);
        using var disabledAgain = await client.GetAsync(path);
        await AssertClientApiError(disabledAgain, 404, "not_found");
        Assert.Equal(count, upstream.Count);
    }

    [Fact]
    public async Task ClientApi_PreflightOptInIsLiveAndDefaultsOff()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory, enabled: false);
        foreach (var enabled in new[] { false, true, false })
        {
            if (enabled || (await factory.Services.GetRequiredService<IDurableRuntimeSettings>().GetAsync("ClientApi:Enabled")).Origin == RuntimeSettingOrigin.Durable)
                await SetClientApiEnabledAsync(factory, enabled);
            using var request = new HttpRequestMessage(HttpMethod.Options, "/Items?IncludeItemTypes=Audio");
            request.Headers.Add("Origin", "https://client.example");
            request.Headers.Add("Access-Control-Request-Method", "GET");
            request.Headers.Add("Access-Control-Request-Headers", "X-Allstarr-Title-Markers");
            using var response = await client.SendAsync(request);
            Assert.Equal(enabled ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(enabled, response.Headers.Contains("Access-Control-Allow-Headers"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_CapabilitiesUsesListenerScopeAndCamelCase(bool administrator)
    {
        ProtocolExecutionContext? observed = null;
        using var factory = ClientApiFactory(request => Json(200,
            JsonSerializer.Serialize(new { Id = "listener", Policy = new { IsAdministrator = administrator } })),
            services => services.Configure<MvcOptions>(options => options.Filters.Add(
                new ClientApiObservationFilter(context => observed = context))));
        using var client = CreateClientApiClient(factory);
        using var fixtures = new ProviderIconFiles();
        fixtures.Use(factory.Services);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync("/allstarr/v1/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(["apiVersion", "features", "library", "libraryAdditions", "serverVersion"], root.EnumerateObject().Select(item => item.Name).Order().ToArray());
        Assert.Equal(1, root.GetProperty("apiVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("serverVersion").GetString()));
        AssertLibraryProvider(root.GetProperty("library"));
        var features = root.GetProperty("features");
        Assert.Equal(["appearances", "dashboardLink", "events", "fixesMatch", "follow", "history", "historyImports", "historyRecheck", "itemDetails", "libraryAdditions", "lyricsRich", "lyricsSearch", "lyricsSources", "lyricsVersions", "mixes", "playActivity", "playCounts", "playback", "playlistKinds", "preferences", "releases", "reports", "searchStream", "similarAlbums", "sound", "stats", "taste", "titleMarkers"],
            features.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(features.EnumerateObject(), item => Assert.Equal(item.Name != "dashboardLink", item.Value.GetBoolean()));
        Assert.NotNull(observed?.Principal);
        Assert.False(observed.Principal.IsAdministrator);
        Assert.Equal(ProviderActorKind.User, observed.RequireActor().Kind);
        var principal = await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(
            new BackendIdentityDescriptor("Jellyfin", "listener"));
        Assert.Equal(administrator, principal!.IsAdministrator);
    }

    [Theory]
    [InlineData(null, 200, false, 401, "unauthorized")]
    [InlineData("invalid", 401, false, 401, "unauthorized")]
    [InlineData("api-key", 400, false, 403, "forbidden")]
    [InlineData("api-key", 404, false, 403, "forbidden")]
    [InlineData("api-key", 400, true, 403, "forbidden")]
    [InlineData("listener", 503, false, 503, "unavailable")]
    public async Task ClientApi_AuthenticationErrorsUseTheContract(string? token, int upstreamStatus,
        bool explicitUser, int expectedStatus, string code)
    {
        var upstream = new List<string>();
        using var factory = ClientApiFactory(request =>
        {
            upstream.Add(request.RequestUri!.AbsolutePath);
            Assert.Contains($"Token=\"{token}\"", request.Headers.GetValues("Authorization").Single());
            return request.RequestUri.AbsolutePath == "/Users/Me"
                ? Json(upstreamStatus, """{"privateDetail":"upstream detail"}""")
                : Json(200, """{"Id":"listener"}""");
        });
        using var client = CreateClientApiClient(factory);
        if (token != null) client.DefaultRequestHeaders.Add("X-Emby-Token", token);

        using var response = await client.GetAsync("/allstarr/v1/capabilities" + (explicitUser ? "?UserId=listener" : ""));
        await AssertClientApiError(response, expectedStatus, code);
        Assert.Equal(token == null ? [] : explicitUser ? ["/Users/Me", "/Users/listener"] : new[] { "/Users/Me" }, upstream);
    }

    [Fact]
    public async Task ClientApi_UnlinkedTokenIsForbidden()
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory, ready: false);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/capabilities");
        await AssertClientApiError(response, 403, "forbidden");
    }

    [Theory]
    [InlineData("/allstarr/v1/capabilities", 5275, "Jellyfin")]
    [InlineData("/allstarr/v1/openapi.json", 5275, "Jellyfin")]
    [InlineData("/allstarr/v1/capabilities", 8080, "Subsonic")]
    [InlineData("/allstarr/v1/openapi.json", 8080, "Subsonic")]
    [InlineData("/allstarr/v1/does-not-exist", 8080, "Jellyfin")]
    [InlineData("/allstarr/v2/capabilities", 8080, "Jellyfin")]
    [InlineData("/allstarr", 8080, "Jellyfin")]
    public async Task ClientApi_UnknownAndUnavailableRoutesNeverReachUpstream(string path, int port, string backend)
    {
        var upstream = new List<string>();
        using var factory = ClientApiFactory(request =>
        {
            upstream.Add(request.RequestUri!.AbsolutePath);
            return Json(200, "{}");
        }, backend: backend);
        using var client = CreateClientApiClient(factory);
        using var response = await client.GetAsync($"http://localhost:{port}{path}");
        await AssertClientApiError(response, 404, "not_found");
        Assert.Empty(upstream);
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("none", false, true)]
    [InlineData("other", true, true)]
    [InlineData("none", true, false)]
    public async Task ClientApi_TitleMarkersAreARequestOnlyPreference(string? header, bool markers, bool enabled)
    {
        var song = new Song
        {
            Id = "ext-deezer-song-markers",
            ExternalId = "markers",
            ExternalProvider = "deezer",
            Title = "Fixture",
            Artist = "Fixture Artist",
            ExplicitContentLyrics = 1
        };
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Fixture",
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), null))
            .ReturnsAsync(new SearchResult { Songs = [song] });
        var savedPreferences = new ListeningPreferences();
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(service => service.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                System.Collections.Immutable.ImmutableDictionary<ProviderCapabilityKind, System.Collections.Immutable.ImmutableArray<string>>.Empty,
                System.Collections.Immutable.ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            { UserId = user, Preferences = savedPreferences });
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, """{"Items":[],"TotalRecordCount":0}"""), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
        });
        using var client = CreateClientApiClient(factory, enabled: enabled);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Items?SearchTerm=Fixture&IncludeItemTypes=Audio");
        if (header != null) request.Headers.Add("X-Allstarr-Title-Markers", header);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(markers ? "Fixture [A]/[E]" : "Fixture",
            document.RootElement.GetProperty("Items")[0].GetProperty("Name").GetString());
        Assert.True(savedPreferences.ShowExternalLabel);
        Assert.True(savedPreferences.ShowExplicitLabel);
        await SetClientApiEnabledAsync(factory, !enabled);
        using var toggledRequest = new HttpRequestMessage(HttpMethod.Get, "/Items?SearchTerm=Fixture&IncludeItemTypes=Audio");
        toggledRequest.Headers.Add("X-Allstarr-Title-Markers", "none");
        using var toggled = await client.SendAsync(toggledRequest);
        using var toggledBody = JsonDocument.Parse(await toggled.Content.ReadAsStringAsync());
        Assert.Equal(enabled ? "Fixture [A]/[E]" : "Fixture", toggledBody.RootElement.GetProperty("Items")[0].GetProperty("Name").GetString());
        using var next = await client.GetAsync("/Items?SearchTerm=Fixture&IncludeItemTypes=Audio");
        using var restored = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
        Assert.Equal("Fixture [A]/[E]", restored.RootElement.GetProperty("Items")[0].GetProperty("Name").GetString());
    }

    [Theory]
    [InlineData("/allstarr/v1/capabilities")]
    [InlineData("/Items?IncludeItemTypes=Audio")]
    [InlineData("/Audio/native-song/stream")]
    public async Task ClientApi_CorsAllowsTheHeaderNamespaceAndExposesPlaybackRequests(string path)
    {
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/Items" => Json(200, """{"Items":[{"Id":"native-song","Type":"Audio"}]}"""),
            "/Audio/native-song/stream" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
            _ => throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}")
        });
        using var client = CreateClientApiClient(factory);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, path);
        preflight.Headers.Add("Origin", "https://client.example");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "X-Allstarr-Title-Markers,X-Allstarr-Future,Authorization,X-Unlisted");
        using var response = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var allowed = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("x-allstarr-title-markers", allowed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-allstarr-future", allowed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authorization", allowed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-unlisted", allowed, StringComparison.OrdinalIgnoreCase);
        var allowedHeaders = allowed.Split(',', StringSplitOptions.TrimEntries);
        Assert.Equal(allowedHeaders.Length, allowedHeaders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        using var stream = new HttpRequestMessage(HttpMethod.Get, "/Audio/native-song/stream?api_key=listener-token");
        stream.Headers.Add("Origin", "https://client.example");
        using var streamed = await client.SendAsync(stream);
        Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
        Assert.Contains("X-Allstarr-Playback-Request", string.Join(",", streamed.Headers.GetValues("Access-Control-Expose-Headers")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?documentName=other")]
    public async Task ClientApi_OpenApiMatchesTheCheckedInContract(string query)
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var response = await client.GetAsync("/allstarr/v1/openapi.json" + query);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected the client document, received {(int)response.StatusCode}: {body}");
        var document = JsonNode.Parse(body)!;
        var path = Path.Combine(FindClientApiRepositoryRoot(), "apis", "specifications", "allstarr", "client-v1.json");
        var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("ALLSTARR_UPDATE_CLIENT_SPEC") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);
        }
        else
        {
            Assert.True(File.Exists(path) && JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(path)), document),
                "The client contract changed. Generated document:\n" + json);
        }
    }

    [Fact]
    public async Task ClientApi_OpenApiOperationIdsAreNonEmptyAndUnique()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var response = await client.GetAsync("/allstarr/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.False(paths.TryGetProperty("/allstarr/v1/openapi.json", out _));
        var operations = paths.EnumerateObject().SelectMany(path => path.Value.EnumerateObject()).Where(item =>
            new[] { "get", "post", "put", "patch", "delete", "head", "options", "trace" }.Contains(item.Name)).ToArray();
        Assert.NotEmpty(operations);
        var operationIds = operations.Select(operation => operation.Value.GetProperty("operationId").GetString()).ToArray();
        Assert.All(operationIds, operationId => Assert.False(string.IsNullOrWhiteSpace(operationId)));
        Assert.Equal(operationIds.Length, operationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("getEvents", paths.GetProperty("/allstarr/v1/events").GetProperty("get").GetProperty("operationId").GetString());
        var stream = paths.GetProperty("/allstarr/v1/events").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content");
        Assert.Equal(["text/event-stream"], stream.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.Equal("string", stream.GetProperty("text/event-stream").GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("getCapabilities", paths.GetProperty("/allstarr/v1/capabilities").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("getItemDetails", paths.GetProperty("/allstarr/v1/items/details").GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("getProviderIcon", paths.GetProperty("/allstarr/v1/providers/{providerId}/icon").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("getPlayback", paths.GetProperty("/allstarr/v1/playback/{requestId}").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("getCurrentPlayback", paths.GetProperty("/allstarr/v1/playback/current").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("searchLyrics", paths.GetProperty("/allstarr/v1/search/lyrics").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("getItemLyrics", paths.GetProperty("/allstarr/v1/items/{itemId}/lyrics").GetProperty("get").GetProperty("operationId").GetString());
        var lyricsQuery = paths.GetProperty("/allstarr/v1/items/{itemId}/lyrics").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "sourceId");
        Assert.Equal("query", lyricsQuery.GetProperty("in").GetString());
        Assert.False(lyricsQuery.TryGetProperty("required", out var requiredLyricsQuery) && requiredLyricsQuery.GetBoolean());
        Assert.Equal("string", lyricsQuery.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("getLyricsSources", paths.GetProperty("/allstarr/v1/items/{itemId}/lyrics/sources").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("setLyricsSource", paths.GetProperty("/allstarr/v1/items/{itemId}/lyrics/source").GetProperty("put").GetProperty("operationId").GetString());
        Assert.Equal("clearLyricsSource", paths.GetProperty("/allstarr/v1/items/{itemId}/lyrics/source/clear").GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("getItemMatch", paths.GetProperty("/allstarr/v1/items/{itemId}/match").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("setItemMatch", paths.GetProperty("/allstarr/v1/items/{itemId}/match").GetProperty("put").GetProperty("operationId").GetString());
        Assert.Equal("rejectItemMatch", paths.GetProperty("/allstarr/v1/items/{itemId}/match/reject").GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("clearItemMatch", paths.GetProperty("/allstarr/v1/items/{itemId}/match").GetProperty("delete").GetProperty("operationId").GetString());
        Assert.Equal("createReport", paths.GetProperty("/allstarr/v1/reports").GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("listMixes", paths.GetProperty("/allstarr/v1/mixes").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("listMixItems", paths.GetProperty("/allstarr/v1/mixes/{mixId}/items").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("getMixCover", paths.GetProperty("/allstarr/v1/mixes/{mixId}/cover").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("getMyStats", paths.GetProperty("/allstarr/v1/me/stats").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("getMyStatsArtwork", paths.GetProperty("/allstarr/v1/me/stats/art").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("getMyTaste", paths.GetProperty("/allstarr/v1/me/taste").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("getMySound", paths.GetProperty("/allstarr/v1/me/sound").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("listReleases", paths.GetProperty("/allstarr/v1/releases").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("getReleaseCover", paths.GetProperty("/allstarr/v1/releases/{releaseId}/cover").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("followArtist", paths.GetProperty("/allstarr/v1/artists/{artistId}/follow").GetProperty("put").GetProperty("operationId").GetString());
        Assert.Equal("unfollowArtist", paths.GetProperty("/allstarr/v1/artists/{artistId}/follow").GetProperty("delete").GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task ClientApi_OpenApiNumericSchemasNeverAllowStrings()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var response = await client.GetAsync("/allstarr/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var numeric = 0;
        void Check(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("type", out var type) && type.ValueKind is JsonValueKind.String or JsonValueKind.Array)
                {
                    var types = type.ValueKind == JsonValueKind.Array
                        ? type.EnumerateArray().Select(value => value.GetString()).ToArray() : [type.GetString()];
                    if (types.Contains("integer") || types.Contains("number"))
                    {
                        numeric++;
                        Assert.DoesNotContain("string", types);
                    }
                }
                foreach (var property in node.EnumerateObject()) Check(property.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var value in node.EnumerateArray()) Check(value);
        }
        Check(document.RootElement);
        Assert.True(numeric > 0);
    }

    [Fact]
    public async Task ClientApi_OpenApiContainsOnlyClientOperationsAndCamelCaseSchemasWithErrors()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var response = await client.GetAsync("/allstarr/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        Assert.NotEmpty(paths.EnumerateObject());
        Assert.True(paths.TryGetProperty("/allstarr/v1/capabilities", out _));
        foreach (var path in paths.EnumerateObject())
        {
            Assert.StartsWith("/allstarr/v1/", path.Name);
            var operations = path.Value.EnumerateObject().Where(item =>
                new[] { "get", "post", "put", "patch", "delete", "head", "options", "trace" }.Contains(item.Name)).ToArray();
            Assert.NotEmpty(operations);
            foreach (var operation in operations)
                foreach (var status in new[] { "400", "401", "403", "404", "409", "503" })
                {
                    var problem = status == "400" && path.Name == "/allstarr/v1/providers/{providerId}/icon";
                    Assert.EndsWith(problem ? "/ProblemDetails" : "/ClientApiError", operation.Value.GetProperty("responses").GetProperty(status)
                        .GetProperty("content").GetProperty(problem ? "application/problem+json" : "application/json")
                        .GetProperty("schema").GetProperty("$ref").GetString());
                }
        }
        AssertClientApiSchemaNames(root);
    }

    [Theory]
    [InlineData("number=bad", 400, "invalid_request")]
    [InlineData("number=0", 400, "invalid_request")]
    [InlineData("number=1&failure=conflict", 409, "conflict")]
    [InlineData("number=1&failure=throw", 503, "unavailable")]
    public async Task ClientApi_ValidationAndExceptionsUseTheContract(string query, int status, string code)
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
            services.AddControllers().AddApplicationPart(typeof(ClientApiProbeController).Assembly));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/contract-probe?" + query);
        await AssertClientApiError(response, status, code);
    }

    [Fact]
    public async Task ClientApi_NewControllersInheritCamelCaseResponses()
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
            services.AddControllers().AddApplicationPart(typeof(ClientApiProbeController).Assembly));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/contract-probe?number=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("listener", document.RootElement.GetProperty("nestedValue").GetProperty("listenerName").GetString());
        Assert.Equal("fixture", document.RootElement.GetProperty("dictionaryValue").GetProperty("displayTitle").GetString());
    }

    [Theory]
    [InlineData("{", "application/json")]
    [InlineData("{}", "application/json")]
    [InlineData("text", "text/plain")]
    public async Task ClientApi_InvalidBodiesUseTheContract(string body, string contentType)
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
            services.AddControllers().AddApplicationPart(typeof(ClientApiProbeController).Assembly));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.PostAsync("/allstarr/v1/contract-probe",
            new StringContent(body, System.Text.Encoding.UTF8, contentType));
        await AssertClientApiError(response, 400, "invalid_request");
    }

    private static void AssertClientApiSchemaNames(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name == "properties")
                    Assert.All(property.Value.EnumerateObject(), item => Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(item.Name), item.Name));
                AssertClientApiSchemaNames(property.Value);
            }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) AssertClientApiSchemaNames(item);
    }

    private static async Task AssertClientApiError(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["code", "error"], document.RootElement.EnumerateObject().Select(item => item.Name).Order().ToArray());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("error").GetString()));
        Assert.DoesNotContain("upstream detail", document.RootElement.GetProperty("error").GetString());
    }

    private static ProtocolFactory ClientApiFactory(Func<HttpRequestMessage, HttpResponseMessage> responder,
        Action<IServiceCollection>? configure = null, string backend = "Jellyfin") => new(backend, responder, services =>
        {
            services.AddSingleton<IStartupFilter, ClientApiPortStartupFilter>();
            configure?.Invoke(services);
        }, new Dictionary<string, string?> { ["Cors:AllowedOrigins"] = "https://client.example" });

    private static HttpClient CreateClientApiClient(ProtocolFactory factory, bool ready = true, bool enabled = true)
    {
        var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(ready ? DurableStorageReadiness.Ready : DurableStorageReadiness.Initializing);
        if (enabled) SetClientApiEnabledAsync(factory, true).GetAwaiter().GetResult();
        return client;
    }

    private static async Task SetClientApiEnabledAsync(ProtocolFactory factory, bool enabled)
    {
        var settings = factory.Services.GetRequiredService<IDurableRuntimeSettings>();
        var current = await settings.GetAsync("ClientApi:Enabled");
        await settings.ApplyBatchAsync([new("ClientApi:Enabled", enabled.ToString(), current.Revision)], "fixture");
    }

    private static string FindClientApiRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "allstarr.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class ClientApiPortStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuePipeline) =>
            {
                context.Connection.LocalPort = context.Request.Host.Port ?? 8080;
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return continuePipeline();
            });
            next(app);
        };
    }

    private sealed class ClientApiObservationFilter(Action<ProtocolExecutionContext?> observe) : IResultFilter
    {
        public void OnResultExecuting(ResultExecutingContext context) => observe(context.HttpContext.GetProtocolExecutionContext());
        public void OnResultExecuted(ResultExecutedContext context) { }
    }
}

[Route("allstarr/v1/contract-probe")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ClientApiProbeController : ClientApiControllerBase
{
    [HttpGet]
    public IActionResult Get([System.ComponentModel.DataAnnotations.Range(1, 10)] int number, string? failure)
    {
        if (failure == "throw") throw new InvalidOperationException("upstream detail");
        if (failure == "conflict") return Conflict();
        return Ok(new
        {
            NestedValue = new { ListenerName = Execution.Principal!.DisplayName },
            DictionaryValue = new Dictionary<string, string> { ["DisplayTitle"] = "fixture" }
        });
    }

    [HttpPost]
    public IActionResult Post([FromBody] ClientApiProbeBody body) => Ok(body);
}

public sealed class ClientApiProbeBody
{
    [System.ComponentModel.DataAnnotations.Required]
    public string? Name { get; set; }
}
