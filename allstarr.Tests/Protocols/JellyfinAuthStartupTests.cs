using System.Diagnostics;
using System.Net;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class JellyfinAuthStartupTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("storage", 503)]
    [InlineData("disabled", 403)]
    [InlineData("unexpected", 500)]
    public async Task LocalIdentityFailuresDoNotReportUpstreamFailure(string failure, int status)
    {
        var factory = new Mock<IDbContextFactory<AllstarrDbContext>>();
        factory.Setup(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure switch
            {
                "disabled" => new UnauthorizedAccessException("Disabled fixture."),
                "storage" => new Microsoft.Data.Sqlite.SqliteException("Unavailable fixture.", 14),
                _ => new InvalidOperationException("Unexpected fixture.")
            });
        var state = new DurableStorageState(new StorageOptions());
        state.Set(DurableStorageReadiness.Ready);
        var resolver = new BackendIdentityResolver(factory.Object, state, new IdentityOptions(), new SystemPlatformClock());
        var backend = new Backend();
        var filter = CreateFilter(backend, resolver);
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = "/Artists/AlbumArtists";
        http.Request.Headers["X-Emby-Token"] = "fixture";
        var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()),
            [], new Dictionary<string, object?>(), new object());
        await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], context.Controller)));
        Assert.Equal(status, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task StartupWriterLongerThanCommandTimeoutDoesNotBlockExistingListenerVerification()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using (var schema = new AllstarrDbContext(database.Options))
        {
            await schema.Database.OpenConnectionAsync();
            await schema.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await schema.Database.EnsureCreatedAsync();
        }
        const int commandTimeoutSeconds = 1;
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(database.ConnectionString)
        { DefaultTimeout = commandTimeoutSeconds };
        var options = new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .UseSqlite(connection.ToString(), sqlite => sqlite.CommandTimeout(commandTimeoutSeconds)).Options;
        var factory = new Mock<IDbContextFactory<AllstarrDbContext>>();
        factory.Setup(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var db = new AllstarrDbContext(options);
                await db.Database.OpenConnectionAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=100;");
                return db;
            });
        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        using var shutdown = new CancellationTokenSource();
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(item => item.ApplicationStopping).Returns(shutdown.Token);
        var resolver = new BackendIdentityResolver(factory.Object, state, new IdentityOptions(), new SystemPlatformClock(), lifetime: lifetime.Object);
        var original = (await resolver.ResolveAsync(new("jellyfin", "listener", "Listener")))!;
        var backend = new Backend();
        var filter = CreateFilter(backend, resolver);
        await using var writer = new AllstarrDbContext(options);
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlRawAsync("UPDATE users SET DisplayName = DisplayName;");
        var held = Stopwatch.StartNew();
        var before = Task.Run(async () =>
        {
            await using var db = new AllstarrDbContext(options);
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=100;");
            (await db.Users.SingleAsync()).LastSeenAt = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(DbErrors.IsTransientConflict(error));
            return elapsed.Elapsed;
        });
        try
        {
            var baseline = await before;
            var minimumHold = TimeSpan.FromSeconds(commandTimeoutSeconds) + TimeSpan.FromMilliseconds(100);
            if (held.Elapsed < minimumHold) await Task.Delay(minimumHold - held.Elapsed);
            Assert.True(held.Elapsed > TimeSpan.FromSeconds(commandTimeoutSeconds));
            var after = Stopwatch.StartNew();
            for (var index = 0; index < 3; index++)
            {
                var http = new DefaultHttpContext();
                http.Request.Method = "GET";
                http.Request.Path = "/Artists/AlbumArtists";
                http.Request.Headers["X-Emby-Token"] = "listener-" + index;
                var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()),
                    [], new Dictionary<string, object?>(), new object());
                await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], context.Controller)))
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(context.Result);
                var principal = Assert.IsType<AllstarrPrincipal>(http.Items[BackendIdentityResolver.HttpContextPrincipalItemKey]);
                Assert.Equal(original.UserId, principal.UserId);
                Assert.Equal("Renamed", principal.DisplayName);
                Assert.False(principal.IsAdministrator);
            }
            after.Stop();
            Assert.Equal(3, backend.Calls);
            output.WriteLine("Before: blocked identity save failed after {0:F0} ms. After: three verified requests {1:F0} ms total; upstream calls {2}.",
                baseline.TotalMilliseconds, after.Elapsed.TotalMilliseconds, backend.Calls);
        }
        finally
        {
            await transaction.RollbackAsync();
            await before;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var db = new AllstarrDbContext(options);
            if ((await db.Users.SingleAsync(deadline.Token)).DisplayName == "Renamed") break;
            await Task.Delay(20, deadline.Token);
        }
        Assert.Equal(original.UserId, (await resolver.ResolveAsync(new("jellyfin", "listener", "Renamed")))!.UserId);
    }

    private static JellyfinAuthFilter CreateFilter(Backend backend, BackendIdentityResolver resolver)
    {
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(new HttpClient(backend));
        var cache = new DisabledApplicationCache();
        var proxy = new JellyfinProxyService(clients.Object, Options.Create(new JellyfinSettings { Url = "http://backend.test" }),
            new HttpContextAccessor(), NullLogger<JellyfinProxyService>.Instance, cache,
            new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new ConfigurationBuilder().Build());
        return new(proxy, resolver, NullLogger<JellyfinAuthFilter>.Instance);
    }

    private sealed class Backend : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/Users/Me", request.RequestUri!.AbsolutePath);
            Interlocked.Increment(ref Calls);
            await Task.Delay(150, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Id":"listener","Name":"Renamed","Policy":{"IsAdministrator":false}}""")
            };
        }
    }
}
