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
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private FakeClock _clock = null!;
    private DurableJobOptions _options = null!;
    private DurableJobQueue _queue = null!;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        await using var context = await _factory.CreateDbContextAsync();
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
    public async Task Enqueue_IsIdempotent()
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
    }

    [Fact]
    public async Task ConcurrentEnqueue_CreatesOneDurableJob()
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
        Assert.Equal("personal_account", snapshot.AuthorizationRule);

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
    public async Task Progress_IsPersistedForTheActiveLeaseAndRedactsUnsafeDetails()
    {
        await Enqueue("playlist.materialize", "playlist-progress");
        var claim = (await _queue.ClaimNextAsync("worker-progress"))!;

        var reported = await _queue.ReportProgressAsync(
            claim,
            new DurableJobProgressUpdate(
                "provider-started",
                "Searching https://provider.invalid/catalog?token=fixture token=fixture",
                2,
                4,
                "spotify",
                "Release Radar",
                "Fixture track"));

        Assert.True(reported);
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var progress = Assert.Single(await context.AuditEvents.ToListAsync());
            Assert.Equal("job-progress", progress.Category);
            Assert.Equal("provider-started", progress.Action);
            Assert.Equal(claim.CorrelationId, progress.CorrelationId);
            Assert.Contains("\"completed\":2", progress.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("\"total\":4", progress.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("Release Radar", progress.DetailsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture", progress.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("redacted", progress.DetailsJson, StringComparison.OrdinalIgnoreCase);
        }

        await _queue.CompleteAsync(claim, DurableJobCompletion.Success());
        Assert.False(await _queue.ReportProgressAsync(
            claim,
            new DurableJobProgressUpdate("late", "This must not be persisted.")));
        await using var finalContext = await _factory.CreateDbContextAsync();
        Assert.Single(await finalContext.AuditEvents.ToListAsync());
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
            Assert.Contains("provider.invalid", scheduled.LastErrorMessage ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("token=<redacted>", scheduled.LastErrorMessage ?? string.Empty, StringComparison.Ordinal);
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
        var storageOptions = StorageOptions();
        var storageState = new DurableStorageState(storageOptions);
        storageState.Set(DurableStorageReadiness.Ready, "fixture");
        await using var services = new ServiceCollection().BuildServiceProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWorker = new DurableJobWorker(
            _queue,
            _options,
            storageState,
            services,
            [new BlockingHandler(started)],
            NullLogger<DurableJobWorker>.Instance);

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
            NullLogger<DurableJobWorker>.Instance);
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
    }

    [Fact]
    public async Task RunningCooperativeHandler_IsCancelledWhenLeaseRenewalSeesCancellationRequest()
    {
        _options.LeaseSeconds = 3;
        _options.PollIntervalMilliseconds = 20;
        var queued = await Enqueue("cooperative", "cooperative-cancellation");
        var storageOptions = StorageOptions();
        var storageState = new DurableStorageState(storageOptions);
        storageState.Set(DurableStorageReadiness.Ready, "fixture");
        await using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new CooperativeCancellationHandler();
        var worker = new DurableJobWorker(
            _queue,
            _options,
            storageState,
            services,
            [handler],
            NullLogger<DurableJobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await _queue.RequestCancellationAsync(queued.JobId, _tenantId));

        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForState(queued.JobId, DurableJobState.Cancelled);
        await worker.StopAsync(CancellationToken.None);

        await using var context = await _factory.CreateDbContextAsync();
        var job = await context.Jobs.SingleAsync(item => item.Id == queued.JobId);
        Assert.NotNull(job.CancellationRequestedAt);
        Assert.Equal(DurableJobState.Cancelled, job.State);
        var attempt = await context.JobAttempts.SingleAsync(item => item.JobId == queued.JobId);
        Assert.Equal("cancelled", attempt.Outcome);
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

        var storageOptions = StorageOptions();
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
            NullLogger<DurableJobWorker>.Instance);

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

    private Task<DurableJobEnqueueResult> Enqueue(string type, string key) =>
        _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            type,
            key,
            new { itemId = key },
            _tenantId,
            _userId));

    [Theory]
    [InlineData("user")]
    [InlineData("account")]
    [InlineData("revoked")]
    [InlineData("purpose")]
    [InlineData("credential-scope")]
    [InlineData("capability")]
    [InlineData("provider-removed")]
    [InlineData("account-scope")]
    public async Task Retry_RevalidatesExactInitiatorAccountCapabilityAndCredential(string change)
    {
        var account = await AddProviderAccount("deezer", _userId);
        var secretId = Guid.CreateVersion7();
        var otherUser = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(new PlatformUserRecord
            {
                Id = otherUser,
                TenantId = _tenantId,
                DisplayName = "Other",
                Status = PlatformUserStatus.Active
            });
            db.SecretReferences.Add(new SecretReferenceRecord
            {
                Id = secretId,
                TenantId = _tenantId,
                Purpose = $"provider-account:deezer:{account.Id:N}",
                ActiveVersion = 1,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            (await db.ProviderAccounts.SingleAsync(item => item.Id == account.Id)).SecretReferenceId = secretId;
            await db.SaveChangesAsync();
        }
        var registry = new Moq.Mock<allstarr.Core.Capabilities.IProviderRegistry>();
        allstarr.Core.Capabilities.ProviderDescriptor? descriptor = JobProvider();
        registry.Setup(item => item.TryGet("deezer", out descriptor)).Returns(true);
        var authorizer = new DurableJobContextAuthorizer(_factory, registry.Object);
        var queue = new DurableJobQueue(_factory, _options, new JobPayloadPolicy(_options), _clock, authorizer);
        var request = new DurableJobEnqueueRequest<object>("provider.download", "authorized-retry", new { track = "fixture" },
            _tenantId, _userId, ProviderAccountId: account.Id, Capability: "download");
        await queue.EnqueueAsync(request);
        var first = (await queue.ClaimNextAsync("worker"))!;
        Assert.True((await queue.ReauthorizeAsync(first)).Authorized);
        await queue.CompleteAsync(first, DurableJobCompletion.Retry("transient", "Try again", TimeSpan.FromSeconds(1)));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            if (change == "user") (await db.Users.SingleAsync(item => item.Id == _userId)).Status = PlatformUserStatus.Disabled;
            if (change == "account") (await db.ProviderAccounts.SingleAsync(item => item.Id == account.Id)).Enabled = false;
            if (change == "revoked") (await db.SecretReferences.SingleAsync()).RevokedAt = _clock.UtcNow;
            if (change == "purpose") (await db.SecretReferences.SingleAsync()).Purpose = "backend:unrelated";
            if (change == "credential-scope") (await db.SecretReferences.SingleAsync()).TenantId = null;
            await db.SaveChangesAsync();
        }
        if (change is "capability" or "account-scope" or "provider-removed")
        {
            descriptor = JobProvider(change == "capability", change == "account-scope");
            registry.Setup(item => item.TryGet("deezer", out descriptor)).Returns(change != "provider-removed");
        }
        _clock.Advance(TimeSpan.FromSeconds(2));
        var retried = (await queue.ClaimNextAsync("worker-retry"))!;
        Assert.NotNull(retried);
        Assert.Equal(2, retried.AttemptNumber);
        Assert.Equal(first.ProviderAccountId, retried.ProviderAccountId);
        Assert.False((await queue.ReauthorizeAsync(retried)).Authorized);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queue.EnqueueAsync(request with { IdempotencyKey = "new-denied" }));
    }

    [Fact]
    public async Task PersonalAccount_CannotBeUsedByAnotherActiveInitiator()
    {
        var account = await AddProviderAccount("deezer", _userId);
        var other = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Users.Add(new PlatformUserRecord
            {
                Id = other,
                TenantId = _tenantId,
                DisplayName = "Other",
                Status = PlatformUserStatus.Active
            });
            await db.SaveChangesAsync();
        }
        var authorizer = new DurableJobContextAuthorizer(_factory);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authorizer.AuthorizeEnqueueAsync(
            _tenantId, other, account.Id, null, "download", null));
        await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("fixture", "foreign-account", new { track = "fixture" },
            _tenantId, _userId, ProviderAccountId: account.Id, Capability: "download"));
        var claim = (await _queue.ClaimNextAsync("worker"))!;
        Assert.False((await authorizer.ReauthorizeAsync(claim with { OwnerUserId = other })).Authorized);
    }

    [Fact]
    public async Task SharedAccount_RequiresExplicitActiveInitiatorAndRejectsUnknownCapability()
    {
        var account = await AddProviderAccount("deezer", _userId);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var stored = await db.ProviderAccounts.SingleAsync();
            stored.OwnerUserId = null;
            stored.TenantId = null;
            await db.SaveChangesAsync();
        }
        var authorizer = new DurableJobContextAuthorizer(_factory);
        Assert.Equal(_userId, (await authorizer.AuthorizeEnqueueAsync(_tenantId, _userId, account.Id, null, "download", null)).OwnerUserId);
        await Assert.ThrowsAsync<ArgumentException>(() => authorizer.AuthorizeEnqueueAsync(_tenantId, null, account.Id, null, "download", null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authorizer.AuthorizeEnqueueAsync(_tenantId, _userId, account.Id, null, "anything", null));
    }

    private static allstarr.Core.Capabilities.ProviderDescriptor JobProvider(bool unavailable = false, bool sharedOnly = false) => new(
        "deezer", "Deezer", "Fixture provider", allstarr.Core.Capabilities.ProviderOrigin.BuiltIn, "1", "1.0",
        [new(allstarr.Core.Capabilities.ProviderCapabilityKind.Download,
            unavailable ? allstarr.Core.Capabilities.ProviderCapabilitySupportState.Unavailable : allstarr.Core.Capabilities.ProviderCapabilitySupportState.Supported,
            allstarr.Core.Capabilities.ProviderAccountRequirement.Required, "1.0", ["checkAvailability", "download"],
            sharedOnly ? [ProviderAccountScope.Shared] : [ProviderAccountScope.Personal, ProviderAccountScope.Shared])],
        new allstarr.Core.Capabilities.ProviderPermissionDescriptor());

    private StorageOptions StorageOptions() => new()
    {
        DataDirectory = _database.StorageOptions.DataDirectory,
        DatabaseFileName = _database.StorageOptions.DatabaseFileName
    };

    private async Task<ProviderAccountRecord> AddProviderAccount(string providerId, Guid ownerUserId)
    {
        var account = new ProviderAccountRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            OwnerUserId = ownerUserId,
            ProviderId = providerId,
            DisplayName = $"{providerId} fixture",
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

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
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

    private sealed class CooperativeCancellationHandler : IDurableJobHandler
    {
        public string JobType => "cooperative";
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DurableJobCompletion.Success();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
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
