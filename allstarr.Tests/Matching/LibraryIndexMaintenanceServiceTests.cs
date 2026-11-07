using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Configuration;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class LibraryIndexMaintenanceServiceTests : IAsyncLifetime
{
    private readonly DateTimeOffset _now = new(2026, 8, 26, 18, 0, 0, TimeSpan.Zero);
    private readonly TestClock _clock;
    private SqliteTestDatabase _database = null!;
    private TestFactory _factory = null!;

    public LibraryIndexMaintenanceServiceTests() => _clock = new TestClock(_now);

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        _factory = new TestFactory(_database.Options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    [Fact]
    public async Task MaintenanceResolvesTheRegisteredLibraryIndexAndCleansSharedItems()
    {
        var first = Identity(Guid.NewGuid(), "jellyfin", "first");
        var second = Identity(Guid.NewGuid(), "jellyfin", "second");
        var original = Track(first, _now);
        var duplicate = Track(second, _now);
        duplicate.BackendItemId = original.BackendItemId;
        await using (var db = _factory.CreateDbContext())
        {
            db.Users.AddRange(first, second);
            db.LibraryTracks.AddRange(original, duplicate);
            await db.SaveChangesAsync();
        }
        var storage = new DurableStorageState(_database.StorageOptions);
        storage.Set(DurableStorageReadiness.Ready, "fixture");
        var access = new TestBackendLibraryAccess(_factory, "music");
        var index = new LibraryIndexService(_factory, storage, _clock,
            new TrackIdentityService(_factory, storage, _clock), access);
        await using var services = new ServiceCollection().AddSingleton<ILibraryIndexService>(index).BuildServiceProvider();
        using var maintenance = ActivatorUtilities.CreateInstance<LibraryIndexMaintenanceService>(services,
            _factory, Queue(), storage, _clock, NullLogger<LibraryIndexMaintenanceService>.Instance, access,
            new LibraryIndexDiagnostics(_factory, _clock, NullLogger<LibraryIndexDiagnostics>.Instance));

        await maintenance.EnqueueStaleIndexesAsync(default);

        await using var saved = _factory.CreateDbContext();
        Assert.Single(await saved.LibraryTracks.ToArrayAsync());
        Assert.True(await saved.AuditEvents.AnyAsync(item => item.Action == "track.consolidated"));
    }

    [Fact]
    public async Task EnqueueStaleIndexes_UsesNewestExactSubsonicCredential()
    {
        var userId = Guid.CreateVersion7();
        var jellyfinIdentity = Identity(Guid.CreateVersion7(), "jellyfin", "jellyfin-user");
        var subsonicIdentity = Identity(userId, "subsonic", "subsonic-user");
        var olderCredential = Credential(subsonicIdentity.Id, _now.AddMinutes(-5));
        var newestCredential = Credential(subsonicIdentity.Id, _now);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.AddRange(jellyfinIdentity, subsonicIdentity);
            db.SecretReferences.AddRange(olderCredential, newestCredential);
            db.LibraryTracks.Add(Track(jellyfinIdentity, _now));
            await db.SaveChangesAsync();
        }

        var service = Service();
        Assert.Equal(1, await service.EnqueueStaleIndexesAsync(default));

        await using var verification = await _factory.CreateDbContextAsync();
        var job = Assert.Single(await verification.Jobs.AsNoTracking().ToListAsync());
        var payload = JsonSerializer.Deserialize<LibraryIndexJobPayload>(job.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(newestCredential.Id, payload.CredentialReferenceId);
        Assert.Equal(subsonicIdentity.BackendInstanceId, payload.BackendInstanceId);
        Assert.Equal(subsonicIdentity.BackendPrincipalId, payload.BackendPrincipalId);
        Assert.DoesNotContain("username", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", job.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnqueueStaleIndexes_SkipsRevokedOrWrongIdentityCredentials()
    {
        var userId = Guid.CreateVersion7();
        var jellyfinIdentity = Identity(Guid.CreateVersion7(), "jellyfin", "jellyfin-user");
        var subsonicIdentity = Identity(userId, "subsonic", "subsonic-user");
        var wrongIdentityCredential = Credential(jellyfinIdentity.Id, _now);
        var revokedCredential = Credential(subsonicIdentity.Id, _now);
        revokedCredential.RevokedAt = _now;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.AddRange(jellyfinIdentity, subsonicIdentity);
            db.SecretReferences.AddRange(wrongIdentityCredential, revokedCredential);
            db.LibraryTracks.Add(Track(jellyfinIdentity, _now));
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await Service().EnqueueStaleIndexesAsync(default));
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.Jobs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task JobHandler_RejectsCredentialFromAnotherBackendIdentity()
    {
        var userId = Guid.CreateVersion7();
        var requestedIdentity = Identity(userId, "subsonic", "requested-user");
        var otherIdentity = Identity(Guid.CreateVersion7(), "subsonic", "other-user");
        var credential = Credential(otherIdentity.Id, _now);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.AddRange(requestedIdentity, otherIdentity);
            db.SecretReferences.Add(credential);
            await db.SaveChangesAsync();
        }

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new LibraryIndexJobPayload(
            "music", requestedIdentity.BackendInstanceId, requestedIdentity.BackendPrincipalId, credential.Id)));
        using var policy = JsonDocument.Parse("{}");
        var claim = new DurableJobClaim(Guid.CreateVersion7(), 1, "library.index",
            payload.RootElement.Clone(), userId, null, null, policy.RootElement.Clone(),
            "credential-scope-test");
        var handler = new LibraryIndexJobHandler(_factory,
            new BackendLibraryCatalogScannerResolver([]), _clock);
        await using var services = new ServiceCollection().BuildServiceProvider();

        var result = await handler.ExecuteAsync(new DurableJobExecutionContext(claim, services), default);

        Assert.Equal(DurableJobCompletionKind.Failed, result.Kind);
        Assert.Equal("library_index_credential_unavailable", result.ErrorCode);
    }

    [Theory]
    [InlineData(DurableJobState.Pending)]
    [InlineData(DurableJobState.Running)]
    [InlineData(DurableJobState.RetryScheduled)]
    public async Task EnqueueStaleIndexes_DoesNotDuplicateAnActiveScopeAcrossRefreshCycles(DurableJobState state)
    {
        var identity = Identity(Guid.CreateVersion7(), "jellyfin", "listener");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(identity);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await Service().EnqueueStaleIndexesAsync(default));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync();
            job.State = state;
            await db.SaveChangesAsync();
        }
        _clock.UtcNow = _now.AddHours(1);

        Assert.Equal(0, await Service().EnqueueStaleIndexesAsync(default));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Single(await verification.Jobs.ToListAsync());
    }

    [Fact]
    public async Task EnqueueStaleIndexes_ClosesExistingDuplicateJobsAndKeepsTheRunningScan()
    {
        var identity = Identity(Guid.CreateVersion7(), "jellyfin", "listener");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(identity);
            await db.SaveChangesAsync();
        }
        var queue = Queue();
        for (var i = 0; i < 3; i++)
            await queue.EnqueueAsync(new DurableJobEnqueueRequest<LibraryIndexJobPayload>("library.index",
                $"legacy-{i}", new(null, identity.BackendInstanceId, identity.BackendPrincipalId), identity.Id));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var jobs = await db.Jobs.OrderBy(job => job.Id).ToArrayAsync();
            jobs[0].State = DurableJobState.RetryScheduled;
            jobs[0].AvailableAt = _now.AddDays(1);
            jobs[1].State = DurableJobState.Running;
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await Service().EnqueueStaleIndexesAsync(default));

        await using var verification = await _factory.CreateDbContextAsync();
        var remaining = await verification.Jobs.ToArrayAsync();
        Assert.Single(remaining, job => job.State == DurableJobState.Running);
        Assert.Equal(2, remaining.Count(job => job.State == DurableJobState.Cancelled && job.CompletedAt.HasValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JobHandler_MissingAccessIsTerminal(bool missingServiceKey)
    {
        var identity = Identity(Guid.CreateVersion7(), "jellyfin", "listener");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(identity);
            await db.SaveChangesAsync();
        }
        var handler = new LibraryIndexJobHandler(_factory,
            new BackendLibraryCatalogScannerResolver([new FailingScanner(missingServiceKey)]), _clock);
        await using var services = new ServiceCollection().BuildServiceProvider();

        var result = await handler.ExecuteAsync(new(Claim(identity), services), default);

        Assert.Equal(DurableJobCompletionKind.Failed, result.Kind);
        Assert.Equal(missingServiceKey ? BackendAccessMissingException.Code : "library_index_access_denied", result.ErrorCode);
    }

    [Fact]
    public async Task JobHandler_IndexesWithServiceAccessWithoutAUserSessionOrStoredUserCredential()
    {
        var identity = Identity(Guid.CreateVersion7(), "jellyfin", "listener");
        identity.IsAdmin = true;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(identity);
            await db.SaveChangesAsync();
        }
        var settings = new JellyfinSettings { Url = "https://backend.test", ApiKey = "service-fixture" };
        var clients = new Clients(new CatalogHandler());
        var source = new BackendLibraryPermissionSource(new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            _factory, null!, Options.Create(settings), Options.Create(new SubsonicSettings()));
        using var cache = new MemoryApplicationCache(_clock);
        var access = new BackendLibraryAccessResolver(source, clients, cache, _clock, new ConfigurationBuilder().Build());
        var storage = new DurableStorageState(new StorageOptions());
        storage.Set(DurableStorageReadiness.Ready, "fixture");
        var index = new LibraryIndexService(_factory, storage, _clock,
            new TrackIdentityService(_factory, storage, _clock), access);
        var scanner = new JellyfinLibraryCatalogScanner(clients.CreateClient("catalog"), settings, index, _clock);
        var handler = new LibraryIndexJobHandler(_factory, new BackendLibraryCatalogScannerResolver([scanner]), _clock);
        await using var services = new ServiceCollection().BuildServiceProvider();

        var result = await handler.ExecuteAsync(new(Claim(identity), services), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(identity.Id, (await verification.LibraryTracks.SingleAsync()).OwnerUserId);
        Assert.Empty(await verification.SecretReferences.ToArrayAsync());
    }

    private DurableJobClaim Claim(UserRecord identity)
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new LibraryIndexJobPayload(
            null, identity.BackendInstanceId, identity.BackendPrincipalId)));
        using var policy = JsonDocument.Parse("{}");
        return new(Guid.CreateVersion7(), 1, "library.index", payload.RootElement.Clone(), identity.Id,
            null, null, policy.RootElement.Clone(), "index-test");
    }

    private DurableJobQueue Queue()
    {
        var options = new DurableJobOptions();
        return new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
    }

    [Fact]
    public async Task EnqueueStaleIndexes_SessionOnlyScopeClosesQueuedWorkWithoutRecurringJobsOrWarnings()
    {
        var identity = Identity(Guid.CreateVersion7(), "jellyfin", "listener");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(identity);
            await db.SaveChangesAsync();
        }
        await Queue().EnqueueAsync(new DurableJobEnqueueRequest<LibraryIndexJobPayload>("library.index",
            "legacy", new(null, identity.BackendInstanceId, identity.BackendPrincipalId), identity.Id));
        var access = new TestBackendLibraryAccess(_factory, "music");
        access.Permissions[identity.Id] = new(false, [], "library_index_credential_unavailable");
        var logger = new WarningLogger();
        var diagnostics = new LibraryIndexDiagnostics(_factory, _clock, logger);

        Assert.Equal(0, await Service(access, diagnostics).EnqueueStaleIndexesAsync(default));
        _clock.UtcNow = _now.AddHours(2);
        Assert.Equal(0, await Service(access, diagnostics).EnqueueStaleIndexesAsync(default));
        Assert.Single(logger.Messages);
        Assert.DoesNotContain("listener", logger.Messages[0], StringComparison.Ordinal);
        await using (var verification = await _factory.CreateDbContextAsync())
        {
            var job = await verification.Jobs.SingleAsync();
            Assert.Equal(DurableJobState.Failed, job.State);
            Assert.Equal("library_index_credential_unavailable", job.LastErrorCode);
        }
        _clock.UtcNow = _now.AddDays(1);
        var restarted = new LibraryIndexDiagnostics(_factory, _clock, logger);
        Assert.Equal(0, await Service(access, restarted).EnqueueStaleIndexesAsync(default));
        Assert.Equal(2, logger.Messages.Count);
        access.Permissions.Remove(identity.Id);
        Assert.Equal(1, await Service(access, restarted).EnqueueStaleIndexesAsync(default));
    }

    [Fact]
    public async Task EnqueueStaleIndexes_TransientPermissionFailurePreservesTheExistingRetry()
    {
        var identity = Identity(Guid.CreateVersion7(), "jellyfin", "listener");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(identity);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await Service().EnqueueStaleIndexesAsync(default));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Jobs.SingleAsync()).State = DurableJobState.RetryScheduled;
            await db.SaveChangesAsync();
        }
        var access = new TestBackendLibraryAccess(_factory, "music");
        access.Permissions[identity.Id] = BackendLibraryAccess.Unavailable;
        _clock.UtcNow = _now.AddHours(1);

        Assert.Equal(0, await Service(access).EnqueueStaleIndexesAsync(default));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(DurableJobState.RetryScheduled, (await verification.Jobs.SingleAsync()).State);
    }

    [Fact]
    public async Task StoredPermissions_UseTheJobsExactCredentialAndCloseUnreadableAccess()
    {
        var identity = Identity(Guid.CreateVersion7(), "subsonic", "listener");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(identity);
            await db.SaveChangesAsync();
        }
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(_database.StorageOptions.DataDirectory, "keys.json") };
        var ring = new FileSecretKeyRingProvider(options);
        await ring.CreateIfMissingAsync(false);
        var secrets = new EncryptedSecretStore(_factory, ring, options, _clock);
        var selected = await secrets.StoreAsync(identity.Id, BackendCredentialScope.SubsonicPurpose,
            Encoding.UTF8.GetBytes("""{"username":"listener","password":"selected-fixture"}"""));
        _clock.UtcNow = _now.AddMinutes(1);
        await secrets.StoreAsync(identity.Id, BackendCredentialScope.SubsonicPurpose,
            Encoding.UTF8.GetBytes("""{"username":"listener","password":"newest-fixture"}"""));
        var source = new BackendLibraryPermissionSource(new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            _factory, secrets, Options.Create(new JellyfinSettings()), Options.Create(new SubsonicSettings { Url = "https://backend.test" }));
        var context = LibraryIndexExecution.Create(identity, selected.Id, "stored-access-test", _clock, default);
        using (var request = await source.CreateRequestAsync(context, default))
        {
            Assert.NotNull(request);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("p=selected-fixture", body, StringComparison.Ordinal);
            Assert.DoesNotContain("newest-fixture", body, StringComparison.Ordinal);
        }
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.SecretVersions.SingleAsync(version => version.SecretReferenceId == selected.Id)).AuthenticationTag = new byte[16];
            await db.SaveChangesAsync();
        }

        Assert.Null(await source.CreateRequestAsync(context, default));
    }

    [Fact]
    public async Task DurableJobLibraryIndex_ConcurrentGenerationsCreateOneActiveScope()
    {
        var identity = Identity(Guid.CreateVersion7(), "jellyfin", "listener");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(identity);
            await db.SaveChangesAsync();
        }
        var queue = Queue();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(generation => Task.Run(() =>
            queue.EnqueueLibraryIndexAsync(new DurableJobEnqueueRequest<LibraryIndexJobPayload>("library.index",
                $"concurrent-{generation}", new(null, identity.BackendInstanceId, identity.BackendPrincipalId), identity.Id), default))));

        Assert.Single(results, result => result.Created);
        Assert.Single(results.Select(result => result.JobId).Distinct());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DurableJobCompletionKind.Failed)]
    [InlineData(HttpStatusCode.Forbidden, DurableJobCompletionKind.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DurableJobCompletionKind.Retry)]
    public async Task JobHandler_ClassifiesHttpFailuresAndWarnsOnceAcrossRestarts(
        HttpStatusCode status, DurableJobCompletionKind expected)
    {
        var identity = Identity(Guid.CreateVersion7(), "jellyfin", "listener");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(identity);
            await db.SaveChangesAsync();
        }
        var logger = new WarningLogger();
        await using var services = new ServiceCollection().BuildServiceProvider();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var diagnostics = new LibraryIndexDiagnostics(_factory, _clock, logger);
            var handler = new LibraryIndexJobHandler(_factory,
                new BackendLibraryCatalogScannerResolver([new ExceptionScanner(new HttpRequestException("private fixture detail", null, status))]),
                _clock, diagnostics);

            var result = await handler.ExecuteAsync(new(Claim(identity), services), default);

            Assert.Equal(expected, result.Kind);
            Assert.DoesNotContain("private fixture detail", result.SafeMessage ?? "", StringComparison.Ordinal);
        }
        Assert.Single(logger.Messages);
        Assert.DoesNotContain("private fixture detail", logger.Messages[0], StringComparison.Ordinal);
    }

    private LibraryIndexMaintenanceService Service(IBackendLibraryAccessResolver? access = null, LibraryIndexDiagnostics? diagnostics = null)
    {
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), _clock);
        var storage = new DurableStorageState(new StorageOptions());
        return new LibraryIndexMaintenanceService(_factory, queue, storage, _clock,
            NullLogger<LibraryIndexMaintenanceService>.Instance, access ?? new TestBackendLibraryAccess(_factory, "music"),
            diagnostics ?? new LibraryIndexDiagnostics(_factory, _clock, NullLogger<LibraryIndexDiagnostics>.Instance));
    }

    private UserRecord Identity(Guid userId, string backendType, string principalId) => new()
    {
        Id = userId,
        BackendType = backendType,
        BackendInstanceId = "primary",
        BackendPrincipalId = principalId,
        DisplayName = principalId,
        Enabled = true,
        CreatedAt = _now,
        UpdatedAt = _now,
        LastSeenAt = _now
    };

    private SecretReferenceRecord Credential(Guid userId, DateTimeOffset updatedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Purpose = BackendCredentialScope.SubsonicPurpose,
        ActiveVersion = 1,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt
    };

    private LibraryTrackRecord Track(UserRecord identity,
        DateTimeOffset indexedAt) => new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = identity.Id,
            BackendLibraryId = "music",
            Protocol = identity.BackendType,
            BackendInstanceId = identity.BackendInstanceId,
            BackendItemId = Guid.CreateVersion7().ToString("N"),
            FilePath = "/music/track.flac",
            Title = "Track",
            Artist = "Artist",
            ProviderIdsJson = "{}",
            IndexedAt = indexedAt,
            SourceModifiedAt = indexedAt,
            UpdatedAt = indexedAt
        };

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FailingScanner(bool missingServiceKey) : IBackendLibraryCatalogScanner
    {
        public ProtocolKind Protocol => ProtocolKind.Jellyfin;
        public Task<LibraryCatalogScanResult> ScanAsync(ProtocolExecutionContext context, LibraryCatalogScanRequest request,
            CancellationToken cancellationToken)
        {
            if (missingServiceKey) throw new BackendAccessMissingException();
            throw new UnauthorizedAccessException("private backend detail");
        }
    }

    private sealed class ExceptionScanner(Exception error) : IBackendLibraryCatalogScanner
    {
        public ProtocolKind Protocol => ProtocolKind.Jellyfin;
        public Task<LibraryCatalogScanResult> ScanAsync(ProtocolExecutionContext context, LibraryCatalogScanRequest request,
            CancellationToken cancellationToken) => throw error;
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("service-fixture", request.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
            Assert.Contains("UserId=listener", request.RequestUri!.Query, StringComparison.Ordinal);
            var body = request.RequestUri.AbsolutePath == "/UserViews"
                ? """{"Items":[{"Id":"music","CollectionType":"music"}]}"""
                : """{"Items":[{"Id":"song","Name":"Song","Path":"/music/song.flac","Artists":["Artist"],"DateCreated":"2026-08-26T01:00:00Z"}],"TotalRecordCount":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class WarningLogger : ILogger<LibraryIndexDiagnostics>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Messages.Add(formatter(state, exception));
        }
    }
}
