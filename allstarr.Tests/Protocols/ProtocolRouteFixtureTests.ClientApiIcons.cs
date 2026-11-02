using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_ProviderIconDocumentDescribesBinaryImagesAndEmptyRevalidation()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var response = await client.GetAsync("/allstarr/v1/openapi.json");
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = json.RootElement.GetProperty("paths").GetProperty("/allstarr/v1/providers/{providerId}/icon")
            .GetProperty("get").GetProperty("responses");
        var content = responses.GetProperty("200").GetProperty("content");
        Assert.NotEmpty(content.EnumerateObject());
        Assert.All(content.EnumerateObject(), item =>
        {
            Assert.StartsWith("image/", item.Name);
            Assert.Equal("binary", item.Value.GetProperty("schema").GetProperty("format").GetString());
        });
        Assert.False(responses.GetProperty("304").TryGetProperty("content", out var unchanged) && unchanged.EnumerateObject().Any());
    }

    [Theory]
    [InlineData("deezer", "deezer.svg", "image/svg+xml")]
    [InlineData("apple-musickit", "applemusic.svg", "image/svg+xml")]
    [InlineData("qobuz", "qobuz.webp", "image/webp")]
    [InlineData("spicylyrics", "spicylyrics.ico", "image/x-icon")]
    public async Task ClientApi_ProviderIconsMatchDashboardAssetsAndSupportRevalidation(
        string provider, string filename, string contentType)
    {
        var upstream = new List<string>();
        using var factory = ClientApiFactory(request =>
        {
            upstream.Add(request.RequestUri!.AbsolutePath);
            return Json(200, """{"Id":"listener"}""");
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var dashboard = await client.GetAsync($"http://localhost:5275/images/providers/{filename}");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        var expected = await dashboard.Content.ReadAsByteArrayAsync();

        var path = $"/allstarr/v1/providers/{provider}/icon";
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromDays(1), response.Headers.CacheControl?.MaxAge);
        Assert.NotNull(response.Headers.ETag);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var unchanged = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        Assert.Equal(response.Headers.ETag, unchanged.Headers.ETag);
        Assert.Equal(response.Headers.CacheControl?.ToString(), unchanged.Headers.CacheControl?.ToString());
        Assert.Equal(["/Users/Me", "/Users/Me"], upstream);
    }

    [Fact]
    public async Task ClientApi_ProviderIconsUseTheSameActiveExtensionIconAsDashboard()
    {
        const string provider = "fixture-icon";
        var directory = Path.Combine(Path.GetTempPath(), "allstarr-provider-icon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var expected = await File.ReadAllBytesAsync(Path.Combine(FindClientApiRepositoryRoot(),
                "allstarr", "wwwroot", "images", "providers", "deezer-cover-96.png"));
            await File.WriteAllBytesAsync(Path.Combine(directory, "icon.png"), expected);
            using var factory = ClientApiFactory(_ => Json(200,
                """{"Id":"listener","Name":"Listener","Policy":{"IsAdministrator":true,"IsDisabled":false}}"""),
                services => services.AddProviderDescriptor(new ProviderDescriptor(provider, "Fixture", "Fixture provider",
                    ProviderOrigin.Extension, "1", "1",
                    [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.ConfiguredOnly,
                        ProviderAccountRequirement.None, "1")],
                    new ProviderPermissionDescriptor(), branding: new("icon.png"), entryPoint: "index.js")));
            using var client = CreateClientApiClient(factory);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
            {
                db.ExtensionPackages.Add(new ExtensionPackageRecord
                {
                    Id = Guid.NewGuid(),
                    ExtensionId = provider,
                    DisplayName = "Fixture",
                    Version = "1.0.0",
                    SdkVersion = "1",
                    Sha256 = new string('a', 64),
                    ContentSha256 = new string('b', 64),
                    PackagePath = directory,
                    State = ExtensionPackageState.Active,
                    StagedAt = DateTimeOffset.UtcNow,
                    ManifestJson = """{"id":"fixture-icon","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js","icon":"icon.png","capabilities":[{"kind":"Metadata","hooks":["getTrack"],"accountScopes":[],"accountRequired":false}],"permissions":[]}"""
                });
                await db.SaveChangesAsync();
            }
            var principal = await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(
                new BackendIdentityDescriptor("Jellyfin", "listener", "Listener", true));
            var session = await factory.Services.GetRequiredService<AdminAuthSessionService>().CreateSessionAsync(
                "listener", "Listener", true, "dashboard-token", null, allstarrUserId: principal!.UserId);
            using var dashboardRequest = new HttpRequestMessage(HttpMethod.Get,
                $"http://localhost:5275/api/admin/extensions/providers/{provider}/icon");
            dashboardRequest.Headers.Add("Cookie", $"{AdminAuthSessionService.SessionCookieName}={session.SessionId}");
            using var dashboard = await client.SendAsync(dashboardRequest);
            Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
            Assert.Equal("image/png", dashboard.Content.Headers.ContentType?.MediaType);
            Assert.Null(dashboard.Headers.CacheControl);
            Assert.Equal(expected, await dashboard.Content.ReadAsByteArrayAsync());

            using var cookieRequest = new HttpRequestMessage(HttpMethod.Get, $"/allstarr/v1/providers/{provider}/icon");
            cookieRequest.Headers.Add("Cookie", $"{AdminAuthSessionService.SessionCookieName}={session.SessionId}");
            using var cookieOnly = await client.SendAsync(cookieRequest);
            await AssertClientApiError(cookieOnly, 401, "unauthorized");

            client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
            using var response = await client.GetAsync($"/allstarr/v1/providers/{provider}/icon");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(TimeSpan.FromDays(1), response.Headers.CacheControl?.MaxAge);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("unknown-provider")]
    [InlineData("Deezer")]
    public async Task ClientApi_UnknownProviderIconsUseTheErrorContract(string provider)
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync($"/allstarr/v1/providers/{provider}/icon");
        await AssertClientApiError(response, 404, "not_found");
        Assert.Null(response.Headers.CacheControl);
    }

    [Theory]
    [InlineData(ProviderOrigin.BuiltIn)]
    [InlineData(ProviderOrigin.Extension)]
    public async Task ClientApi_ProviderWithoutIconUsesTheNotFoundContract(ProviderOrigin origin)
    {
        const string provider = "fixture-without-icon";
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""),
            services => services.AddProviderDescriptor(new ProviderDescriptor(provider, "Fixture", "Fixture provider",
                origin, "1", "1",
                [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.ConfiguredOnly,
                    ProviderAccountRequirement.None, "1")],
                new ProviderPermissionDescriptor(), entryPoint: origin == ProviderOrigin.Extension ? "index.js" : null)));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync($"/allstarr/v1/providers/{provider}/icon");
        await AssertClientApiError(response, 404, "not_found");
    }

    [Theory]
    [InlineData(null, 200, true, 401, "unauthorized")]
    [InlineData("invalid", 401, true, 401, "unauthorized")]
    [InlineData("api-key", 404, true, 403, "forbidden")]
    [InlineData("listener-token", 200, false, 403, "forbidden")]
    public async Task ClientApi_ProviderIconsRequireCallerTokenAndLinkedListener(
        string? token, int upstreamStatus, bool ready, int expectedStatus, string code)
    {
        var upstream = new List<string>();
        using var factory = ClientApiFactory(request =>
        {
            upstream.Add(request.RequestUri!.AbsolutePath);
            return Json(upstreamStatus, """{"Id":"listener"}""");
        });
        using var client = CreateClientApiClient(factory, ready: ready);
        if (token != null) client.DefaultRequestHeaders.Add("X-Emby-Token", token);
        using var response = await client.GetAsync("/allstarr/v1/providers/deezer/icon");
        await AssertClientApiError(response, expectedStatus, code);
        Assert.Equal(token == null ? [] : new[] { "/Users/Me" }, upstream);
    }

    [Fact]
    public async Task ClientApi_ProviderIconsFollowLiveOptIn()
    {
        var upstream = new List<string>();
        using var factory = ClientApiFactory(request =>
        {
            upstream.Add(request.RequestUri!.AbsolutePath);
            return Json(200, """{"Id":"listener"}""");
        });
        using var client = CreateClientApiClient(factory, enabled: false);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        const string path = "/allstarr/v1/providers/deezer/icon";
        using var disabled = await client.GetAsync(path);
        await AssertClientApiError(disabled, 404, "not_found");
        Assert.Empty(upstream);
        await SetClientApiEnabledAsync(factory, true);
        using var enabled = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Single(upstream);
        await SetClientApiEnabledAsync(factory, false);
        using var disabledAgain = await client.GetAsync(path);
        await AssertClientApiError(disabledAgain, 404, "not_found");
        Assert.Single(upstream);
    }

    [Theory]
    [InlineData(5275, "Jellyfin")]
    [InlineData(8080, "Subsonic")]
    public async Task ClientApi_ProviderIconsStayOnTheSupportedListener(int port, string backend)
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"), backend: backend);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync($"http://localhost:{port}/allstarr/v1/providers/deezer/icon");
        await AssertClientApiError(response, 404, "not_found");
    }
}
