using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Collections.Generic;
using allstarr.Filters;
using allstarr.Models.Settings;

namespace allstarr.Tests;

public class ApiKeyAuthFilterTests
{
    private readonly Mock<ILogger<ApiKeyAuthFilter>> _loggerMock;
    private readonly IOptions<JellyfinSettings> _options;

    public ApiKeyAuthFilterTests()
    {
        _loggerMock = new Mock<ILogger<ApiKeyAuthFilter>>();
        _options = Options.Create(new JellyfinSettings { ApiKey = "secret-key" });
    }

    private static (ActionExecutingContext ExecContext, ActionContext ActionContext) CreateContext(HttpContext httpContext)
    {
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var execContext = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
        return (execContext, actionContext);
    }

    private static ActionExecutionDelegate CreateNext(ActionContext actionContext, Action onInvoke)
    {
        return () =>
        {
            onInvoke();
            var executedContext = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object());
            return Task.FromResult(executedContext);
        };
    }

    [Fact]
    public async Task OnActionExecutionAsync_WithValidHeader_AllowsRequest()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Api-Key"] = "secret-key";

        var (ctx, actionCtx) = CreateContext(httpContext);
        var filter = new ApiKeyAuthFilter(_options, _loggerMock.Object);

        var invoked = false;
        var next = CreateNext(actionCtx, () => invoked = true);

        // Act
        await filter.OnActionExecutionAsync(ctx, next);

        // Assert
        Assert.True(invoked, "Next delegate should be invoked for valid API key header");
        Assert.Null(ctx.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_WithValidQuery_AllowsRequest()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?api_key=secret-key");

        var (ctx, actionCtx) = CreateContext(httpContext);
        var filter = new ApiKeyAuthFilter(_options, _loggerMock.Object);

        var invoked = false;
        var next = CreateNext(actionCtx, () => invoked = true);

        // Act
        await filter.OnActionExecutionAsync(ctx, next);

        // Assert
        Assert.True(invoked, "Next delegate should be invoked for valid API key query");
        Assert.Null(ctx.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_WithXEmbyTokenHeader_AllowsRequest()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Emby-Token"] = "secret-key";

        var (ctx, actionCtx) = CreateContext(httpContext);
        var filter = new ApiKeyAuthFilter(_options, _loggerMock.Object);

        var invoked = false;
        var next = CreateNext(actionCtx, () => invoked = true);

        // Act
        await filter.OnActionExecutionAsync(ctx, next);

        // Assert
        Assert.True(invoked, "Next delegate should be invoked for valid X-Emby-Token header");
        Assert.Null(ctx.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_WithMissingKey_ReturnsUnauthorized()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        var (ctx, actionCtx) = CreateContext(httpContext);
        var filter = new ApiKeyAuthFilter(_options, _loggerMock.Object);

        var invoked = false;
        var next = CreateNext(actionCtx, () => invoked = true);

        // Act
        await filter.OnActionExecutionAsync(ctx, next);

        // Assert
        Assert.False(invoked, "Next delegate should not be invoked when API key is missing");
        Assert.IsType<UnauthorizedObjectResult>(ctx.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_WithWrongKey_ReturnsUnauthorized()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Api-Key"] = "wrong-key";

        var (ctx, actionCtx) = CreateContext(httpContext);
        var filter = new ApiKeyAuthFilter(_options, _loggerMock.Object);

        var invoked = false;
        var next = CreateNext(actionCtx, () => invoked = true);

        // Act
        await filter.OnActionExecutionAsync(ctx, next);

        // Assert
        Assert.False(invoked, "Next delegate should not be invoked for wrong API key");
        Assert.IsType<UnauthorizedObjectResult>(ctx.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_ConstantTimeComparison_WorksForDifferentLengths()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Api-Key"] = "short";

        var (ctx, actionCtx) = CreateContext(httpContext);
        var filter = new ApiKeyAuthFilter(Options.Create(new JellyfinSettings { ApiKey = "much-longer-secret-key" }), _loggerMock.Object);

        var invoked = false;
        var next = CreateNext(actionCtx, () => invoked = true);

        // Act
        await filter.OnActionExecutionAsync(ctx, next);

        // Assert
        Assert.False(invoked, "Next should not be invoked for wrong key");
        Assert.IsType<UnauthorizedObjectResult>(ctx.Result);
    }
}
