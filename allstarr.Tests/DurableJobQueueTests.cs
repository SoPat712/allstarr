using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class DurableJobQueueTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private TestDbContextFactory _factory = null!;
    private FakeClock _clock = null!;
    private DurableJobOptions _options = null!;
    private DurableJobQueue _queue = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var dbOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "jobs.db")}")
            .Options;
        _factory = new TestDbContextFactory(dbOptions);
        await using var context = await _factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
        context.Tenants.Add(new TenantRecord
        {
            Id = _tenantId,
            Slug = "fixture",
            Name = "Fixture tenant",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.Users.Add(new PlatformUserRecord
        {
            Id = _userId,
            TenantId = _tenantId,
            DisplayName = "Fixture user",
            Status = PlatformUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        _clock = new FakeClock(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
        _options = new DurableJobOptions
        {
            DefaultMaxAttempts = 3,
            LeaseSeconds = 10,
            PollIntervalMilliseconds = 100,
            MaxPayloadBytes = 64 * 1024
        };
        _queue = new DurableJobQueue(
            _factory,
            _options,
            new JobPayloadPolicy(_options),
            _clock);
    }

    [Fact]
    public async Task Enqueue_IsIdempotentAndWritesOutboxInSameTransaction()
    {
        var request = new DurableJobEnqueueRequest<object>(
            "favorite.download",
            "favorite:track-1:on",
            new { trackId = "track-1", secretReferenceId = Guid.CreateVersion7() },
            _tenantId,
            _userId);

        var first = await _queue.EnqueueAsync(request);
        var repeated = await _queue.EnqueueAsync(request);

        Assert.True(first.Created);
        Assert.False(repeated.Created);
        Assert.Equal(first.JobId, repeated.JobId);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Single(await context.Jobs.ToListAsync());
        var message = Assert.Single(await context.OutboxMessages.ToListAsync());
        Assert.Equal("job.enqueued", message.Type);
        Assert.Contains(first.JobId.ToString(), message.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentEnqueue_CreatesOneDurableJobAndOneOutboxMessage()
    {
        var request = new DurableJobEnqueueRequest<object>(
            "favorite.download",
            "favorite:track-concurrent:on",
            new { trackId = "track-concurrent" },
            _tenantId,
            _userId);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => _queue.EnqueueAsync(request)));

        Assert.Single(results, result => result.Created);
        Assert.Single(results.Select(result => result.JobId).Distinct());
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Single(await context.Jobs.ToListAsync());
        Assert.Single(await context.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task SameTenantUsers_HaveIndependentIdempotencyScopes()
    {
        var secondUserId = Guid.CreateVersion7();
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            setup.Users.Add(new PlatformUserRecord
            {
                Id = secondUserId,
                TenantId = _tenantId,
                DisplayName = "Second fixture user",
                Status = PlatformUserStatus.Active,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        var firstRequest = new DurableJobEnqueueRequest<object>(
            "playlist.sync",
            "same-client-key",
            new { playlistId = "shared-provider-id" },
            _tenantId,
            _userId);
        var secondRequest = firstRequest with { OwnerUserId = secondUserId };

        var results = await Task.WhenAll(
            _queue.EnqueueAsync(firstRequest),
            _queue.EnqueueAsync(secondRequest));

        Assert.All(results, result => Assert.True(result.Created));
        Assert.Equal(2, results.Select(result => result.JobId).Distinct().Count());
        await using var context = await _factory.CreateDbContextAsync();
        var jobs = await context.Jobs.OrderBy(item => item.OwnerUserId).ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Equal(2, jobs.Select(item => item.ScopeKey).Distinct().Count());
        Assert.All(jobs, job => Assert.Contains(':', job.ScopeKey));
        Assert.Equal(2, await context.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task PayloadPolicy_RejectsPlaintextCredentialFields()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
                "provider.probe",
                "probe-1",
                new { accessToken = "must-not-persist" },
                _tenantId,
                _userId)));

        Assert.Contains("secret reference", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Empty(await context.Jobs.ToListAsync());
    }

    [Theory]
    [InlineData("credential", "opaque-value")]
    [InlineData("clientSecretValue", "opaque-value")]
    [InlineData("spotifyCookieValue", "opaque-value")]
    [InlineData("endpoint", "https://provider.invalid/media?access_token=opaque-value")]
    [InlineData("target", "Host=database;Password=opaque-value")]
    [InlineData("requestMetadata", "Bearer opaque-value")]
    public async Task PayloadPolicy_RejectsNestedSecretNamesAndEmbeddedCredentials(
        string field,
        string value)
    {
        var payload = new Dictionary<string, object>
        {
            ["operation"] = "probe",
            ["nested"] = new Dictionary<string, string> { [field] = value }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
                "provider.probe",
                $"secret-shape-{field}",
                payload,
                _tenantId,
                _userId)));

        Assert.Contains("secret reference", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Empty(await context.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Idempotency_UsesCanonicalPayloadAndRejectsDifferentRequestedWork()
    {
        var firstPayload = new Dictionary<string, object>
        {
            ["trackId"] = "track-1",
            ["options"] = new Dictionary<string, object>
            {
                ["quality"] = "lossless",
                ["normalize"] = false
            }
        };
        var samePayloadDifferentPropertyOrder = new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["normalize"] = false,
                ["quality"] = "lossless"
            },
            ["trackId"] = "track-1"
        };
        var request = new DurableJobEnqueueRequest<object>(
            "provider.download",
            "canonical-request",
            firstPayload,
            _tenantId,
            _userId,
            Priority: 10,
            MaxAttempts: 4,
            MaxDeferrals: 12);

        var first = await _queue.EnqueueAsync(request);
        var repeated = await _queue.EnqueueAsync(request with
        {
            Payload = samePayloadDifferentPropertyOrder,
            CorrelationId = "another-request"
        });

        Assert.True(first.Created);
        Assert.False(repeated.Created);
        var conflicts = new[]
        {
            request with { Payload = new { trackId = "track-2" } },
            request with { Priority = 11 },
            request with { MaxAttempts = 5 },
            request with { MaxDeferrals = 13 },
            request with { AvailableAt = _clock.UtcNow.AddHours(1) }
        };
        foreach (var conflict in conflicts)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _queue.EnqueueAsync(conflict));
            Assert.Contains("request payload or execution policy", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using var context = await _factory.CreateDbContextAsync();
        var job = Assert.Single(await context.Jobs.ToListAsync());
        Assert.Equal(64, job.RequestFingerprint.Length);
        Assert.Single(await context.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Enqueue_PersistsExactProviderContextAndRedactsUnsafeCorrelation()
    {
        var account = await AddProviderAccount("deezer", _userId);

        var enqueued = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "provider.download",
            "context-snapshot",
            new { trackId = "fixture" },
            _tenantId,
            _userId,
            ProviderAccountId: account.Id,
            LibraryScopeId: " music-main ",
            Capability: " DOWNLOAD ",
            CorrelationId: "https://caller.invalid/request?token=must-not-persist"));

        await using var context = await _factory.CreateDbContextAsync();
        var job = await context.Jobs.SingleAsync(item => item.Id == enqueued.JobId);
        Assert.Equal(_tenantId, job.TenantId);
        Assert.Equal(_userId, job.OwnerUserId);
        Assert.Equal(account.Id, job.ProviderAccountId);
        Assert.Equal("music-main", job.LibraryScopeId);
        Assert.Equal("download", job.ProviderCapability);
        Assert.StartsWith("redacted-", job.CorrelationId, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-persist", job.CorrelationId, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretReference", job.PolicySnapshotJson, StringComparison.OrdinalIgnoreCase);
        var snapshot = JsonSerializer.Deserialize<DurableJobPolicySnapshot>(job.PolicySnapshotJson);
        Assert.NotNull(snapshot);
        Assert.Equal("deezer", snapshot.ProviderId);
        Assert.Equal("download", snapshot.Capability);
        Assert.Equal("user_account", snapshot.AuthorizationRule);

        var claim = await _queue.ClaimNextAsync("worker-a");
        Assert.NotNull(claim);
        Assert.Equal(account.Id, claim.ProviderAccountId);
        Assert.Equal("download", claim.ProviderCapability);
        Assert.Equal(job.CorrelationId, claim.CorrelationId);
    }

    [Fact]
    public async Task Enqueue_RequiresAnActiveInitiatorInTheExactTenant()
    {
        var missingUser = Guid.CreateVersion7();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
                "placement",
                "missing-initiator",
                new { itemId = "fixture" },
                _tenantId,
                missingUser)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
                "placement",
                "missing-context",
                new { itemId = "fixture" })));
    }

    [Fact]
    public async Task Idempotency_RejectsProviderLibraryOrPolicyContextMismatch()
    {
        var firstAccount = await AddProviderAccount("deezer", _userId);
        var secondAccount = await AddProviderAccount("qobuz", _userId);
        var first = new DurableJobEnqueueRequest<object>(
            "provider.download",
            "context-conflict",
            new { trackId = "fixture" },
            _tenantId,
            _userId,
            ProviderAccountId: firstAccount.Id,
            LibraryScopeId: "music-a",
            Capability: "download",
            CorrelationId: "request-one");
        await _queue.EnqueueAsync(first);

        var sameContext = await _queue.EnqueueAsync(first with { CorrelationId = "request-two" });
        Assert.False(sameContext.Created);

        var conflicts = new[]
        {
            first with { ProviderAccountId = secondAccount.Id },
            first with { LibraryScopeId = "music-b" },
            first with { Capability = "playlist" }
        };
        foreach (var conflict in conflicts)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _queue.EnqueueAsync(conflict));
            Assert.Contains("execution context", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Single(await context.Jobs.ToListAsync());
        Assert.Single(await context.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ExpiredLease_IsRecoveredAndStaleWorkerCannotComplete()
    {
        var queued = await Enqueue("placement", "placement-1");
        var first = await _queue.ClaimNextAsync("worker-a");
        Assert.NotNull(first);
        _clock.Advance(TimeSpan.FromSeconds(11));

        var recovered = await _queue.ClaimNextAsync("worker-b");

        Assert.NotNull(recovered);
        Assert.Equal(queued.JobId, recovered.JobId);
        Assert.Equal(2, recovered.AttemptNumber);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _queue.CompleteAsync(first, DurableJobCompletion.Success()));
        await _queue.CompleteAsync(recovered, DurableJobCompletion.Success());
        await using var context = await _factory.CreateDbContextAsync();
        var job = await context.Jobs.SingleAsync();
        Assert.Equal(DurableJobState.Succeeded, job.State);
        var attempts = await context.JobAttempts.OrderBy(item => item.AttemptNumber).ToListAsync();
        Assert.Equal("lease_expired", attempts[0].Outcome);
        Assert.Equal("succeeded", attempts[1].Outcome);
    }

    [Fact]
    public async Task ConcurrentClaims_LeaseAJobToOnlyOneWorker()
    {
        var queued = await Enqueue("placement", "placement-concurrent-claim");

        var claims = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(index => _queue.ClaimNextAsync($"worker-{index}")));

        var claim = Assert.Single(claims, item => item != null)!;
        Assert.Equal(queued.JobId, claim.JobId);
        await using var context = await _factory.CreateDbContextAsync();
        var job = await context.Jobs.SingleAsync();
        Assert.Equal(DurableJobState.Running, job.State);
        Assert.Equal(claim.WorkerId, job.LeaseOwner);
        Assert.Single(await context.JobAttempts.ToListAsync());
    }

    [Fact]
    public async Task RetryAndTerminalFailure_AreDurableAndErrorsAreRedacted()
    {
        await Enqueue("provider.download", "download-1");
        var first = (await _queue.ClaimNextAsync("worker-a"))!;

        await _queue.CompleteAsync(
            first,
            DurableJobCompletion.Retry(
                "provider_error token=fixture",
                "request https://provider.invalid/song?token=fixture failed token=fixture"));

        await using (var context = await _factory.CreateDbContextAsync())
        {
            var scheduled = await context.Jobs.SingleAsync();
            Assert.Equal(DurableJobState.RetryScheduled, scheduled.State);
            Assert.DoesNotContain("fixture", scheduled.LastErrorMessage ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("provider.invalid", scheduled.LastErrorMessage ?? string.Empty, StringComparison.Ordinal);
        }

        _clock.Advance(TimeSpan.FromSeconds(2));
        var second = (await _queue.ClaimNextAsync("worker-b"))!;
        await _queue.CompleteAsync(
            second,
            DurableJobCompletion.Failure("media_incompatible", "No compatible media"));

        await using (var context = await _factory.CreateDbContextAsync())
        {
            var failed = await context.Jobs.SingleAsync();
            Assert.Equal(DurableJobState.Failed, failed.State);
            Assert.Equal(2, failed.AttemptCount);
            Assert.Equal("media_incompatible", failed.LastErrorCode);
        }
    }

    [Fact]
    public async Task CancellationBeforeClaim_ProducesTerminalStateWithoutExecution()
    {
        var queued = await Enqueue("playlist.refresh", "refresh-1");

        var requested = await _queue.RequestCancellationAsync(queued.JobId, _tenantId);
        var repeated = await _queue.RequestCancellationAsync(queued.JobId, _tenantId);
        var claim = await _queue.ClaimNextAsync("worker-a");

        Assert.True(requested);
        Assert.True(repeated);
        Assert.Null(claim);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(DurableJobState.Cancelled, (await context.Jobs.SingleAsync()).State);
        Assert.Empty(await context.JobAttempts.ToListAsync());
        var outbox = await context.OutboxMessages.OrderBy(item => item.CreatedAt).ToListAsync();
        Assert.Equal(2, outbox.Count);
        Assert.Equal("job.enqueued", outbox[0].Type);
        Assert.Equal("job.cancelled", outbox[1].Type);
    }

    [Fact]
    public async Task OutboxFailure_IsRetryableAndRecoveredAfterLeaseOrBackoff()
    {
        await Enqueue("probe", "probe-1");
        var outbox = new DurableOutbox(_factory, _options, _clock);
        var first = (await outbox.ClaimNextAsync("dispatcher-a"))!;

        await outbox.MarkFailedAsync(
            first,
            "sink_unavailable",
            "sink https://events.invalid unavailable token=fixture");
        Assert.Null(await outbox.ClaimNextAsync("dispatcher-b"));
        _clock.Advance(TimeSpan.FromSeconds(2));
        var retried = await outbox.ClaimNextAsync("dispatcher-b");

        Assert.NotNull(retried);
        Assert.Equal(first.MessageId, retried.MessageId);
        await outbox.MarkDeliveredAsync(retried);
        await using var context = await _factory.CreateDbContextAsync();
        var delivered = await context.OutboxMessages.SingleAsync();
        Assert.Equal(OutboxMessageState.Delivered, delivered.State);
        Assert.Equal(2, delivered.AttemptCount);
        Assert.DoesNotContain("fixture", delivered.LastErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutboxFailure_BecomesTerminalAtThePersistedAttemptLimit()
    {
        _options.MaxOutboxAttempts = 2;
        await Enqueue("probe", "probe-terminal-outbox");
        _options.MaxOutboxAttempts = 99;
        var outbox = new DurableOutbox(_factory, _options, _clock);
        var first = (await outbox.ClaimNextAsync("dispatcher-a"))!;
        var firstFailure = await outbox.MarkFailedAsync(
            first,
            "sink_unavailable",
            "The sink was unavailable.");
        Assert.False(firstFailure.Terminal);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var second = (await outbox.ClaimNextAsync("dispatcher-b"))!;

        var secondFailure = await outbox.MarkFailedAsync(
            second,
            "sink_unavailable",
            "The sink was unavailable.");

        Assert.True(secondFailure.Terminal);
        Assert.Equal(2, secondFailure.AttemptCount);
        Assert.Equal(2, secondFailure.MaxAttempts);
        Assert.Null(await outbox.ClaimNextAsync("dispatcher-c"));
        await using var context = await _factory.CreateDbContextAsync();
        var failed = await context.OutboxMessages.SingleAsync();
        Assert.Equal(OutboxMessageState.Failed, failed.State);
        Assert.Equal(2, failed.MaxAttempts);
        Assert.NotNull(failed.FailedAt);
    }

    [Fact]
    public async Task ExpiredOutboxLease_CannotExceedThePersistedAttemptLimit()
    {
        _options.MaxOutboxAttempts = 1;
        await Enqueue("probe", "probe-abandoned-outbox");
        var outbox = new DurableOutbox(_factory, _options, _clock);
        Assert.NotNull(await outbox.ClaimNextAsync("dispatcher-a"));
        _clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Null(await outbox.ClaimNextAsync("dispatcher-b"));

        await using var context = await _factory.CreateDbContextAsync();
        var failed = await context.OutboxMessages.SingleAsync();
        Assert.Equal(OutboxMessageState.Failed, failed.State);
        Assert.Equal("outbox_attempts_exhausted", failed.LastErrorCode);
        Assert.NotNull(failed.FailedAt);
    }

    [Fact]
    public async Task SidecarDeferrals_HaveASeparateBoundedBudgetWithoutConsumingFailureRetries()
    {
        var enqueued = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "provider.download",
            "sidecar-deferral",
            new { trackId = "fixture" },
            _tenantId,
            _userId,
            MaxAttempts: 2,
            MaxDeferrals: 1));
        var first = (await _queue.ClaimNextAsync("worker-a"))!;

        await _queue.CompleteAsync(
            first,
            DurableJobCompletion.Defer(
                "sidecar_unreachable",
                "Waiting for sidecar readiness.",
                TimeSpan.Zero));
        var second = (await _queue.ClaimNextAsync("worker-b"))!;
        await _queue.CompleteAsync(
            second,
            DurableJobCompletion.Defer(
                "sidecar_unreachable",
                "Waiting for sidecar readiness.",
                TimeSpan.Zero));

        await using var context = await _factory.CreateDbContextAsync();
        var job = await context.Jobs.SingleAsync(item => item.Id == enqueued.JobId);
        Assert.Equal(DurableJobState.Failed, job.State);
        Assert.Equal(0, job.FailureCount);
        Assert.Equal(2, job.DeferralCount);
        Assert.Equal("deferral_limit_exceeded", job.LastErrorCode);
        var attempts = await context.JobAttempts.OrderBy(item => item.AttemptNumber).ToListAsync();
        Assert.Equal("deferred", attempts[0].Outcome);
        Assert.Equal("failed", attempts[1].Outcome);
    }

    [Fact]
    public async Task RepeatedWorkerLeaseLoss_StopsAtTheFailureBudget()
    {
        var enqueued = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "placement",
            "lease-loss-budget",
            new { itemId = "fixture" },
            _tenantId,
            _userId,
            MaxAttempts: 1));
        Assert.NotNull(await _queue.ClaimNextAsync("worker-a"));
        _clock.Advance(TimeSpan.FromSeconds(11));

        var recovered = await _queue.ClaimNextAsync("worker-b");

        Assert.Null(recovered);
        await using var context = await _factory.CreateDbContextAsync();
        var job = await context.Jobs.SingleAsync(item => item.Id == enqueued.JobId);
        Assert.Equal(DurableJobState.Failed, job.State);
        Assert.Equal(1, job.FailureCount);
        Assert.Equal("worker_lease_expired", job.LastErrorCode);
    }

    [Fact]
    public async Task WorkerRestart_RecoversExpiredLeaseAndCompletesTheOriginalJob()
    {
        _options.LeaseSeconds = 5;
        _options.PollIntervalMilliseconds = 25;
        var queued = await Enqueue("restartable", "restart-1");
        var storageOptions = new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_root, "jobs.db")}",
            BackupDirectory = Path.Combine(_root, "backups")
        };
        var storageState = new DurableStorageState(storageOptions);
        storageState.Set(DurableStorageReadiness.Ready, "fixture");
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var traces = new PlatformTraceCollector();
        await traces.StartAsync(CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWorker = new DurableJobWorker(
            _queue,
            _options,
            storageState,
            services,
            [new BlockingHandler(started)],
            NullLogger<DurableJobWorker>.Instance,
            new ReadyStorageProbe(storageState));

        await firstWorker.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await firstWorker.StopAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(6));

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWorker = new DurableJobWorker(
            _queue,
            _options,
            storageState,
            services,
            [new SuccessfulHandler(completed)],
            NullLogger<DurableJobWorker>.Instance,
            new ReadyStorageProbe(storageState));
        await secondWorker.StartAsync(CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForState(queued.JobId, DurableJobState.Succeeded);
        await secondWorker.StopAsync(CancellationToken.None);

        await using var context = await _factory.CreateDbContextAsync();
        var jobs = await context.Jobs.ToListAsync();
        var job = Assert.Single(jobs);
        Assert.Equal(queued.JobId, job.Id);
        Assert.Equal(2, job.AttemptCount);
        var attempts = await context.JobAttempts.OrderBy(item => item.AttemptNumber).ToListAsync();
        Assert.Equal("lease_expired", attempts[0].Outcome);
        Assert.Equal("succeeded", attempts[1].Outcome);
        Assert.Contains(
            traces.GetSnapshot(),
            span => span.Operation == "durable-job.execute" && !span.Failed);
    }

    [Fact]
    public async Task Worker_DeniesDisabledSavedAccountWithoutRetargetingToAnotherAccount()
    {
        _options.PollIntervalMilliseconds = 25;
        var savedAccount = await AddProviderAccount("deezer", _userId);
        var alternativeAccount = await AddProviderAccount("deezer", _userId);
        var queued = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "provider.work",
            "exact-account-only",
            new { trackId = "fixture" },
            _tenantId,
            _userId,
            ProviderAccountId: savedAccount.Id,
            Capability: "download",
            CorrelationId: "exact-account-test"));
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var account = await context.ProviderAccounts.SingleAsync(item => item.Id == savedAccount.Id);
            account.Enabled = false;
            await context.SaveChangesAsync();
        }

        var storageOptions = new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_root, "jobs.db")}",
            BackupDirectory = Path.Combine(_root, "backups")
        };
        var storageState = new DurableStorageState(storageOptions);
        storageState.Set(DurableStorageReadiness.Ready, "fixture");
        await using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new CountingHandler();
        var worker = new DurableJobWorker(
            _queue,
            _options,
            storageState,
            services,
            [handler],
            NullLogger<DurableJobWorker>.Instance,
            new ReadyStorageProbe(storageState));

        await worker.StartAsync(CancellationToken.None);
        await WaitForState(queued.JobId, DurableJobState.Failed);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, handler.InvocationCount);
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var job = await context.Jobs.SingleAsync(item => item.Id == queued.JobId);
            Assert.Equal(savedAccount.Id, job.ProviderAccountId);
            Assert.NotEqual(alternativeAccount.Id, job.ProviderAccountId);
            Assert.Equal("job_provider_account_unauthorized", job.LastErrorCode);
        }
    }

    [Fact]
    public async Task WorkerRefreshesRuntimeStorageAndDoesNotClaimWhileDatabaseIsUnavailable()
    {
        _options.PollIntervalMilliseconds = 25;
        var queued = await Enqueue("provider.work", "runtime-storage-guard");
        var storageOptions = new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_root, "jobs.db")}",
            BackupDirectory = Path.Combine(_root, "backups")
        };
        var storageState = new DurableStorageState(storageOptions);
        storageState.Set(DurableStorageReadiness.Ready, "startup-schema");
        var checkedStorage = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new UnavailableStorageProbe(storageState, checkedStorage);
        var handler = new CountingHandler();
        await using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new DurableJobWorker(
            _queue,
            _options,
            storageState,
            services,
            [handler],
            NullLogger<DurableJobWorker>.Instance,
            probe);

        await worker.StartAsync(CancellationToken.None);
        await checkedStorage.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, handler.InvocationCount);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(
            DurableJobState.Pending,
            (await context.Jobs.SingleAsync(item => item.Id == queued.JobId)).State);
    }

    private Task<DurableJobEnqueueResult> Enqueue(string type, string key) =>
        _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            type,
            key,
            new { itemId = key },
            _tenantId,
            _userId));

    private async Task<ProviderAccountRecord> AddProviderAccount(string providerId, Guid ownerUserId)
    {
        var account = new ProviderAccountRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            OwnerUserId = ownerUserId,
            ProviderId = providerId,
            DisplayName = $"{providerId} fixture",
            Scope = ProviderAccountScope.User,
            Enabled = true,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };
        await using var context = await _factory.CreateDbContextAsync();
        context.ProviderAccounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    private async Task WaitForState(Guid jobId, DurableJobState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            await using var context = await _factory.CreateDbContextAsync(timeout.Token);
            var state = await context.Jobs
                .Where(item => item.Id == jobId)
                .Select(item => item.State)
                .SingleAsync(timeout.Token);
            if (state == expected)
            {
                return;
            }

            await Task.Delay(20, timeout.Token);
        }

        throw new TimeoutException($"Job {jobId} did not reach {expected}.");
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class BlockingHandler(TaskCompletionSource started) : IDurableJobHandler
    {
        public string JobType => "restartable";

        public async Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return DurableJobCompletion.Success();
        }
    }

    private sealed class SuccessfulHandler(TaskCompletionSource completed) : IDurableJobHandler
    {
        public string JobType => "restartable";

        public Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            completed.TrySetResult();
            return Task.FromResult(DurableJobCompletion.Success());
        }
    }

    private sealed class CountingHandler : IDurableJobHandler
    {
        private int _invocationCount;

        public int InvocationCount => _invocationCount;
        public string JobType => "provider.work";

        public Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            return Task.FromResult(DurableJobCompletion.Success());
        }
    }

    private sealed class UnavailableStorageProbe(
        DurableStorageState state,
        TaskCompletionSource checkedStorage) : IDurableStorageRuntimeProbe
    {
        public Task<DurableStorageSnapshot> CheckAsync(
            CancellationToken cancellationToken = default)
        {
            state.Set(
                DurableStorageReadiness.Unavailable,
                errorCode: "database_unavailable");
            checkedStorage.TrySetResult();
            return Task.FromResult(state.GetSnapshot());
        }
    }

    private sealed class ReadyStorageProbe(DurableStorageState state)
        : IDurableStorageRuntimeProbe
    {
        public Task<DurableStorageSnapshot> CheckAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(state.GetSnapshot());
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
