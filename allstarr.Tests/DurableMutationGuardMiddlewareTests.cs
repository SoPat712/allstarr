using allstarr.Core.Storage;
using allstarr.Middleware;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class DurableMutationGuardMiddlewareTests
{
    [Fact]
    public async Task Mutation_WhenSelectedDatabaseUnavailable_ReturnsActionable503()
    {
        var nextCalled = false;
        var middleware = new DurableMutationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var options = Options("Postgres");
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Unavailable, errorCode: "database_unavailable");
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/admin/config";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, options, state, new StubStorageProbe(state));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("Postgres", body, StringComparison.Ordinal);
        Assert.Contains("database_unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadOnlyRequest_WhenSelectedDatabaseUnavailable_StillReachesProxySurface()
    {
        var nextCalled = false;
        var middleware = new DurableMutationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var options = Options("Postgres");
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Unavailable, errorCode: "database_unavailable");
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/System/Info/Public";

        await middleware.InvokeAsync(context, options, state, new StubStorageProbe(state));

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("/rest/star.view")]
    [InlineData("/rest/scrobble")]
    [InlineData("/rest/createPlaylist.view")]
    [InlineData("/REST/savePlayQueue.VIEW")]
    public async Task SubsonicGetMutation_WhenDatabaseUnavailable_IsGuarded(string path)
    {
        var nextCalled = false;
        var middleware = new DurableMutationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var options = Options("Postgres");
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Unavailable, errorCode: "database_unavailable");
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, options, state, new StubStorageProbe(state));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task SubsonicGetRead_WhenDatabaseUnavailable_StillReachesProxySurface()
    {
        var nextCalled = false;
        var middleware = new DurableMutationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var options = Options("Postgres");
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Unavailable, errorCode: "database_unavailable");
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/rest/getSong.view";

        await middleware.InvokeAsync(context, options, state, new StubStorageProbe(state));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Mutation_WhenStorageReady_Continues()
    {
        var nextCalled = false;
        var middleware = new DurableMutationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var options = Options("Sqlite");
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, "test-schema");
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/rest/scrobble.view";

        await middleware.InvokeAsync(context, options, state, new StubStorageProbe(state));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Mutation_RefreshesStaleReadyStateBeforeContinuing()
    {
        var nextCalled = false;
        var middleware = new DurableMutationGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var options = Options("Sqlite");
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, "startup-schema");
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/admin/config";
        context.Response.Body = new MemoryStream();
        var probe = new StubStorageProbe(state, () => state.Set(
            DurableStorageReadiness.Unavailable,
            errorCode: "database_unavailable"));

        await middleware.InvokeAsync(context, options, state, probe);

        Assert.Equal(1, probe.CheckCount);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    private static DurableStorageOptions Options(string provider) => new()
    {
        Provider = provider,
        ConnectionString = provider == "Postgres"
            ? "Host=database;Database=allstarr;Username=allstarr;Password=not-used"
            : "Data Source=:memory:"
    };

    private sealed class StubStorageProbe(
        DurableStorageState state,
        Action? onCheck = null) : IDurableStorageRuntimeProbe
    {
        public int CheckCount { get; private set; }

        public Task<DurableStorageSnapshot> CheckAsync(
            CancellationToken cancellationToken = default)
        {
            CheckCount++;
            onCheck?.Invoke();
            return Task.FromResult(state.GetSnapshot());
        }
    }
}
