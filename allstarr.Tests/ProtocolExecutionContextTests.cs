using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Middleware;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ProtocolExecutionContextTests
{
    [Fact]
    public void ResolvedPrincipal_ProjectsOnlyVerifiedIdentityIntoProviderActor()
    {
        var now = new DateTimeOffset(2026, 7, 11, 18, 0, 0, TimeSpan.Zero);
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var http = new DefaultHttpContext { TraceIdentifier = "trace-fallback" };
        http.Items[CorrelationMiddleware.HttpContextItemKey] = "correlation-17";
        http.Items[BackendIdentityResolver.HttpContextPrincipalItemKey] = new AllstarrPrincipal(
            tenantId,
            userId,
            "jellyfin",
            "primary",
            "backend-user-17",
            "Listener",
            IsAdministrator: false);
        http.Request.Headers.Authorization = "Bearer must-not-enter-context";
        var factory = new ProtocolExecutionContextFactory(
            new ProtocolExecutionOptions { OperationTimeoutSeconds = 20 },
            new FakeClock(now),
            new IdentityOptions { BackendInstanceId = "primary" });

        var context = factory.Create(
            http,
            ProtocolKind.Jellyfin,
            "backend-user-17",
            "primary",
            new ProtocolClientDescriptor("finamp", "phone-17", "Phone"),
            "music-library");

        Assert.True(context.CanRunUserScopedWork);
        Assert.Equal(tenantId, context.RequireActor().TenantId);
        Assert.Equal(userId, context.Actor!.UserId);
        Assert.Equal(ProviderActorKind.User, context.Actor.Kind);
        Assert.Equal("backend-user-17", context.Actor.BackendPrincipal!.PrincipalId);
        Assert.Equal("correlation-17", context.CorrelationId);
        Assert.Equal(now.AddSeconds(20), context.Deadline);
        Assert.Equal("music-library", context.LibraryScopeId);
        Assert.DoesNotContain(
            "must-not-enter-context",
            System.Text.Json.JsonSerializer.Serialize(context),
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnlinkedVerifiedPrincipal_RemainsTransparentButCannotRunUserWork()
    {
        var http = new DefaultHttpContext { TraceIdentifier = "trace-unlinked" };
        var context = new ProtocolExecutionContextFactory(
            new ProtocolExecutionOptions(),
            new FakeClock(DateTimeOffset.UtcNow),
            new IdentityOptions { BackendInstanceId = "primary" }).Create(
            http,
            ProtocolKind.Subsonic,
            "listener",
            "primary");

        Assert.False(context.CanRunUserScopedWork);
        Assert.Null(context.Principal);
        Assert.Throws<UnauthorizedAccessException>(() => context.RequireActor());
    }

    [Fact]
    public void CanonicalPrincipalCannotBeReusedForAnotherProtocolOrBackendIdentity()
    {
        var principal = new AllstarrPrincipal(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "jellyfin",
            "primary",
            "verified-id",
            "Listener",
            IsAdministrator: true);

        Assert.Throws<UnauthorizedAccessException>(() => new ProtocolExecutionContext(
            ProtocolKind.Subsonic,
            "primary",
            "verified-id",
            principal,
            "correlation",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None));
        Assert.Throws<UnauthorizedAccessException>(() => new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            "primary",
            "different-id",
            principal,
            "correlation",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void TimeoutConfiguration_IsBounded(int seconds)
    {
        var options = new ProtocolExecutionOptions { OperationTimeoutSeconds = seconds };

        Assert.Throws<InvalidOperationException>(() => options.GetOperationTimeout());
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, JellyfinAuthFilter.BackendPrincipalIdItemKey)]
    [InlineData(ProtocolKind.Subsonic, SubsonicAuthFilter.BackendPrincipalNameItemKey)]
    public async Task Filter_ProjectsVerifiedProtocolPrincipalBeforeActionRuns(
        ProtocolKind protocol,
        string principalItemKey)
    {
        var http = new DefaultHttpContext { TraceIdentifier = "protocol-request" };
        http.Items[principalItemKey] = "verified-listener";
        if (protocol == ProtocolKind.Jellyfin)
        {
            http.Request.Headers["X-Emby-Authorization"] =
                "MediaBrowser Client=\"Finamp\", DeviceId=\"phone-7\", Token=\"secret\"";
        }
        else
        {
            http.Items[SubsonicAuthFilter.RequestParametersItemKey] =
                SubsonicRequestParameters.FromDictionary(new Dictionary<string, string>
                {
                    ["c"] = "Tempo",
                    ["musicFolderId"] = "folder-4"
                });
        }
        var actionContext = ActionExecuting(http);
        var filter = new ProtocolExecutionContextFilter(
            new ProtocolExecutionContextFactory(
                new ProtocolExecutionOptions(),
                new FakeClock(new DateTimeOffset(2026, 7, 11, 19, 0, 0, TimeSpan.Zero)),
                new IdentityOptions { BackendInstanceId = "primary" }),
            NullLogger<ProtocolExecutionContextFilter>.Instance);
        var actionRan = false;

        await filter.OnActionExecutionAsync(actionContext, () =>
        {
            actionRan = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(
                    actionContext.HttpContext,
                    actionContext.RouteData,
                    actionContext.ActionDescriptor,
                    actionContext.ModelState),
                actionContext.Filters,
                actionContext.Controller));
        });

        Assert.True(actionRan);
        var projected = Assert.IsType<ProtocolExecutionContext>(
            http.Items[ProtocolExecutionContextFactory.HttpContextItemKey]);
        Assert.Equal(protocol, projected.Protocol);
        Assert.Equal("verified-listener", projected.VerifiedBackendPrincipalId);
        Assert.Equal("primary", projected.BackendInstanceId);
        Assert.Null(projected.Actor);
        Assert.Equal(protocol == ProtocolKind.Jellyfin ? "Finamp" : "Tempo", projected.Client.ClientId);
        Assert.Equal(protocol == ProtocolKind.Jellyfin ? "phone-7" : null, projected.Client.DeviceId);
        Assert.Equal(protocol == ProtocolKind.Subsonic ? "folder-4" : null, projected.LibraryScopeId);
        Assert.DoesNotContain(
            "secret",
            System.Text.Json.JsonSerializer.Serialize(projected),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filter_RejectsConflictingVerifiedProtocolPrincipals()
    {
        var http = new DefaultHttpContext();
        http.Items[JellyfinAuthFilter.BackendPrincipalIdItemKey] = "jellyfin-user";
        http.Items[SubsonicAuthFilter.BackendPrincipalNameItemKey] = "subsonic-user";
        var actionContext = ActionExecuting(http);
        var filter = new ProtocolExecutionContextFilter(
            new ProtocolExecutionContextFactory(
                new ProtocolExecutionOptions(),
                new FakeClock(DateTimeOffset.UtcNow),
                new IdentityOptions { BackendInstanceId = "primary" }),
            NullLogger<ProtocolExecutionContextFilter>.Instance);
        var actionRan = false;

        await filter.OnActionExecutionAsync(actionContext, () =>
        {
            actionRan = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(
                    actionContext.HttpContext,
                    actionContext.RouteData,
                    actionContext.ActionDescriptor,
                    actionContext.ModelState),
                actionContext.Filters,
                actionContext.Controller));
        });

        Assert.False(actionRan);
        var result = Assert.IsType<StatusCodeResult>(actionContext.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.False(http.Items.ContainsKey(ProtocolExecutionContextFactory.HttpContextItemKey));
    }

    [Fact]
    public async Task Filter_LeavesPublicBootstrapRequestWithoutAProtocolContext()
    {
        var http = new DefaultHttpContext();
        var actionContext = ActionExecuting(http);
        var filter = new ProtocolExecutionContextFilter(
            new ProtocolExecutionContextFactory(
                new ProtocolExecutionOptions(),
                new FakeClock(DateTimeOffset.UtcNow),
                new IdentityOptions { BackendInstanceId = "primary" }),
            NullLogger<ProtocolExecutionContextFilter>.Instance);
        var actionRan = false;

        await filter.OnActionExecutionAsync(actionContext, () =>
        {
            actionRan = true;
            return Task.FromResult(new ActionExecutedContext(
                new ActionContext(
                    actionContext.HttpContext,
                    actionContext.RouteData,
                    actionContext.ActionDescriptor,
                    actionContext.ModelState),
                actionContext.Filters,
                actionContext.Controller));
        });

        Assert.True(actionRan);
        Assert.Null(http.GetProtocolExecutionContext());
    }

    [Fact]
    public void HttpContextAccessor_RequiresThePostAuthenticationContext()
    {
        var http = new DefaultHttpContext();

        Assert.Null(http.GetProtocolExecutionContext());
        Assert.Throws<InvalidOperationException>(() => http.RequireProtocolExecutionContext());

        var projected = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            "primary",
            "listener",
            principal: null,
            "correlation",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = projected;

        Assert.Same(projected, http.GetProtocolExecutionContext());
        Assert.Same(projected, http.RequireProtocolExecutionContext());
    }

    private static ActionExecutingContext ActionExecuting(HttpContext http)
    {
        var action = new ActionContext(
            http,
            new RouteData(),
            new ActionDescriptor());
        return new ActionExecutingContext(
            action,
            [],
            new Dictionary<string, object?>(),
            controller: new object());
    }

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
