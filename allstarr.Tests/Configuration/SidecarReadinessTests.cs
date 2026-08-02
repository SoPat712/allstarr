using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Health;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class SidecarReadinessTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-sidecar-readiness",
        Guid.NewGuid().ToString("N"));
    private PostgresTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private DurableStorageState _storageState = null!;
    private FakeClock _clock = null!;
    private DurableProviderHealthStore _health = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = await PostgresTestDatabase.CreateAsync();
        var storage = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = _database.ConnectionString
        };
        _factory = new TestDbContextFactory(_database.Options);
        await using var context = await _factory.CreateDbContextAsync();
        context.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = AccountId("applemusic", "legacy-global"),
            ProviderId = "applemusic",
            DisplayName = "Legacy global applemusic",
            Scope = ProviderAccountScope.Global,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        _storageState = new DurableStorageState(storage);
        _storageState.Set(DurableStorageReadiness.Ready, "fixture");
        _clock = new FakeClock(DateTimeOffset.UtcNow);
        _health = new DurableProviderHealthStore(
            _factory,
            _storageState,
            new ProviderHealthOptions(),
            _clock);
    }

    [Fact]
    public async Task MissingOptionalSidecar_DegradesOnlyItsCapabilityAndKeepsPlatformReady()
    {
        var sidecarOptions = new SidecarHealthOptions
        {
            Targets =
            [
                new SidecarProbeTarget
                {
                    Id = "apple-download",
                    ProviderId = "applemusic",
                    Required = false,
                    BaseUrl = null,
                    Capabilities = ["download"]
                }
            ]
        };
        var catalog = new SidecarStatusCatalog(sidecarOptions);
        var readiness = Readiness(catalog, new ReadinessOptions());

        var snapshot = await readiness.CheckAsync();

        Assert.True(snapshot.Ready);
        var sidecar = Assert.Single(snapshot.Components, item => item.Id == "sidecar:apple-download");
        Assert.Equal("not_installed", sidecar.State);
        Assert.False(sidecar.Required);
        var blocked = new SidecarJobGate(catalog).Check("apple-download", TimeSpan.FromSeconds(30));
        Assert.NotNull(blocked);
        Assert.Equal(DurableJobCompletionKind.Deferred, blocked.Kind);
        Assert.Equal("sidecar_not_installed", blocked.ErrorCode);
    }

    [Fact]
    public async Task RuntimeStorageFailureIsReflectedByReadinessImmediately()
    {
        var readiness = Readiness(
            new SidecarStatusCatalog(new SidecarHealthOptions()),
            new ReadinessOptions(),
            new UnavailableStorageProbe(_storageState));

        var snapshot = await readiness.CheckAsync();

        Assert.False(snapshot.Ready);
        Assert.Contains(snapshot.Components, component =>
            component.Id == "storage:postgres" &&
            component.State == "unavailable" &&
            component.ErrorCode == "database_unavailable");
    }

    [Fact]
    public async Task MissingRequiredSidecar_FailsReadinessWithActionableComponent()
    {
        var sidecarOptions = new SidecarHealthOptions
        {
            Targets =
            [
                new SidecarProbeTarget
                {
                    Id = "required-sidecar",
                    ProviderId = "fixture",
                    Required = true
                }
            ]
        };
        var readiness = Readiness(new SidecarStatusCatalog(sidecarOptions), new ReadinessOptions());

        var snapshot = await readiness.CheckAsync();

        Assert.False(snapshot.Ready);
        Assert.Contains(snapshot.Components, item =>
            item.Id == "sidecar:required-sidecar" &&
            item.ErrorCode == "sidecar_not_installed");
    }

    [Fact]
    public async Task IncompatibleThenHealthySidecar_UpdatesCatalogAndDurableCapabilityHealth()
    {
        var options = new SidecarHealthOptions
        {
            ProbeTimeoutSeconds = 2,
            Targets =
            [
                new SidecarProbeTarget
                {
                    Id = "apple-download",
                    ProviderId = "applemusic",
                    BaseUrl = "http://sidecar.test/",
                    HealthPath = "/health",
                    ExpectedApiVersion = "0.0.2",
                    RequireAuthenticated = true,
                    Capabilities = ["download", "streaming"]
                }
            ]
        };
        var catalog = new SidecarStatusCatalog(options);
        var handler = new QueueHandler(
            Json("""{"api_version":"0.0.1","logged_in":true}"""),
            Json("""{"api_version":"0.0.2","logged_in":true}"""));
        var monitor = new SidecarHealthMonitor(
            new HandlerFactory(handler),
            options,
            catalog,
            _health,
            NullLogger<SidecarHealthMonitor>.Instance,
            _clock);

        await monitor.ProbeAllOnceAsync();
        Assert.Equal(SidecarRuntimeState.Incompatible, Assert.Single(catalog.GetAll()).State);
        Assert.True(_health.TryGetLatest("applemusic", "legacy-global", "download", out var incompatible));
        Assert.Equal(ProviderHealthState.Unavailable, incompatible.State);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await monitor.ProbeAllOnceAsync();
        Assert.Equal(SidecarRuntimeState.Ready, Assert.Single(catalog.GetAll()).State);
        Assert.True(_health.TryGetLatest("applemusic", "legacy-global", "download", out var ready));
        Assert.Equal(ProviderHealthState.Healthy, ready.State);
        Assert.Null(new SidecarJobGate(catalog).Check("apple-download"));
    }

    [Fact]
    public async Task TransientHealthWriteFailure_DoesNotStopLaterProbeAndRecovery()
    {
        var options = new SidecarHealthOptions
        {
            ProbeJitterSeconds = 0,
            Targets =
            [
                new SidecarProbeTarget
                {
                    Id = "apple-download",
                    ProviderId = "applemusic",
                    BaseUrl = "http://sidecar.test/",
                    Capabilities = ["download"]
                }
            ]
        };
        var handler = new QueueHandler(Json("{}"), Json("{}"));
        var catalog = new SidecarStatusCatalog(options);
        var health = new FlakyHealthObservationStore(failuresBeforeSuccess: 1);
        var monitor = new SidecarHealthMonitor(
            new HandlerFactory(handler),
            options,
            catalog,
            health,
            NullLogger<SidecarHealthMonitor>.Instance,
            _clock,
            _storageState,
            new ReadyStorageProbe(_storageState));

        await monitor.ProbeAllOnceAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, health.WriteAttempts);
        Assert.Equal(0, health.SuccessfulWrites);
        Assert.Equal(SidecarRuntimeState.Ready, Assert.Single(catalog.GetAll()).State);

        await monitor.ProbeAllOnceAsync();

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, health.WriteAttempts);
        Assert.Equal(1, health.SuccessfulWrites);
        Assert.Equal(SidecarRuntimeState.Ready, Assert.Single(catalog.GetAll()).State);
    }

    [Fact]
    public async Task RefreshedUnavailableStorage_SkipsHealthWriteWithoutSkippingProbe()
    {
        var options = new SidecarHealthOptions
        {
            Targets =
            [
                new SidecarProbeTarget
                {
                    Id = "apple-download",
                    ProviderId = "applemusic",
                    BaseUrl = "http://sidecar.test/",
                    Capabilities = ["download"]
                }
            ]
        };
        var handler = new QueueHandler(Json("{}"));
        var health = new FlakyHealthObservationStore(failuresBeforeSuccess: 0);
        var monitor = new SidecarHealthMonitor(
            new HandlerFactory(handler),
            options,
            new SidecarStatusCatalog(options),
            health,
            NullLogger<SidecarHealthMonitor>.Instance,
            _clock,
            _storageState,
            new UnavailableStorageProbe(_storageState));

        await monitor.ProbeAllOnceAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, health.WriteAttempts);
    }

    [Fact]
    public async Task DisabledProbe_PerformsNoRequestOrHealthMutation()
    {
        var options = new SidecarHealthOptions
        {
            Targets =
            [
                new SidecarProbeTarget
                {
                    Id = "apple-download",
                    ProviderId = "applemusic",
                    BaseUrl = "http://sidecar.test/",
                    ProbeEnabled = false,
                    Capabilities = ["download"]
                }
            ]
        };
        var handler = new QueueHandler(Json("{}"));
        var catalog = new SidecarStatusCatalog(options);
        var monitor = new SidecarHealthMonitor(
            new HandlerFactory(handler),
            options,
            catalog,
            _health,
            NullLogger<SidecarHealthMonitor>.Instance,
            _clock);

        await monitor.ProbeAllOnceAsync();

        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(SidecarRuntimeState.ProbeDisabled, Assert.Single(catalog.GetAll()).State);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Empty(await context.ProviderHealthSamples.ToListAsync());
    }

    [Fact]
    public async Task UnauthorizedProbe_OpensCircuitAndRateLimitsRecheckUntilRetryWindow()
    {
        var options = new SidecarHealthOptions
        {
            Targets =
            [
                new SidecarProbeTarget
                {
                    Id = "apple-download",
                    ProviderId = "applemusic",
                    BaseUrl = "http://sidecar.test/",
                    Capabilities = ["download"]
                }
            ]
        };
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            Json("{}"));
        var catalog = new SidecarStatusCatalog(options);
        var monitor = new SidecarHealthMonitor(
            new HandlerFactory(handler),
            options,
            catalog,
            _health,
            NullLogger<SidecarHealthMonitor>.Instance,
            _clock);

        await monitor.ProbeAllOnceAsync();
        await monitor.ProbeAllOnceAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("sidecar_circuit_open", Assert.Single(catalog.GetAll()).ErrorCode);

        _clock.Advance(TimeSpan.FromSeconds(61));
        await monitor.ProbeAllOnceAsync();

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(SidecarRuntimeState.Ready, Assert.Single(catalog.GetAll()).State);
    }

    [Fact]
    public void ProbePolicy_RejectsUnboundedOrAggressiveConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() => new SidecarHealthOptions
        {
            ProbeIntervalSeconds = 1
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new SidecarHealthOptions
        {
            MaxProbesPerCycle = 1000
        }.Validate());
    }

    [Fact]
    public void UndeclaredSidecar_IsAConfigurationFailureInsteadOfAnInfiniteDeferral()
    {
        var gate = new SidecarJobGate(new SidecarStatusCatalog(new SidecarHealthOptions()));

        var result = gate.Check("missing-definition");

        Assert.NotNull(result);
        Assert.Equal(DurableJobCompletionKind.Failed, result.Kind);
        Assert.Equal("sidecar_unknown", result.ErrorCode);
    }

    [Fact]
    public async Task RequiredSecretKeyRing_IsAReadinessDependencyWithoutExposingPath()
    {
        var readiness = Readiness(
            new SidecarStatusCatalog(new SidecarHealthOptions()),
            new ReadinessOptions { RequireSecretKeyRing = true });

        var snapshot = await readiness.CheckAsync();

        Assert.False(snapshot.Ready);
        var keyRing = Assert.Single(snapshot.Components, item => item.Id == "secret-key-ring");
        Assert.Equal("secret_key_ring_unavailable", keyRing.ErrorCode);
        Assert.DoesNotContain(_root, System.Text.Json.JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadinessPreservesCallerCancellationFromSecretProbe()
    {
        var keyRingPath = Path.Combine(_root, "keyring.json");
        await File.WriteAllTextAsync(keyRingPath, JsonSerializer.Serialize(new
        {
            activeKeyId = "active",
            keys = new Dictionary<string, string>
            {
                ["active"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyRingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Readiness(
            new SidecarStatusCatalog(new SidecarHealthOptions()),
            new ReadinessOptions { RequireSecretKeyRing = true },
            keyRingPath: keyRingPath).CheckAsync(cancellation.Token));
    }

    [Fact]
    public async Task SidecarProbePreservesCallerCancellation()
    {
        var options = new SidecarHealthOptions
        {
            Targets =
            [
                new SidecarProbeTarget
                {
                    Id = "blocking",
                    ProviderId = "fixture",
                    BaseUrl = "http://sidecar.test/"
                }
            ]
        };
        var monitor = new SidecarHealthMonitor(
            new HandlerFactory(new BlockingHandler()),
            options,
            new SidecarStatusCatalog(options),
            _health,
            NullLogger<SidecarHealthMonitor>.Instance,
            _clock);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => monitor.ProbeAllOnceAsync(cancellation.Token));
    }

    [Fact]
    public async Task RequiredDirectory_MustExistAndAcceptAWriteProbeWithoutExposingItsPath()
    {
        var existing = await Readiness(
            new SidecarStatusCatalog(new SidecarHealthOptions()),
            new ReadinessOptions
            {
                MinimumFreeBytes = 0,
                RequiredDirectories = [_root]
            }).CheckAsync();

        Assert.True(existing.Ready);
        Assert.Contains(existing.Components, item =>
            item.Id == "directory:0" && item.State == "ready");

        var missingPath = Path.Combine(_root, "private", "missing");
        var missing = await Readiness(
            new SidecarStatusCatalog(new SidecarHealthOptions()),
            new ReadinessOptions
            {
                MinimumFreeBytes = 0,
                RequiredDirectories = [missingPath]
            }).CheckAsync();

        Assert.False(missing.Ready);
        Assert.Contains(missing.Components, item =>
            item.Id == "directory:0" && item.ErrorCode == "required_directory_missing");
        Assert.DoesNotContain(missingPath, JsonSerializer.Serialize(missing), StringComparison.Ordinal);
    }

    private PlatformReadinessService Readiness(
        SidecarStatusCatalog catalog,
        ReadinessOptions options,
        IDurableStorageRuntimeProbe? storageProbe = null,
        string? keyRingPath = null)
    {
        var secretOptions = new SecretStoreOptions
        {
            KeyRingPath = keyRingPath ?? Path.Combine(_root, "missing-keyring.json")
        };
        return new PlatformReadinessService(
            _storageState,
            options,
            new FileSecretKeyRingProvider(secretOptions),
            catalog,
            storageProbe ?? new ReadyStorageProbe(_storageState));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static Guid AccountId(string providerId, string accountKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"allstarr-provider-account|{providerId}|{accountKey}"));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class FlakyHealthObservationStore(int failuresBeforeSuccess)
        : IDurableProviderHealthObservationStore
    {
        private int _failuresRemaining = failuresBeforeSuccess;

        public int WriteAttempts { get; private set; }
        public int SuccessfulWrites { get; private set; }

        public bool IsCircuitOpen(
            string providerId,
            string accountKey,
            string capability) => false;

        public Task<DurableProviderHealthSnapshot?> RecordAsync(
            string providerId,
            string accountKey,
            string capability,
            ProviderHealthState state,
            long? latencyMilliseconds = null,
            string? failureCode = null,
            CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            if (_failuresRemaining-- > 0)
            {
                throw new InvalidOperationException("fixture persistence failure");
            }

            SuccessfulWrites++;
            return Task.FromResult<DurableProviderHealthSnapshot?>(null);
        }
    }

    private sealed class ReadyStorageProbe(DurableStorageState state)
        : IDurableStorageRuntimeProbe
    {
        public Task<DurableStorageSnapshot> CheckAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(state.GetSnapshot());
    }

    private sealed class UnavailableStorageProbe(DurableStorageState state)
        : IDurableStorageRuntimeProbe
    {
        public Task<DurableStorageSnapshot> CheckAsync(
            CancellationToken cancellationToken = default)
        {
            state.Set(
                DurableStorageReadiness.Unavailable,
                errorCode: "database_unavailable");
            return Task.FromResult(state.GetSnapshot());
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
