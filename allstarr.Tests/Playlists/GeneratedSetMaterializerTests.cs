using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed class GeneratedSetMaterializerTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private readonly Guid _tenant = Guid.CreateVersion7(); private readonly Guid _user = Guid.CreateVersion7();
    private readonly Guid _backendIdentity = Guid.CreateVersion7(); private readonly Guid _set = Guid.CreateVersion7();
    private Factory _factory = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        await using var db = await _factory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow; var job = Guid.CreateVersion7(); var run = Guid.CreateVersion7();
        db.Tenants.Add(new() { Id = _tenant, Slug = "generated", Name = "Generated", CreatedAt = now });
        db.Users.Add(new() { Id = _user, TenantId = _tenant, DisplayName = "Owner", Status = PlatformUserStatus.Active, CreatedAt = now, UpdatedAt = now });
        db.BackendIdentities.Add(new()
        {
            Id = _backendIdentity,
            TenantId = _tenant,
            UserId = _user,
            BackendType = "jellyfin",
            BackendInstanceId = "main",
            PrincipalId = "principal",
            CreatedAt = now,
            LastSeenAt = now
        });
        db.Jobs.Add(new()
        {
            Id = job,
            ScopeKey = $"{_tenant:N}:{_user:N}",
            TenantId = _tenant,
            OwnerUserId = _user,
            Type = "recommendation.generate",
            PayloadJson = "{}",
            PolicySnapshotJson = "{}",
            RequestFingerprint = new string('a', 64),
            IdempotencyKey = "run",
            CorrelationId = "test",
            State = DurableJobState.Succeeded,
            MaxAttempts = 1,
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.RecommendationRuns.Add(new()
        {
            Id = run,
            TenantId = _tenant,
            OwnerUserId = _user,
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            JobId = job,
            IdempotencyKey = "run",
            Limit = 10,
            State = RecommendationRunState.Succeeded,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.GeneratedSets.Add(new()
        {
            Id = _set,
            RunId = run,
            TenantId = _tenant,
            OwnerUserId = _user,
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            Name = "My smart mix",
            CreatedAt = now
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public void Registration_AddsBothBackendFamilies()
    {
        var services = new ServiceCollection(); services.AddGeneratedSetMaterializers();
        var registrations = services.Where(item => item.ServiceType == typeof(IGeneratedSetMaterializer)).ToList();
        Assert.Equal(2, registrations.Count);
        Assert.Contains(registrations, item => item.ImplementationType == typeof(JellyfinGeneratedSetMaterializer));
        Assert.Contains(registrations, item => item.ImplementationType == typeof(SubsonicGeneratedSetMaterializer));
    }

    [Fact]
    public async Task Jellyfin_ReconcilesExactLocalMatchesInOrderAndDurablyExplainsSkips()
    {
        var first = await AddTrack("backend-1", "11111111-1111-1111-1111-111111111111");
        var second = await AddTrack("backend-2", "22222222-2222-2222-2222-222222222222");
        await AddEntries("one", "missing", "two");
        var target = new FakeTarget(BackendPlaylistFamily.Jellyfin);
        var materializer = new JellyfinGeneratedSetMaterializer(_factory, new Resolver(target));
        var request = Request("jellyfin",
            Candidate("one", new(LibraryTrackId: first)),
            Candidate("missing", new(MusicBrainzRecordingId: "33333333-3333-3333-3333-333333333333")),
            Candidate("two", new(LibraryTrackId: second)));

        var result = await materializer.MaterializeAsync(request, default);

        Assert.True(result.Succeeded);
        Assert.Equal(["backend-1", "backend-2"], target.Request!.OrderedBackendItemIds);
        Assert.Equal(BackendPlaylistWriteMode.Reconcile, target.Request.Mode);
        Assert.Contains("[Allstarr", target.Request.Metadata.Name, StringComparison.Ordinal);
        Assert.Contains("exact local library matches", target.Request.Metadata.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Null(target.Request.Metadata.Artwork);
        await using var db = await _factory.CreateDbContextAsync();
        var entries = await db.GeneratedSetEntries.OrderBy(item => item.Position).ToListAsync();
        Assert.Contains("materialization-local-match", entries[0].ExplanationJson);
        Assert.Contains("materialization-metadata-limited", entries[0].ExplanationJson);
        Assert.Contains("materialization-skipped-unmatched", entries[1].ExplanationJson);
        Assert.Contains("materialization-local-match", entries[2].ExplanationJson);
    }

    [Fact]
    public async Task Jellyfin_RepeatedMaterializationUsesStableNameKeyAndNeverDownloads()
    {
        var track = await AddTrack("backend-1", null); await AddEntries("one");
        var target = new FakeTarget(BackendPlaylistFamily.Jellyfin) { Existing = true };
        var materializer = new JellyfinGeneratedSetMaterializer(_factory, new Resolver(target));
        var request = Request("jellyfin", Candidate("one", new(LibraryTrackId: track)));

        Assert.True((await materializer.MaterializeAsync(request, default)).Succeeded);
        Assert.True((await materializer.MaterializeAsync(request, default)).Succeeded);

        Assert.Equal(2, target.Writes);
        Assert.All(target.Names, value => Assert.Equal(target.Names[0], value));
        Assert.Equal("generated-set:test", target.Request!.IdempotencyKey);
        Assert.DoesNotContain(typeof(JellyfinGeneratedSetMaterializer).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType.Name.Contains("Download", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Jellyfin_ScheduledOccurrenceReusesPreviousBackendPlaylistById()
    {
        var track = await AddTrack("backend-1", null); await AddEntries("one");
        var scheduleId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.JobSchedules.Add(new()
            {
                Id = scheduleId,
                TenantId = _tenant,
                OwnerUserId = _user,
                LibraryScopeId = "music",
                JobType = DurableScheduleEngine.RecommendationJobType,
                CronExpression = "0 3 * * *",
                TimeZoneId = "UTC",
                RetryPolicyJson = "{}",
                PayloadTemplateJson = "{}",
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            (await db.GeneratedSets.SingleAsync()).ScheduleId = scheduleId;
            var previousJobId = Guid.CreateVersion7(); var previousRunId = Guid.CreateVersion7();
            db.Jobs.Add(new()
            {
                Id = previousJobId,
                ScopeKey = $"{_tenant:N}:{_user:N}",
                TenantId = _tenant,
                OwnerUserId = _user,
                LibraryScopeId = "music",
                Type = "recommendation.generate",
                PayloadJson = "{}",
                PolicySnapshotJson = "{}",
                RequestFingerprint = new string('b', 64),
                IdempotencyKey = "previous-run",
                CorrelationId = "previous-run",
                State = DurableJobState.Succeeded,
                MaxAttempts = 1,
                AvailableAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
            });
            db.RecommendationRuns.Add(new()
            {
                Id = previousRunId,
                TenantId = _tenant,
                OwnerUserId = _user,
                Protocol = "jellyfin",
                BackendInstanceId = "main",
                LibraryScopeId = "music",
                JobId = previousJobId,
                IdempotencyKey = "previous-run",
                Limit = 10,
                State = RecommendationRunState.Succeeded,
                ScheduleId = scheduleId,
                ScheduledFor = DateTimeOffset.UtcNow.AddDays(-1),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
            });
            db.GeneratedSets.Add(new()
            {
                Id = Guid.CreateVersion7(),
                RunId = previousRunId,
                TenantId = _tenant,
                OwnerUserId = _user,
                Protocol = "jellyfin",
                BackendInstanceId = "main",
                LibraryScopeId = "music",
                Name = "Old display name",
                ScheduleId = scheduleId,
                MaterializationState = GeneratedSetMaterializationState.Succeeded,
                BackendPlaylistId = "playlist-from-yesterday",
                MaterializedAt = DateTimeOffset.UtcNow.AddDays(-1),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }
        var target = new FakeTarget(BackendPlaylistFamily.Jellyfin);
        var materializer = new JellyfinGeneratedSetMaterializer(_factory, new Resolver(target));

        var result = await materializer.MaterializeAsync(Request("jellyfin",
            Candidate("one", new(LibraryTrackId: track))), default);

        Assert.True(result.Succeeded);
        Assert.Equal(["playlist-from-yesterday"], target.ReadIds);
        Assert.Empty(target.Names);
        Assert.Equal("playlist-from-yesterday", target.Request!.BackendPlaylistId);
    }

    [Fact]
    public async Task Subsonic_RequiresExactScopedEncryptedCredentialBeforeTargetCall()
    {
        await ChangeProtocol("subsonic"); var track = await AddTrack("song-1", null, "subsonic"); await AddEntries("one");
        var unrelated = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SecretReferences.Add(new()
            {
                Id = unrelated,
                TenantId = _tenant,
                Purpose = "unrelated",
                ActiveVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            (await db.GeneratedSets.SingleAsync()).TargetCredentialReferenceId = unrelated;
            await db.SaveChangesAsync();
        }
        var target = new FakeTarget(BackendPlaylistFamily.Subsonic);
        var materializer = new SubsonicGeneratedSetMaterializer(_factory, new Resolver(target));

        var result = await materializer.MaterializeAsync(Request("subsonic", Candidate("one", new(LibraryTrackId: track))), default);

        Assert.False(result.Succeeded); Assert.Equal("generated_set_subsonic_credential_unavailable", result.SafeErrorCode);
        Assert.Equal(0, target.Writes);
    }

    [Fact]
    public async Task Subsonic_PassesOnlySnapshottedSameTenantCredentialReference()
    {
        await ChangeProtocol("subsonic"); var track = await AddTrack("song-1", null, "subsonic"); await AddEntries("one");
        var credential = Guid.CreateVersion7(); await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SecretReferences.Add(new()
            {
                Id = credential,
                TenantId = _tenant,
                BackendIdentityId = _backendIdentity,
                Purpose = BackendCredentialScope.SubsonicPurpose,
                ActiveVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            (await db.GeneratedSets.SingleAsync()).TargetCredentialReferenceId = credential; await db.SaveChangesAsync();
        }
        var target = new FakeTarget(BackendPlaylistFamily.Subsonic);
        var materializer = new SubsonicGeneratedSetMaterializer(_factory, new Resolver(target));

        var result = await materializer.MaterializeAsync(Request("subsonic", Candidate("one", new(LibraryTrackId: track))), default);

        Assert.True(result.Succeeded); Assert.Equal(credential.ToString(), target.Context!.CredentialReference);
        Assert.Equal(_tenant, target.Context.TenantId);
    }

    [Fact]
    public async Task DurableHandler_RetryThenSuccessPersistsOutcomeWithoutConcurrencyFailure()
    {
        await AddEntries("one"); var target = new SequenceMaterializer();
        var handler = new GeneratedSetMaterializationJobHandler(_factory, [target], new HandlerClock());
        var claim = new DurableJobClaim(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "smart-playlist.materialize",
            JsonSerializer.SerializeToElement(new GeneratedSetMaterializationPayload(_set)), _tenant, _user, null,
            "music", null, JsonSerializer.SerializeToElement(new { }), "generated-test", "worker", DateTimeOffset.UtcNow.AddMinutes(1));
        var services = new ServiceCollection().BuildServiceProvider();

        var retry = await handler.ExecuteAsync(new(claim, services), default);
        Assert.Equal(DurableJobCompletionKind.Retry, retry.Kind);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var set = await db.GeneratedSets.SingleAsync(); Assert.Equal(GeneratedSetMaterializationState.Failed, set.MaterializationState);
            Assert.Equal("temporary", set.LastErrorCode);
        }
        var success = await handler.ExecuteAsync(new(claim, services), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, success.Kind);
        await using var verified = await _factory.CreateDbContextAsync(); var completed = await verified.GeneratedSets.SingleAsync();
        Assert.Equal(GeneratedSetMaterializationState.Succeeded, completed.MaterializationState);
        Assert.Equal("playlist-42", completed.BackendPlaylistId); Assert.Equal("revision-42", completed.TargetRevision);
    }

    [Fact]
    public async Task DurableHandler_CancellationPersistsCancelledOutcome()
    {
        await AddEntries("one"); using var cancellation = new CancellationTokenSource();
        var handler = new GeneratedSetMaterializationJobHandler(_factory, [new CancellingMaterializer(cancellation)], new HandlerClock());
        var claim = new DurableJobClaim(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "smart-playlist.materialize",
            JsonSerializer.SerializeToElement(new GeneratedSetMaterializationPayload(_set)), _tenant, _user, null,
            "music", null, JsonSerializer.SerializeToElement(new { }), "generated-test", "worker", DateTimeOffset.UtcNow.AddMinutes(1));

        var completion = await handler.ExecuteAsync(new(claim, new ServiceCollection().BuildServiceProvider()), cancellation.Token);

        Assert.Equal(DurableJobCompletionKind.Cancelled, completion.Kind);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(GeneratedSetMaterializationState.Cancelled, (await db.GeneratedSets.SingleAsync()).MaterializationState);
    }

    private async Task<Guid> AddTrack(string backendItem, string? mbid, string protocol = "jellyfin")
    {
        await using var db = await _factory.CreateDbContextAsync(); var id = Guid.CreateVersion7(); var now = DateTimeOffset.UtcNow;
        db.LibraryTracks.Add(new()
        {
            Id = id,
            TenantId = _tenant,
            OwnerUserId = _user,
            BackendIdentityId = _backendIdentity,
            LibraryScopeId = "music",
            Protocol = protocol,
            BackendInstanceId = "main",
            BackendItemId = backendItem,
            FilePath = $"/music/{backendItem}.flac",
            Title = backendItem,
            Artist = "Artist",
            DurationMilliseconds = 1000,
            MusicBrainzRecordingId = mbid,
            ProviderIdsJson = "{}",
            IndexedAt = now,
            SourceModifiedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(); return id;
    }
    private async Task AddEntries(params string[] keys)
    {
        await using var db = await _factory.CreateDbContextAsync();
        for (var i = 0; i < keys.Length; i++) db.GeneratedSetEntries.Add(new()
        {
            Id = Guid.CreateVersion7(),
            GeneratedSetId = _set,
            TenantId = _tenant,
            OwnerUserId = _user,
            Position = i,
            TrackKey = keys[i],
            Source = "fixture",
            Score = .8,
            ExplanationJson = JsonSerializer.Serialize(new[] { new RecommendationSignal("fixture", .8, "Fixture reason") }),
            IdentityJson = "{}"
        });
        await db.SaveChangesAsync();
    }
    private async Task ChangeProtocol(string protocol)
    {
        await using var db = await _factory.CreateDbContextAsync();
        (await db.BackendIdentities.SingleAsync()).BackendType = protocol; (await db.RecommendationRuns.SingleAsync()).Protocol = protocol;
        (await db.GeneratedSets.SingleAsync()).Protocol = protocol; await db.SaveChangesAsync();
    }
    private GeneratedSetMaterializationRequest Request(string protocol, params RecommendationCandidate[] candidates) =>
        new(new(_tenant, _user, protocol, "main", "music"), _set, candidates, "generated-set:test");
    private static RecommendationCandidate Candidate(string key, RecommendationTrackIdentity identity) =>
        new(key, .8, "fixture", [new("fixture", .8, "Fixture reason")], identity);
    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    { public AllstarrDbContext CreateDbContext() => new(options); public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext()); }
    private sealed class Resolver(IBackendPlaylistTarget target) : IBackendPlaylistTargetResolver { public IBackendPlaylistTarget Resolve(string targetProtocol) => target; }
    private sealed class HandlerClock : allstarr.Core.Operations.IPlatformClock { public DateTimeOffset UtcNow => new(2026, 7, 12, 23, 30, 0, TimeSpan.Zero); }
    private sealed class SequenceMaterializer : IGeneratedSetMaterializer
    {
        private int _calls; public string Protocol => "jellyfin";
        public Task<GeneratedSetMaterializationResult> MaterializeAsync(GeneratedSetMaterializationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(++_calls == 1 ? new GeneratedSetMaterializationResult(false, true, "temporary") :
                new GeneratedSetMaterializationResult(true, BackendPlaylistId: "playlist-42", TargetRevision: "revision-42"));
    }
    private sealed class CancellingMaterializer(CancellationTokenSource source) : IGeneratedSetMaterializer
    { public string Protocol => "jellyfin"; public Task<GeneratedSetMaterializationResult> MaterializeAsync(GeneratedSetMaterializationRequest request, CancellationToken cancellationToken) { source.Cancel(); throw new OperationCanceledException(cancellationToken); } }
    private sealed class FakeTarget(BackendPlaylistFamily family) : IBackendPlaylistTarget
    {
        public BackendPlaylistFamily Family => family; public BackendPlaylistTargetCapabilities Capabilities { get; } = new(true, true, true, true, true, true, false, false, false);
        public BackendPlaylistWriteRequest? Request { get; private set; }
        public int Writes { get; private set; }
        public bool Existing { get; set; }
        public BackendPlaylistTargetContext? Context { get; private set; }
        public List<string> Names { get; } = [];
        public List<string> ReadIds { get; } = [];
        public Task<BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>> ListAsync(BackendPlaylistTargetContext context, string? query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>(BackendPlaylistTargetStatus.Success, []));
        public Task<BackendPlaylistTargetResult<BackendPlaylistArtwork>> ReadArtworkAsync(BackendPlaylistTargetContext context, string backendPlaylistId, string? artworkReference, CancellationToken cancellationToken) =>
            Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistArtwork>(BackendPlaylistTargetStatus.NotFound));
        public Task<BackendPlaylistTargetResult<BackendPlaylistSnapshot?>> FindByNameAsync(BackendPlaylistTargetContext context, string name, CancellationToken cancellationToken)
        { Context = context; Names.Add(name); BackendPlaylistSnapshot? value = Existing ? new("playlist-1", name, [], BackendPlaylistSnapshot.ComputeFingerprint("playlist-1", name, [])) : null; return Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistSnapshot?>(BackendPlaylistTargetStatus.Success, value)); }
        public Task<BackendPlaylistTargetResult<BackendPlaylistSnapshot>> ReadAsync(BackendPlaylistTargetContext context, string backendPlaylistId, CancellationToken cancellationToken)
        {
            Context = context; ReadIds.Add(backendPlaylistId);
            var value = new BackendPlaylistSnapshot(backendPlaylistId, "Existing schedule playlist", [],
                BackendPlaylistSnapshot.ComputeFingerprint(backendPlaylistId, "Existing schedule playlist", []));
            return Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistSnapshot>(
                BackendPlaylistTargetStatus.Success, value));
        }
        public Task<BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>> WriteAsync(BackendPlaylistTargetContext context, BackendPlaylistWriteRequest request, CancellationToken cancellationToken)
        {
            Request = request; Writes++; var snapshot = new BackendPlaylistSnapshot(request.BackendPlaylistId ?? "playlist-1", request.Metadata.Name,
            request.OrderedBackendItemIds.Select(id => new BackendPlaylistMember(id)).ToArray(), BackendPlaylistSnapshot.ComputeFingerprint("playlist-1", request.Metadata.Name, request.OrderedBackendItemIds.Select(id => new BackendPlaylistMember(id))), Description: request.Metadata.Description);
            return Task.FromResult(new BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>(BackendPlaylistTargetStatus.Success, new(snapshot, true, ["artwork"])));
        }
    }
}
