using allstarr.Controllers;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Lyrics;
using allstarr.Services.Admin;
using allstarr.Services.Jellyfin;
using allstarr.Services.Subsonic;
using allstarr.Services.Common;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class RecordingLyricsControllerTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _recording = Guid.NewGuid();
    private Factory _factory = null!;
    private readonly Mock<IRecordingLyricsReview> _review = new(MockBehavior.Strict);

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        await using var db = _factory.CreateDbContext();
        db.Users.Add(new UserRecord
        {
            Id = _user,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            DisplayName = "Listener",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task ReadChooseAndClearUseCurrentListenerAndCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new LyricsChoiceRevision();
        var layers = new RecordingLyricsChoiceLayers(null, null);
        _review.Setup(service => service.GetAsync(It.Is<ProtocolExecutionContext>(context =>
                context.Principal!.UserId == _user && context.CancellationToken == cancellation.Token),
                _recording, "personal", It.IsAny<Func<string, CancellationToken, Task<LyricsInfo?>>?>()))
            .ReturnsAsync(new RecordingLyricsReviewResult(_recording, "personal", new("Song", "Artist", "", 10), layers, []));
        _review.Setup(service => service.ChooseAsync(It.Is<ProtocolExecutionContext>(context => context.Principal!.UserId == _user),
                _recording, "lrclib", "personal", expected, "sha256:fixture", It.IsAny<Func<string, CancellationToken, Task<LyricsInfo?>>?>()))
            .ReturnsAsync(layers);
        _review.Setup(service => service.ClearAsync(It.Is<ProtocolExecutionContext>(context => context.Principal!.UserId == _user),
                _recording, "personal", expected)).ReturnsAsync(layers);
        var controller = Controller();
        Assert.IsType<OkObjectResult>(await controller.Get(_recording, cancellation.Token));
        Assert.IsType<OkObjectResult>(await controller.Choose(_recording, new("lrclib", "personal", expected, "sha256:fixture"), cancellation.Token));
        Assert.IsType<OkObjectResult>(await controller.Clear(_recording, new("personal", expected), cancellation.Token));
        _review.VerifyAll();
        _review.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingAndUnlinkedSessionsCannotReadOrWrite()
    {
        var controller = Controller(linked: false);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Get(_recording, default)).StatusCode);
        controller.HttpContext.Items.Clear();
        Assert.IsType<UnauthorizedResult>(await controller.Choose(_recording, new("lrclib", "personal", new(), "revision"), default));
        Assert.IsType<UnauthorizedResult>(await controller.Clear(_recording, new("personal", new()), default));
        _review.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrRoleChangedSessionCannotReachReview(bool roleChanged)
    {
        await using var db = _factory.CreateDbContext();
        var user = await db.Users.SingleAsync();
        if (roleChanged) user.IsAdmin = true;
        else user.Enabled = false;
        await db.SaveChangesAsync();
        var result = Assert.IsType<ObjectResult>(await Controller().Get(_recording, default));
        Assert.Equal(403, result.StatusCode);
        _review.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("missing", 404)]
    [InlineData("conflict", 409)]
    [InlineData("forbidden", 403)]
    [InlineData("invalid", 400)]
    [InlineData("upstream", 503)]
    public async Task ErrorsUseSafeMessages(string kind, int status)
    {
        Exception exception = kind switch
        {
            "missing" => new KeyNotFoundException("private detail"),
            "conflict" => new LyricsChoiceConflictException("private detail"),
            "forbidden" => new UnauthorizedAccessException("private detail"),
            "invalid" => new ArgumentException("private detail"),
            _ => new HttpRequestException("private detail")
        };
        _review.Setup(service => service.GetAsync(It.IsAny<ProtocolExecutionContext>(), _recording,
            "personal", It.IsAny<Func<string, CancellationToken, Task<LyricsInfo?>>?>())).ThrowsAsync(exception);
        var result = Assert.IsAssignableFrom<ObjectResult>(await Controller().Get(_recording, default));
        Assert.Equal(status, result.StatusCode);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller().Get(_recording, cancellation.Token));
        _review.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("jellyfin", true)]
    [InlineData("jellyfin", false)]
    [InlineData("subsonic", true)]
    [InlineData("subsonic", false)]
    public async Task NativeReadsUseOnlyTheLinkedSessionCredentials(string backend, bool hasCredentials)
    {
        await using (var db = _factory.CreateDbContext())
        {
            (await db.Users.SingleAsync()).BackendType = backend;
            await db.SaveChangesAsync();
        }
        using var handler = new NativeHandler(backend);
        using var client = new HttpClient(handler);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var cache = new DisabledApplicationCache();
        var jellyfin = new JellyfinProxyService(clients.Object,
            Options.Create(new JellyfinSettings { Url = "http://backend.invalid", ApiKey = "operator-token" }),
            accessor, NullLogger<JellyfinProxyService>.Instance, cache,
            new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new ConfigurationBuilder().Build());
        var subsonic = new SubsonicProxyService(clients.Object,
            Options.Create(new SubsonicSettings { Url = "http://backend.invalid" }), accessor);
        _review.Setup(service => service.GetAsync(It.IsAny<ProtocolExecutionContext>(), _recording,
                "personal", It.IsAny<Func<string, CancellationToken, Task<LyricsInfo?>>?>()))
            .Returns(async (ProtocolExecutionContext context, Guid _, string _, Func<string, CancellationToken, Task<LyricsInfo?>>? read) =>
            {
                Assert.NotNull(read);
                var lyrics = await read("native-song", context.CancellationToken);
                if (hasCredentials) Assert.Equal("Native words", lyrics?.PlainLyrics);
                else Assert.Null(lyrics);
                return new RecordingLyricsReviewResult(_recording, "personal", new("Song", "Artist", "", 10), new(null, null), []);
            });
        var controller = Controller(backend: backend, hasCredentials: hasCredentials, jellyfin: jellyfin, subsonic: subsonic);
        Assert.IsType<OkObjectResult>(await controller.Get(_recording, default));
        Assert.Equal(hasCredentials ? 1 : 0, handler.Calls);
    }

    private sealed class NativeHandler(string backend) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.DoesNotContain("operator-token", request.ToString());
            if (backend == "jellyfin")
            {
                Assert.Contains("Token=\"fixture-token\"", Assert.Single(request.Headers.GetValues("Authorization")), StringComparison.Ordinal);
                Assert.EndsWith("/Audio/native-song/Lyrics", request.RequestUri!.AbsolutePath);
            }
            else
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
                Assert.Equal("listener", query["u"]);
                Assert.Equal("fixture-password", query["p"]);
                Assert.Equal("native-song", query["id"]);
                Assert.Equal("json", query["f"]);
            }
            var body = backend == "jellyfin"
                ? """{"Metadata":{"IsSynced":false},"Lyrics":[{"Text":"Native words"}]}"""
                : """{"subsonic-response":{"status":"ok","lyricsList":{"structuredLyrics":[{"synced":false,"line":[{"value":"Native words"}]}]}}}""";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private RecordingLyricsController Controller(bool linked = true, string backend = "jellyfin", bool hasCredentials = true,
        JellyfinProxyService? jellyfin = null, SubsonicProxyService? subsonic = null)
    {
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "listener",
            UserName = "Listener",
            IsAdministrator = false,
            BackendType = backend,
            BackendInstanceId = "fixture",
            AllstarrUserId = linked ? _user : null,
            JellyfinAccessToken = hasCredentials ? "fixture-token" : "",
            SubsonicReadAuthentication = hasCredentials ? new() { ["u"] = "listener", ["p"] = "fixture-password" } : null,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return new(_review.Object, new(_factory, new Clock()), jellyfin, subsonic) { ControllerContext = new() { HttpContext = http } };
    }

    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
