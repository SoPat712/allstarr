using allstarr.Core.Operations;
using allstarr.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed class OperationalObservabilityTests
{
    [Fact]
    public async Task CorrelationMiddleware_PreservesSafeIdInRequestScope()
    {
        var observed = string.Empty;
        var middleware = new CorrelationMiddleware(
            context =>
            {
                observed = context.Items[CorrelationMiddleware.HttpContextItemKey]?.ToString() ?? string.Empty;
                return Task.CompletedTask;
            },
            NullLogger<CorrelationMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = "fixture.trace-123";

        await middleware.InvokeAsync(context);

        Assert.Equal("fixture.trace-123", observed);
    }

    [Fact]
    public async Task CorrelationMiddleware_RejectsHeaderInjection()
    {
        var observed = string.Empty;
        var middleware = new CorrelationMiddleware(
            context =>
            {
                observed = context.Items[CorrelationMiddleware.HttpContextItemKey]?.ToString() ?? string.Empty;
                return Task.CompletedTask;
            },
            NullLogger<CorrelationMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = "bad\r\nsecret: value";

        await middleware.InvokeAsync(context);

        Assert.NotEqual("bad\r\nsecret: value", observed);
        Assert.DoesNotContain('\r', observed);
        Assert.DoesNotContain('\n', observed);
    }

    [Fact]
    public void RuntimeLogger_RedactsSecretsButKeepsUsefulUrlAndPathContext()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        using var provider = new RedactingConsoleLoggerProvider(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Debug"
            }).Build(),
            output,
            error);
        var logger = provider.CreateLogger("allstarr.fixture");

        logger.LogError(
            new InvalidOperationException(
                "request https://provider.invalid/media?token=private-token failed"),
            "Provider {Provider} request {Url} token={Token} failed at {Path} using {ConnectionString}",
            "deezer",
            "https://provider.invalid/media?token=private-token",
            "private-token",
            "/media/private/track.flac",
            "Host=database;Password=database-secret");

        var log = error.ToString();
        Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("deezer", log, StringComparison.Ordinal);
        Assert.Contains("redacted", log, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", log, StringComparison.Ordinal);
        Assert.Contains("provider.invalid", log, StringComparison.Ordinal);
        Assert.Contains("/media/private", log, StringComparison.Ordinal);
        Assert.DoesNotContain("request https", log, StringComparison.Ordinal);
        Assert.DoesNotContain("database-secret", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeLogger_KeepsNonsecretContentAndMultipleAddresses()
    {
        var output = new StringWriter();
        using var provider = new RedactingConsoleLoggerProvider(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information"
            }).Build(), output, TextWriter.Null);
        var logger = provider.CreateLogger("allstarr.fixture");

        logger.LogInformation("Serving {ContentRoot} on {Addresses}", "/app", "http://[::]:8080/;http://[::]:5275/");

        var log = output.ToString();
        Assert.Contains("/app", log, StringComparison.Ordinal);
        Assert.Contains("http://[::]:8080/", log, StringComparison.Ordinal);
        Assert.Contains("http://[::]:5275/", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeLogger_IncludesSafeCorrelationScopeAndRedactsScopedSecrets()
    {
        var output = new StringWriter();
        using var provider = new RedactingConsoleLoggerProvider(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information"
            }).Build(),
            output,
            TextWriter.Null);
        var logger = provider.CreateLogger("allstarr.fixture");

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = "fixture-correlation-123",
            ["ConnectionString"] = "Host=database;Password=scoped-secret"
        }))
        {
            logger.LogInformation("Scoped operation completed");
        }

        var log = output.ToString();
        Assert.Contains("fixture-correlation-123", log, StringComparison.Ordinal);
        Assert.Contains("redacted", log, StringComparison.Ordinal);
        Assert.DoesNotContain("scoped-secret", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeLogger_RedactsGenericMessageAndResponseFields()
    {
        var error = new StringWriter();
        using var provider = new RedactingConsoleLoggerProvider(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Debug"
            }).Build(),
            TextWriter.Null,
            error);
        var logger = provider.CreateLogger("allstarr.fixture");

        logger.LogWarning(
            "Provider failed with {Message}; response was {Response}",
            "opaque-private-token",
            "raw upstream account payload");

        var log = error.ToString();
        Assert.Contains("redacted", log, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-private-token", log, StringComparison.Ordinal);
        Assert.DoesNotContain("raw upstream account payload", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeLogger_HonorsLongestCategoryOverride()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        using var provider = new RedactingConsoleLoggerProvider(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Error"
            }).Build(),
            output,
            error);
        var logger = provider.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        logger.LogInformation("Routine database command");
        logger.LogWarning("Routine database warning");
        logger.LogError("Database command failed");

        Assert.Empty(output.ToString());
        Assert.DoesNotContain("Routine database warning", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Database command failed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlobalExceptionHandler_LogsSafeRoutePatternAndErrorClassification()
    {
        var error = new StringWriter();
        var provider = new RedactingConsoleLoggerProvider(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information"
            }).Build(),
            TextWriter.Null,
            error);
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(provider);
        });
        var handler = new GlobalExceptionHandler(loggerFactory.CreateLogger<GlobalExceptionHandler>());
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("api/admin/extensions/registries"),
            order: 0,
            EndpointMetadataCollection.Empty,
            displayName: "fixture"));

        await handler.TryHandleAsync(context, new InvalidOperationException("private detail"), default);

        var log = error.ToString();
        Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("api/admin/extensions/registries", log, StringComparison.Ordinal);
        Assert.Contains("400", log, StringComparison.Ordinal);
        Assert.DoesNotContain("private detail", log, StringComparison.Ordinal);
    }

}
