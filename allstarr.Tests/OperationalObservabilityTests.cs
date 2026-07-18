using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed class OperationalObservabilityTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));
    private TestDbContextFactory _factory = null!;
    private DurableStorageState _state = null!;
    private readonly Guid _providerAccountId = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var options = new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_root, "metrics.db")}"
        };
        _factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<AllstarrDbContext>()
                .UseSqlite(options.ConnectionString)
                .Options);
        await using var context = await _factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
        context.Jobs.Add(new DurableJobRecord
        {
            Id = Guid.CreateVersion7(),
            ScopeKey = "global",
            Type = "fixture",
            PayloadJson = "{}",
            IdempotencyKey = "fixture-job",
            State = DurableJobState.Failed,
            MaxAttempts = 3,
            AttemptCount = 3,
            AvailableAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            UpdatedAt = DateTimeOffset.UtcNow
        });
        context.OutboxMessages.Add(new OutboxMessageRecord
        {
            Id = Guid.CreateVersion7(),
            Type = "fixture.event",
            PayloadJson = "{}",
            State = OutboxMessageState.Pending,
            AvailableAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        context.Backups.Add(new BackupRecord
        {
            Id = Guid.CreateVersion7(),
            StorageProvider = "Sqlite",
            ArtifactPath = "/redacted/backup.sqlite",
            Sha256 = new string('a', 64),
            SchemaVersion = "fixture",
            ApplicationVersion = AppVersion.Version,
            Status = "verified",
            CreatedAt = DateTimeOffset.UtcNow,
            VerifiedAt = DateTimeOffset.UtcNow
        });
        context.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = _providerAccountId,
            ProviderId = "deezer",
            DisplayName = "private account label",
            Scope = ProviderAccountScope.Global,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        context.ProviderHealthRollups.Add(new ProviderHealthRollupRecord
        {
            Id = Guid.CreateVersion7(),
            ProviderAccountId = _providerAccountId,
            Capability = "download",
            WindowStart = DateTimeOffset.UtcNow.AddMinutes(-15),
            WindowEnd = DateTimeOffset.UtcNow,
            SampleCount = 4,
            SuccessCount = 3,
            FailureCount = 1,
            SuccessRate = 0.75,
            P50LatencyMilliseconds = 42,
            P95LatencyMilliseconds = 90,
            LastState = ProviderHealthState.Healthy,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        _state = new DurableStorageState(options);
        _state.Set(DurableStorageReadiness.Ready, "fixture");
    }

    [Fact]
    public async Task Metrics_ExposeDurableOperationalStateWithoutIdentifiersOrPaths()
    {
        var service = new OperationalMetricsService(_factory, _state);

        var metrics = await service.RenderPrometheusAsync();

        Assert.Contains("allstarr_storage_ready{provider=\"sqlite\"} 1", metrics, StringComparison.Ordinal);
        Assert.Contains("allstarr_jobs{state=\"failed\"} 1", metrics, StringComparison.Ordinal);
        Assert.Contains("allstarr_outbox_messages{state=\"pending\"} 1", metrics, StringComparison.Ordinal);
        Assert.Contains("allstarr_backup_age_seconds", metrics, StringComparison.Ordinal);
        Assert.Contains(
            "allstarr_provider_success_rate{provider=\"deezer\",capability=\"download\"} 0.75",
            metrics,
            StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-job", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("/redacted/", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain(_providerAccountId.ToString(), metrics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private account label", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_WhenStorageUnavailable_ExposeOnlySelectedProviderReadiness()
    {
        _state.Set(DurableStorageReadiness.Unavailable, errorCode: "database_unavailable");
        var service = new OperationalMetricsService(_factory, _state);

        var metrics = await service.RenderPrometheusAsync();

        Assert.Contains("allstarr_storage_ready{provider=\"sqlite\"} 0", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("allstarr_jobs{", metrics, StringComparison.Ordinal);
    }

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

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
