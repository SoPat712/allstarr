using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class SubsonicViewerReadAuthenticationTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();

    [Theory]
    [InlineData("getPlaylists")]
    [InlineData("getPlaylist")]
    [InlineData("getSong")]
    [InlineData("getCoverArt")]
    public async Task ReadEndpointsSendCurrentViewerAuthenticationEvenWithStoredCredentialReference(string endpoint)
    {
        var http = ProtocolHttp();
        http.Items[SubsonicAuthFilter.RequestParametersItemKey] = SubsonicRequestParameters.FromDictionary(
            new Dictionary<string, string>
            {
                ["u"] = "viewer",
                ["t"] = "viewer-token",
                ["s"] = "viewer-salt",
                ["v"] = "1.16.1",
                ["c"] = "viewer-client",
                ["id"] = "untrusted-id",
                ["songId"] = "untrusted-song",
                ["f"] = "xml",
                ["arbitrary"] = "untrusted"
            });
        using var handler = new RecordingBackend();
        using var client = new HttpClient(handler);
        var target = new SubsonicPlaylistTarget(client, new Uri("https://backend.test/"), Resolver(http));

        switch (endpoint)
        {
            case "getPlaylists": Assert.True((await target.ListAsync(TargetContext(), null, 20, default)).IsSuccess); break;
            case "getPlaylist": Assert.True((await target.ReadAsync(TargetContext(), "playlist", default)).IsSuccess); break;
            case "getSong": Assert.True((await target.ReadItemsAsync(TargetContext(), ["song"], default)).IsSuccess); break;
            case "getCoverArt": Assert.True((await target.ReadArtworkAsync(TargetContext(), "playlist", "cover", default)).IsSuccess); break;
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal(endpoint, request.Endpoint);
        Assert.Equal("viewer", request.Parameters["u"]);
        Assert.Equal("viewer-token", request.Parameters["t"]);
        Assert.Equal("viewer-salt", request.Parameters["s"]);
        Assert.Equal("json", request.Parameters["f"]);
        Assert.DoesNotContain("p", request.Parameters.Keys);
        Assert.DoesNotContain("songId", request.Parameters.Keys);
        Assert.DoesNotContain("arbitrary", request.Parameters.Keys);
        Assert.DoesNotContain("untrusted-id", request.Parameters.Values);
    }

    [Theory]
    [InlineData("Subsonic")]
    [InlineData("Navidrome")]
    [InlineData("OpenSubsonic")]
    public async Task AdminReadsUseMatchingSessionAuthenticationAndWhitelistParameters(string dialect)
    {
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(dialect);
        var authentication = await Resolver(http).ResolveReadAsync(TargetContext(), default);

        Assert.Contains(authentication.FormParameters, pair => pair is { Key: "t", Value: "admin-viewer-token" });
        Assert.Contains(authentication.FormParameters, pair => pair is { Key: "u", Value: "viewer" });
        Assert.DoesNotContain(authentication.FormParameters, pair => pair.Key is "id" or "f" or "arbitrary");
    }

    [Theory]
    [InlineData("principal")]
    [InlineData("backend")]
    [InlineData("tenant")]
    [InlineData("protocol")]
    [InlineData("missing_parameters")]
    public async Task MismatchedProtocolViewerCannotOpenStoredOrGlobalSecret(string mismatch)
    {
        var http = ProtocolHttp(
            backend: mismatch == "backend" ? "other-backend" : "backend",
            principal: mismatch == "principal" ? "playlist-owner" : "viewer",
            tenant: mismatch == "tenant" ? Guid.CreateVersion7() : _tenantId,
            protocol: mismatch == "protocol" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic);
        if (mismatch != "missing_parameters")
            http.Items[SubsonicAuthFilter.RequestParametersItemKey] = ViewerParameters();
        // A second authenticated identity cannot replace the verified protocol viewer.
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await Resolver(http).ResolveReadAsync(TargetContext(), default));
    }

    [Theory]
    [InlineData("principal")]
    [InlineData("tenant")]
    [InlineData("dialect")]
    [InlineData("missing_authentication")]
    public async Task MismatchedAdminViewerCannotOpenStoredOrGlobalSecret(string mismatch)
    {
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(
            mismatch == "dialect" ? "Jellyfin" : "Subsonic",
            mismatch == "principal" ? "playlist-owner" : "viewer",
            mismatch == "tenant" ? Guid.CreateVersion7() : _tenantId,
            authentication: mismatch != "missing_authentication");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await Resolver(http).ResolveReadAsync(TargetContext(), default));
    }

    [Fact]
    public async Task UnverifiedQueryAuthenticationIsNeverUsedAsAReadFallback()
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?u=viewer&p=query-password&apiKey=query-key");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await Resolver(http).ResolveReadAsync(TargetContext(), default));
    }

    [Fact]
    public async Task VerifiedApiKeyIsWhitelistedForReadAuthentication()
    {
        var http = ProtocolHttp();
        http.Items[SubsonicAuthFilter.RequestParametersItemKey] = SubsonicRequestParameters.FromDictionary(
            new Dictionary<string, string> { ["apiKey"] = "viewer-api-key", ["v"] = "1.16.1", ["c"] = "client" });

        var authentication = await Resolver(http).ResolveReadAsync(TargetContext(), default);

        Assert.Contains(authentication.FormParameters, pair => pair is { Key: "apiKey", Value: "viewer-api-key" });
    }

    [Fact]
    public async Task MutationsUseWriteResolverAndFollowupReadsUseReadResolver()
    {
        using var handler = new RecordingBackend();
        using var client = new HttpClient(handler);
        var authentication = new SplitAuthenticationResolver();
        var target = new SubsonicPlaylistTarget(client, new Uri("https://backend.test/"), authentication);

        var result = await target.WriteAsync(TargetContext(), new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Recreate, new BackendPlaylistMetadata("Mix"), ["song"], "fixture"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, authentication.WriteCalls);
        Assert.Equal(2, authentication.ReadCalls);
        Assert.Equal("write-token", Assert.Single(handler.Requests, request => request.Endpoint == "createPlaylist").Parameters["t"]);
        Assert.All(handler.Requests.Where(request => request.Endpoint != "createPlaylist"),
            request => Assert.Equal("read-token", request.Parameters["t"]));
    }

    [Fact]
    public async Task SyntheticResolverDefaultReadMethodRetainsExistingAuthentication()
    {
        IBackendPlaylistAuthenticationResolver resolver = new WriteOnlyAuthenticationResolver();

        var authentication = await resolver.ResolveReadAsync(TargetContext(), default);

        Assert.Contains(authentication.FormParameters, pair => pair is { Key: "t", Value: "background-token" });
    }

    private EncryptedSubsonicPlaylistAuthenticationResolver Resolver(HttpContext http) => new(
        null!,
        new HttpContextAccessor { HttpContext = http });

    private BackendPlaylistTargetContext TargetContext() => new(
        "backend", "viewer", Guid.CreateVersion7().ToString(), _tenantId);

    private DefaultHttpContext ProtocolHttp(string backend = "backend", string principal = "viewer",
        Guid? tenant = null, ProtocolKind protocol = ProtocolKind.Subsonic)
    {
        var http = new DefaultHttpContext();
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = new ProtocolExecutionContext(
            protocol, backend, principal,
            new AllstarrPrincipal(tenant ?? _tenantId, Guid.CreateVersion7(), protocol.ToString().ToLowerInvariant(),
                backend, principal, "Fixture", false),
            "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        return http;
    }

    private AdminAuthSession Session(string dialect = "Subsonic", string principal = "viewer",
        Guid? tenant = null, bool authentication = true) => new()
        {
            SessionId = "fixture",
            UserId = principal,
            UserName = "Fixture",
            IsAdministrator = false,
            BackendType = dialect,
            TenantId = tenant ?? _tenantId,
            AllstarrUserId = Guid.CreateVersion7(),
            JellyfinAccessToken = "protected",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow,
            SubsonicReadAuthentication = authentication ? new Dictionary<string, string>
            {
                ["u"] = principal,
                ["t"] = "admin-viewer-token",
                ["s"] = "admin-viewer-salt",
                ["v"] = "1.16.1",
                ["c"] = "admin",
                ["id"] = "untrusted-id",
                ["f"] = "xml",
                ["arbitrary"] = "untrusted"
            } : null
        };

    private static SubsonicRequestParameters ViewerParameters() => SubsonicRequestParameters.FromDictionary(
        new Dictionary<string, string> { ["u"] = "viewer", ["t"] = "viewer-token", ["s"] = "viewer-salt" });

    private sealed class SplitAuthenticationResolver : IBackendPlaylistAuthenticationResolver
    {
        public int WriteCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public ValueTask<BackendPlaylistAuthentication> ResolveAsync(BackendPlaylistTargetContext context, CancellationToken cancellationToken)
        {
            WriteCalls++;
            return ValueTask.FromResult(Authentication("write-token"));
        }
        public ValueTask<BackendPlaylistAuthentication> ResolveReadAsync(BackendPlaylistTargetContext context, CancellationToken cancellationToken)
        {
            ReadCalls++;
            return ValueTask.FromResult(Authentication("read-token"));
        }
    }

    private sealed class WriteOnlyAuthenticationResolver : IBackendPlaylistAuthenticationResolver
    {
        public ValueTask<BackendPlaylistAuthentication> ResolveAsync(BackendPlaylistTargetContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Authentication("background-token"));
    }

    private static BackendPlaylistAuthentication Authentication(string token) => new(new Dictionary<string, string>(),
        [new("u", "viewer"), new("t", token), new("s", "salt")]);

    private sealed class RecordingBackend : HttpMessageHandler
    {
        public List<(string Endpoint, Dictionary<string, string> Parameters)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
            var parameters = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken))
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            Requests.Add((endpoint, parameters));
            if (endpoint == "getCoverArt")
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            object response = endpoint switch
            {
                "getPlaylists" => new { status = "ok", playlists = new { playlist = new[] { new { id = "playlist", name = "Mix" } } } },
                "getPlaylist" => new { status = "ok", playlist = new { id = "playlist", name = "Mix", entry = new[] { new { id = "song", duration = 180 } } } },
                "getSong" => new { status = "ok", song = new { id = "song", title = "Fixture", duration = 180 } },
                _ => new { status = "ok" }
            };
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object> { ["subsonic-response"] = response }),
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
