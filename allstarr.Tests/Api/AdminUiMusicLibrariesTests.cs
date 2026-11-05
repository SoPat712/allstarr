using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class AdminUiMusicLibrariesTests
{
    [Fact]
    public async Task JellyfinListsNamedMusicLibrariesOutsideCurrentSelection()
    {
        using var fixture = new Fixture("Jellyfin", """
            {"Items":[
              {"Id":"library-b","Name":"Classical","CollectionType":"music"},
              {"Id":"movies","Name":"Movies","CollectionType":"movies"},
              {"Id":"library-a","Name":"Albums","CollectionType":"Music"}
            ]}
            """);

        var result = Assert.IsType<OkObjectResult>(await fixture.Controller.GetMusicLibraries());

        Assert.Equal(new[] { ("library-a", "Albums"), ("library-b", "Classical") }, Libraries(result));
        Assert.Equal(HttpMethod.Get, fixture.Handler.Method);
        Assert.Equal("/UserViews", fixture.Handler.Uri!.AbsolutePath);
        Assert.Contains("UserId=fixture-user", fixture.Handler.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("IncludeHidden=true", fixture.Handler.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("Token=\"session-token\"", fixture.Handler.Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubsonicListsNamedMusicFoldersWithNumericIdentifiers()
    {
        using var fixture = new Fixture("Subsonic", """
            {"subsonic-response":{"status":"ok","musicFolders":{"musicFolder":[
              {"id":2,"name":"Classical"},{"id":"1","name":"Albums"}
            ]}}}
            """);

        var result = Assert.IsType<OkObjectResult>(await fixture.Controller.GetMusicLibraries());

        Assert.Equal(new[] { ("1", "Albums"), ("2", "Classical") }, Libraries(result));
        Assert.Equal(HttpMethod.Post, fixture.Handler.Method);
        Assert.Equal("/rest/getMusicFolders.view", fixture.Handler.Uri!.AbsolutePath);
        Assert.Equal(string.Empty, fixture.Handler.Uri.Query);
        Assert.Contains("u=fixture-user", fixture.Handler.Body, StringComparison.Ordinal);
        Assert.Contains("t=session-token", fixture.Handler.Body, StringComparison.Ordinal);
        Assert.Contains("s=session-salt", fixture.Handler.Body, StringComparison.Ordinal);
        Assert.Contains("f=json", fixture.Handler.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Jellyfin", "{}", HttpStatusCode.OK)]
    [InlineData("Jellyfin", "private backend details", HttpStatusCode.BadGateway)]
    [InlineData("Jellyfin", "private backend details", HttpStatusCode.OK)]
    [InlineData("Subsonic", "{\"subsonic-response\":{\"status\":\"failed\",\"error\":{\"message\":\"private backend details\"}}}", HttpStatusCode.OK)]
    public async Task DiscoveryFailureReturnsSanitizedError(string backend, string response, HttpStatusCode status)
    {
        using var fixture = new Fixture(backend, response, status);

        var result = Assert.IsType<ObjectResult>(await fixture.Controller.GetMusicLibraries());

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        var error = JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetString();
        Assert.Equal("Could not load music libraries from the media server. Check media-server access and try again.", error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryRequiresAdministratorSession(bool hasListenerSession)
    {
        using var fixture = new Fixture("Jellyfin", "{\"Items\":[]}", administrator: false);
        if (!hasListenerSession)
            fixture.Controller.HttpContext.Items.Remove(AdminAuthSessionService.HttpContextSessionItemKey);

        var result = Assert.IsType<ObjectResult>(await fixture.Controller.GetMusicLibraries());

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(0, fixture.Handler.RequestCount);
    }

    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task MissingSessionAccessReturnsErrorWithoutContactingBackend(string backend)
    {
        using var fixture = new Fixture(backend, "{}", backendAccess: false);

        var result = Assert.IsType<ObjectResult>(await fixture.Controller.GetMusicLibraries());

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        Assert.Equal(0, fixture.Handler.RequestCount);
    }

    private static (string Id, string Name)[] Libraries(OkObjectResult result) =>
        JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .EnumerateArray().Select(item => (item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!)).ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        public RecordingHandler Handler { get; }
        public AdminUiController Controller { get; }

        public Fixture(string backend, string response, HttpStatusCode status = HttpStatusCode.OK, bool administrator = true,
            bool backendAccess = true)
        {
            Handler = new(response, status);
            _http = new(Handler);
            var clients = new Mock<IHttpClientFactory>();
            clients.Setup(item => item.CreateClient(BackendLibraryAccessResolver.HttpClientName)).Returns(_http);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Type"] = backend,
                [BackendMusicLibraries.SelectionKey] = "library-a"
            }).Build();
            var httpContext = new DefaultHttpContext();
            var source = new BackendLibraryPermissionSource(new HttpContextAccessor { HttpContext = httpContext },
                Mock.Of<IDbContextFactory<AllstarrDbContext>>(), null!,
                Options.Create(new JellyfinSettings { Url = "https://jellyfin.test" }),
                Options.Create(new SubsonicSettings { Url = "https://subsonic.test" }));
            _services = new ServiceCollection().AddSingleton<IBackendLibraryPermissionSource>(source)
                .AddSingleton(clients.Object).BuildServiceProvider();
            httpContext.RequestServices = _services;
            httpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
            {
                SessionId = "fixture",
                UserId = "fixture-user",
                UserName = "Fixture",
                BackendType = backend,
                BackendInstanceId = "primary",
                AllstarrUserId = Guid.CreateVersion7(),
                JellyfinAccessToken = backendAccess ? "session-token" : string.Empty,
                SubsonicReadAuthentication = backendAccess ? new Dictionary<string, string>
                {
                    ["u"] = "fixture-user",
                    ["t"] = "session-token",
                    ["s"] = "session-salt",
                    ["v"] = "1.16.1",
                    ["c"] = "allstarr"
                } : null,
                IsAdministrator = administrator,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            };
            Controller = new AdminUiController(configuration, Options.Create(new SpotifyApiSettings()),
                Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()), Options.Create(new AppleDownloadSettings()),
                Options.Create(new MusicBrainzSettings()), null!, null!, new ProviderAccountOptions(), null!)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };
        }

        public void Dispose()
        {
            _services.Dispose();
            _http.Dispose();
        }
    }

    public sealed class RecordingHandler(string response, HttpStatusCode status) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public string Authorization { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            Uri = request.RequestUri;
            Body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : string.Empty;
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
