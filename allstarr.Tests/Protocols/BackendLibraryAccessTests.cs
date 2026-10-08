using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class BackendLibraryAccessTests
{
    [Theory]
    [InlineData("api_key")]
    [InlineData("ApiKey")]
    [InlineData("access_token")]
    public async Task RequestPermissions_UseVerifiedViewerCredentialsAndNeverTheServiceKey(string tokenParameter)
    {
        var clock = new Clock();
        var viewer = Context(clock, ProtocolKind.Jellyfin, "listener");
        var http = new DefaultHttpContext();
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = viewer;
        http.Request.QueryString = new($"?UserId=other-user&{tokenParameter}=viewer-token");
        var factory = new Mock<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict);
        var source = new BackendLibraryPermissionSource(new HttpContextAccessor { HttpContext = http }, factory.Object, null!,
            Options.Create(new JellyfinSettings { Url = "https://backend.test", ApiKey = "service-key" }),
            Options.Create(new SubsonicSettings()));

        using var request = await source.CreateRequestAsync(viewer, default);
        Assert.NotNull(request);
        Assert.Contains("UserId=listener", request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("IncludeHidden=true", request.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("viewer-token", request.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("service-key", request.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
        Assert.Null(await source.CreateRequestAsync(Context(clock, ProtocolKind.Jellyfin, "other-user"), default));
        http.Request.QueryString = QueryString.Empty;
        Assert.Null(await source.CreateRequestAsync(viewer, default));
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WebUiPermissions_UseOnlyTheSessionsOwnSubsonicReadAuthentication()
    {
        var clock = new Clock();
        var viewer = Context(clock, ProtocolKind.Subsonic, "listener");
        var authentication = SubsonicSessionAuthentication.Create("listener", "password-fixture");
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            UserId = "listener",
            UserName = "Listener",
            IsAdministrator = false,
            BackendType = "Subsonic",
            TenantId = viewer.Principal!.TenantId,
            AllstarrUserId = viewer.Principal.UserId,
            JellyfinAccessToken = "",
            SubsonicReadAuthentication = authentication,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var factory = new Mock<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict);
        var source = new BackendLibraryPermissionSource(new HttpContextAccessor { HttpContext = http }, factory.Object, null!,
            Options.Create(new JellyfinSettings()), Options.Create(new SubsonicSettings { Url = "https://backend.test" }));
        using var request = await source.CreateRequestAsync(viewer, default);
        Assert.NotNull(request);
        Assert.Equal("/rest/getMusicFolders.view", request.RequestUri!.AbsolutePath);
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("u=listener", body, StringComparison.Ordinal);
        Assert.Contains("t=" + authentication["t"], body, StringComparison.Ordinal);
        Assert.DoesNotContain("password-fixture", body, StringComparison.Ordinal);
        Assert.Null(await source.CreateRequestAsync(Context(clock, ProtocolKind.Subsonic, "other-user"), default));
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = Context(clock, ProtocolKind.Subsonic, "other-user");
        Assert.Null(await source.CreateRequestAsync(viewer, default));
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task Permissions_AreBoundToBackendViewerAndAuthenticationContext(ProtocolKind protocol)
    {
        var clock = new Clock();
        var credential = "first-session";
        var handler = new Handler(request => Libraries(protocol, request.RequestUri!.AbsolutePath.Trim('/')));
        var source = new Source(context =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://backend.test/" + context.VerifiedBackendPrincipalId);
            request.Headers.Add("Authorization", "Bearer " + credential);
            return request;
        });
        using var cache = new MemoryApplicationCache(clock);
        var resolver = new BackendLibraryAccessResolver(source, new Clients(handler), cache, clock, new ConfigurationBuilder().Build());
        var a = Context(clock, protocol, "listener-a");
        var b = Context(clock, protocol, "listener-b");

        Assert.Equal(["listener-a"], (await resolver.ResolveAsync(a)).LibraryIds);
        Assert.Equal(["listener-b"], (await resolver.ResolveAsync(b)).LibraryIds);
        Assert.Equal(["listener-a"], (await resolver.ResolveAsync(a)).LibraryIds);
        Assert.Equal(2, handler.Calls);
        credential = "new-session";
        await resolver.ResolveAsync(a);
        Assert.Equal(3, handler.Calls);
        await resolver.ResolveAsync(Context(clock, protocol, "listener-a", "another-backend"));
        Assert.Equal(4, handler.Calls);
        await resolver.InvalidateAsync(protocol, "primary", "listener-a");
        await resolver.ResolveAsync(a);
        Assert.Equal(5, handler.Calls);
        await resolver.ResolveAsync(b);
        Assert.Equal(6, handler.Calls);
        await resolver.ResolveAsync(b);
        Assert.Equal(6, handler.Calls);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task PermissionExpiry_RejectsStaleAccessWhenBackendFailsAndRecovers(ProtocolKind protocol)
    {
        var clock = new Clock();
        var failed = false;
        var handler = new Handler(_ => failed ? new(HttpStatusCode.ServiceUnavailable) : Libraries(protocol, "music"));
        using var cache = new MemoryApplicationCache(clock);
        var resolver = Resolver(handler, cache, clock);
        var context = Context(clock, protocol, "listener");

        Assert.True((await resolver.ResolveAsync(context)).Allows("music"));
        failed = true;
        clock.UtcNow = clock.UtcNow.AddSeconds(29);
        Assert.True((await resolver.ResolveAsync(context)).Allows("music"));
        Assert.Equal(1, handler.Calls);
        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        var unavailable = await resolver.ResolveAsync(context);
        Assert.False(unavailable.Succeeded);
        Assert.Empty(unavailable.LibraryIds);
        Assert.Equal(2, handler.Calls);
        failed = false;
        Assert.True((await resolver.ResolveAsync(context)).Allows("music"));
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task PermissionLookup_DistinguishesEmptyAccessFromMalformedResponse(ProtocolKind protocol)
    {
        var clock = new Clock();
        var malformed = true;
        var handler = new Handler(_ => malformed
            ? new(HttpStatusCode.OK) { Content = new StringContent("{}") }
            : Libraries(protocol));
        using var cache = new MemoryApplicationCache(clock);
        var resolver = Resolver(handler, cache, clock);
        var context = Context(clock, protocol, "listener");

        Assert.False((await resolver.ResolveAsync(context)).Succeeded);
        malformed = false;
        var empty = await resolver.ResolveAsync(context);
        Assert.True(empty.Succeeded);
        Assert.Empty(empty.LibraryIds);
        Assert.False(empty.Allows("music"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ConfiguredSelection_IsAnIntersectionAndChangesInvalidateCachedSelection()
    {
        var clock = new Clock();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var handler = new Handler(_ => Libraries(ProtocolKind.Jellyfin, "music-a", "music-b"));
        using var cache = new MemoryApplicationCache(clock);
        var resolver = Resolver(handler, cache, clock, configuration);
        var context = Context(clock, ProtocolKind.Jellyfin, "listener");
        Assert.Equal(["music-a", "music-b"], (await resolver.ResolveAsync(context)).LibraryIds);
        configuration[BackendMusicLibraries.SelectionKey] = "music-b,not-permitted";
        Assert.Equal(["music-b"], (await resolver.ResolveAsync(context)).LibraryIds);
        Assert.Equal(2, handler.Calls);
    }

    private static BackendLibraryAccessResolver Resolver(Handler handler, IApplicationCache cache, Clock clock,
        IConfiguration? configuration = null) => new(
        new Source(_ => new(HttpMethod.Get, "https://backend.test/libraries")), new Clients(handler), cache, clock,
        configuration ?? new ConfigurationBuilder().Build());

    private static ProtocolExecutionContext Context(Clock clock, ProtocolKind protocol, string user, string backend = "primary") =>
        new(protocol, backend, user, new AllstarrPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(),
            protocol.ToString().ToLowerInvariant(), backend, user, user, false),
            "library-access-test", clock.UtcNow.AddMinutes(5), default);

    private static HttpResponseMessage Libraries(ProtocolKind protocol, params string[] ids)
    {
        var json = protocol == ProtocolKind.Jellyfin
            ? JsonSerializer.Serialize(new { Items = ids.Select(id => new { Id = id, CollectionType = "music" }) })
            : JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["subsonic-response"] = new { status = "ok", musicFolders = new { musicFolder = ids.Select(id => new { id }) } }
            });
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Source(Func<ProtocolExecutionContext, HttpRequestMessage?> request) : IBackendLibraryPermissionSource
    {
        public Task<HttpRequestMessage?> CreateRequestAsync(ProtocolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(request(context));
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(response(request));
        }
    }
}
