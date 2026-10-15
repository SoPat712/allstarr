using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed class RecordingKeptFilesTests : IAsyncLifetime
{
    private readonly Guid owner = Guid.CreateVersion7();
    private readonly Guid other = Guid.CreateVersion7();
    private readonly Guid account = Guid.CreateVersion7();
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-kept", Guid.NewGuid().ToString("N"));
    private readonly byte[] audio = "fLaCverified kept fixture audio"u8.ToArray();
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private ProviderDownloadArtifactResolver artifacts = null!;
    private TrackIdentityService identities = null!;
    private DurableJobQueue queue = null!;
    private ServiceProvider services = null!;
    private ManagedTrackPlacementOptions placement = null!;
    private Action? placementBoundary;
    private Guid jobId;
    private Guid recordingId;
    private VerifiedProviderDownloadArtifact artifact = null!;

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        var now = DateTimeOffset.UtcNow;
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.Users.AddRange(User(owner), User(other));
            db.ProviderAccounts.Add(new()
            {
                Id = account,
                OwnerUserId = owner,
                ProviderId = "deezer",
                DisplayName = "Source",
                Enabled = true,
                Revision = 7,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }
        var options = new DurableJobOptions();
        queue = new(factory, options, new JobPayloadPolicy(options), new SystemPlatformClock());
        jobId = (await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "recording.keep", "fixture-keep", new { item = "track" }, owner))).JobId;
        Assert.NotNull(await queue.ClaimNextAsync("fixture"));
        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        identities = new(factory, state, new SystemPlatformClock());
        recordingId = (await identities.CreateRecordingAsync(Actor(owner), "fixture-recording")).Recording.Id;
        await identities.LinkAsync(SourceContext(), new(recordingId,
            new("deezer", ProviderResourceKind.Track, "source-track"), ProviderIdentityScope.Catalog,
            ProviderIdentityVerification.Verified, "fixture-exact", 1));
        artifacts = new(new EfProviderDownloadArtifactStore(factory), new() { RootPath = Path.Combine(root, "workspaces") });
        var workspace = await artifacts.CreateWorkspaceAsync(new(owner, jobId, "deezer", account, "fixture-source"));
        await using var input = new MemoryStream(audio);
        var written = await artifacts.WriteAsync(new(workspace.Reference, jobId, "deezer", "source.flac", input, 1000));
        artifact = await artifacts.ResolveAsync(workspace.Reference,
            new(written.ArtifactId, written.Sha256, written.SizeBytes, new("audio/flac", "flac", "flac"), true),
            sourceTrack: new("deezer", ProviderResourceKind.Track, "source-track"), sourceAccountRevision: 7);
        placement = new() { RootId = Guid.CreateVersion7(), RootPath = Path.Combine(root, "kept"), PathTemplate = "{artist}/{title}" };
        var collection = new ServiceCollection();
        collection.AddDbContext<AllstarrDbContext>(builder => builder.UseSqlite(database.ConnectionString));
        collection.AddManagedFilePlacement();
        collection.AddSingleton<IManagedFileOperations>(new BoundaryFiles(new PhysicalManagedFileOperations(), path =>
        {
            if (!path.StartsWith(Path.GetFullPath(placement.RootPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
            Interlocked.Exchange(ref placementBoundary, null)?.Invoke();
        }));
        services = collection.BuildServiceProvider();
    }

    [Fact]
    public async Task PublishVerifiesPersonalSourceAndCreatesHouseholdFileWithDurableProvenance()
    {
        var result = await Service().PublishAsync(Request());
        Assert.Equal(recordingId, result.RecordingId);
        Assert.Equal(audio, await File.ReadAllBytesAsync(result.Path));
        Assert.StartsWith(Path.GetFullPath(placement.RootPath) + Path.DirectorySeparatorChar, result.Path, StringComparison.Ordinal);
        Assert.Equal(audio.Length, result.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(audio)), result.ContentSha256);
        Assert.DoesNotContain(result.Path, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        await using var db = new AllstarrDbContext(database.Options);
        var file = Assert.Single(await db.ManagedFiles.ToArrayAsync());
        Assert.Null(file.OwnerUserId);
        Assert.Null(file.SourceJobId);
        Assert.Equal(ManagedFileScopeKey.Create(null, placement.RootId), file.ScopeKey);
        Assert.Equal(1, file.ReferenceCount);
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM recording_kept_routes WHERE PublicationJobId = {jobId} AND PublishedByUserId = {owner} AND SourceArtifactId = {artifact.Id} AND ManagedFileId = {file.Id}").SingleAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        Assert.Equal(ProviderDownloadArtifactState.Verified, (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).State);
    }

    [Fact]
    public async Task RetryAfterRestartReusesFileAndReferenceWithoutDuplicatingPublication()
    {
        var first = await Service().PublishAsync(Request());
        var second = await Service().PublishAsync(Request());
        Assert.Equal(first.ManagedFileId, second.ManagedFileId);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Single(await db.ManagedFiles.ToArrayAsync());
        Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM recording_kept_routes").SingleAsync());
    }

    [Theory]
    [InlineData("user")]
    [InlineData("job")]
    [InlineData("artifact")]
    [InlineData("recording")]
    [InlineData("provider")]
    [InlineData("disabled-user")]
    [InlineData("disabled-account")]
    [InlineData("account-revision")]
    [InlineData("cancelled")]
    [InlineData("stale-execution")]
    [InlineData("reserved-reference")]
    public async Task UnauthorizedOrChangedPublicationCannotCreateHouseholdFile(string change)
    {
        var request = Request();
        await using (var db = new AllstarrDbContext(database.Options))
        {
            if (change == "disabled-user") (await db.Users.SingleAsync(item => item.Id == owner)).Enabled = false;
            if (change == "disabled-account") (await db.ProviderAccounts.SingleAsync()).Enabled = false;
            if (change == "account-revision") (await db.ProviderAccounts.SingleAsync()).Revision++;
            await db.SaveChangesAsync();
        }
        request = change switch
        {
            "user" => request with { InitiatingUserId = other },
            "job" => request with { DurableJobId = Guid.CreateVersion7() },
            "artifact" => request with { SourceArtifactId = Guid.CreateVersion7() },
            "recording" => request with { RecordingId = Guid.CreateVersion7() },
            "provider" => request with { SourceProviderId = "qobuz" },
            "reserved-reference" => request with { ReferenceKey = $"pending:kept:{jobId:N}:forged-interest" },
            _ => request
        };
        if (change == "cancelled") Assert.True(await queue.RequestCancellationAsync(jobId, owner));
        if (change == "stale-execution")
        {
            Assert.Equal(1, await queue.RecoverInterruptedAsync());
            Assert.Equal(2, (await queue.ClaimNextAsync("restarted"))!.AttemptNumber);
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().PublishAsync(request));
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Empty(await verify.ManagedFiles.ToArrayAsync());
        Assert.False(Directory.Exists(placement.RootPath) && Directory.EnumerateFiles(placement.RootPath, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task ArtifactCannotPublishAnotherUsersWorkspaceBytes()
    {
        var foreignJob = (await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "recording.keep", "foreign-workspace", new { item = "other" }, other))).JobId;
        var foreignWorkspace = await artifacts.CreateWorkspaceAsync(new(other, foreignJob, "deezer", null, "foreign-workspace"));
        var foreignBytes = "private bytes from another workspace"u8.ToArray();
        var written = await artifacts.WriteAsync(new(foreignWorkspace.Reference, foreignJob, "deezer", "foreign.flac",
            new MemoryStream(foreignBytes), 1024));
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var source = await db.Set<ProviderDownloadArtifactEntity>().SingleAsync();
            source.WorkspaceRecordId = foreignWorkspace.RecordId;
            source.WorkspaceId = foreignWorkspace.Reference.WorkspaceId;
            source.RelativePath = written.ArtifactId;
            source.ContentSha256 = written.Sha256;
            source.Length = written.SizeBytes;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().PublishAsync(Request()));
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Empty(await verify.ManagedFiles.ToArrayAsync());
        Assert.Empty(await verify.Set<RecordingKeptRouteEntity>().ToArrayAsync());
    }

    [Fact]
    public async Task ChangedVerifiedBytesCannotBePublished()
    {
        await File.WriteAllBytesAsync(artifact.SourcePath, new byte[audio.Length]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().PublishAsync(Request()));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.ManagedFiles.ToArrayAsync());
    }

    [Fact]
    public async Task DefaultConfiguredTemplateKeepsTheVerifiedAudioExtension()
    {
        placement.PathTemplate = new ManagedTrackPlacementOptions().PathTemplate;
        var kept = await Service().PublishAsync(Request());
        Assert.Equal(".flac", Path.GetExtension(kept.Path));
        Assert.Equal(audio, await File.ReadAllBytesAsync(kept.Path));
    }

    [Fact]
    public async Task PublicationFollowsTheVerifiedRecordingSurvivor()
    {
        var survivor = (await identities.CreateRecordingAsync(Actor(owner), "fixture-survivor")).Recording.Id;
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var previous = await db.CanonicalRecordings.SingleAsync(item => item.Id == recordingId);
            previous.MergedIntoId = survivor;
            previous.Revision++;
            await db.SaveChangesAsync();
        }
        var kept = await Service().PublishAsync(Request() with { RecordingId = survivor });
        Assert.Equal(survivor, kept.RecordingId);
        Assert.Equal(audio, await File.ReadAllBytesAsync(kept.Path));
    }

    [Fact]
    public async Task ReuseRechecksCancellationBeforeAcquiringANewInterest()
    {
        await Service().PublishAsync(Request());
        ArmBoundary(db =>
        {
            var job = db.Jobs.Single();
            job.CancellationRequestedAt = DateTimeOffset.UtcNow;
            job.Revision++;
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().PublishAsync(
            Request() with { ReferenceKey = "explicit-second-interest" }));
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Equal(1, await verify.ManagedFileReferences.CountAsync(item => item.ReleasedAt == null));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("source-changed")]
    public async Task RejectedPublicationReleasesItsProvisionalFileAndReference(string change)
    {
        ArmBoundary(db =>
        {
            if (change == "cancelled")
            {
                var job = db.Jobs.Single();
                job.CancellationRequestedAt = DateTimeOffset.UtcNow;
                job.Revision++;
            }
            else db.Set<ProviderDownloadArtifactEntity>().Single().SourceExternalTrackId = "different-track";
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().PublishAsync(Request()));
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Empty(await verify.ManagedFiles.Where(item => item.RemovedAt == null).ToArrayAsync());
        Assert.Empty(await verify.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        Assert.Empty(Directory.EnumerateFiles(placement.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task HouseholdReadDoesNotRequireTheOriginalProviderAccount()
    {
        var published = await Service().PublishAsync(Request());
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var saved = await db.ProviderAccounts.SingleAsync();
            saved.Enabled = false;
            saved.Revision++;
            await db.SaveChangesAsync();
        }
        var read = await Service().FindAsync(Actor(other), recordingId);
        Assert.NotNull(read);
        Assert.Equal(published.ManagedFileId, read.ManagedFileId);
        Assert.Equal(audio, await File.ReadAllBytesAsync(read.Path));
        Assert.Null(await Service().FindAsync(new(ProviderActorKind.PublicRead, null), recordingId));
        var mismatch = new ProviderActorContext(ProviderActorKind.User, other, new("jellyfin", "other-instance", other.ToString("N")));
        Assert.Null(await Service().FindAsync(mismatch, recordingId));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("missing")]
    [InlineData("symlink")]
    public async Task HouseholdReadRejectsMissingOrChangedOwnedBytes(string change)
    {
        var published = await Service().PublishAsync(Request());
        if (change == "bytes") await File.WriteAllBytesAsync(published.Path, new byte[audio.Length]);
        else
        {
            File.Delete(published.Path);
            if (change == "symlink") File.CreateSymbolicLink(published.Path, artifact.SourcePath);
        }
        Assert.Null(await Service().FindAsync(Actor(other), recordingId));
    }

    [Fact]
    public async Task HouseholdReadFollowsAnAlreadyPublishedRecordingAfterMerge()
    {
        var published = await Service().PublishAsync(Request());
        var survivor = (await identities.CreateRecordingAsync(Actor(owner), "published-survivor")).Recording.Id;
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var previous = await db.CanonicalRecordings.SingleAsync(item => item.Id == recordingId);
            previous.MergedIntoId = survivor;
            previous.Revision++;
            await db.SaveChangesAsync();
        }
        var read = await Service().FindAsync(Actor(other), survivor);
        Assert.NotNull(read);
        Assert.Equal(survivor, read.RecordingId);
        Assert.Equal(published.ManagedFileId, read.ManagedFileId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DatabaseRejectsPersonalFilesOrMismatchedPublicationOwner(bool personalFile)
    {
        await using var scope = services.CreateAsyncScope();
        var fileOwner = personalFile ? (Guid?)owner : null;
        var placed = await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlaceAsync(new(
            new(placement.RootId, Path.Combine(root, personalFile ? "private" : "shared"), fileOwner),
            artifact.SourcePath, "{title}", new("Raw boundary", "Artist", Extension: ".flac"),
            personalFile ? jobId : null, ManagedFileScopeKey.Create(fileOwner, placement.RootId), true,
            ExpectedContentSha256: artifact.ContentSha256, ExpectedLength: artifact.Length)
        { ReferenceKey = "raw-boundary" });
        await using var db = new AllstarrDbContext(database.Options);
        db.Add(new RecordingKeptRouteEntity
        {
            CanonicalRecordingId = recordingId,
            ManagedFileId = placed.File.Id,
            PublishedByUserId = personalFile ? owner : other,
            PublicationJobId = jobId,
            SourceArtifactId = artifact.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DatabaseRejectsArtifactWithMismatchedWorkspaceIdentity()
    {
        await using var scope = services.CreateAsyncScope();
        var placed = await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlaceAsync(new(
            new(placement.RootId, Path.GetFullPath(placement.RootPath), null), artifact.SourcePath,
            "{title}", new("Workspace boundary", "Artist", Extension: ".flac"),
            null, ManagedFileScopeKey.Create(null, placement.RootId), true,
            ExpectedContentSha256: artifact.ContentSha256, ExpectedLength: artifact.Length)
        { ReferenceKey = "workspace-boundary" });
        await using var db = new AllstarrDbContext(database.Options);
        (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).WorkspaceId = "unregistered-workspace";
        await db.SaveChangesAsync();
        db.Add(new RecordingKeptRouteEntity
        {
            CanonicalRecordingId = recordingId,
            ManagedFileId = placed.File.Id,
            PublishedByUserId = owner,
            PublicationJobId = jobId,
            SourceArtifactId = artifact.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DatabaseRetainsPublishedSourceOwnershipProof()
    {
        await Service().PublishAsync(Request());
        await using var db = new AllstarrDbContext(database.Options);
        var saved = await db.Set<ProviderDownloadArtifactEntity>().SingleAsync();
        saved.OwnerUserId = other;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("source-track")]
    [InlineData("source-hash")]
    [InlineData("file-path")]
    [InlineData("file-revocation")]
    [InlineData("workspace-identity")]
    public async Task PublishedProvenanceAndOwnedFileCannotBeRetargeted(string change)
    {
        await Service().PublishAsync(Request());
        await using var db = new AllstarrDbContext(database.Options);
        if (change == "workspace-identity")
            (await db.Set<ProviderDownloadWorkspaceEntity>().SingleAsync()).WorkspaceId = "replaced-workspace";
        else if (change.StartsWith("source-", StringComparison.Ordinal))
        {
            var source = await db.Set<ProviderDownloadArtifactEntity>().SingleAsync();
            if (change == "source-track") source.SourceExternalTrackId = "different-track";
            else source.ContentSha256 = new string('a', 64);
        }
        else
        {
            var file = await db.ManagedFiles.SingleAsync();
            if (change == "file-path") file.CanonicalPath = Path.Combine(root, "unrelated.flac");
            else
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IManagedFileOwnershipStore>()
                    .ReleaseReferenceAsync(file.Id, Request().ReferenceKey, default);
                db.ChangeTracker.Clear();
                file = await db.ManagedFiles.SingleAsync();
                file.RemovedAt = DateTimeOffset.UtcNow;
            }
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task RestartAdoptsAnUnpublishedPlacementWithoutDuplicatingReferences()
    {
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlaceAsync(new(
                new(placement.RootId, Path.GetFullPath(placement.RootPath), null), artifact.SourcePath,
                placement.PathTemplate, Request().Track, null, ManagedFileScopeKey.Create(null, placement.RootId), true, true,
                artifact.ContentSha256, artifact.Length)
            {
                ReferenceKey = $"pending:kept:{jobId:N}:{recordingId:N}:1:interrupted",
                DestinationIsImmutable = true
            });
        }
        Assert.Equal(1, await queue.RecoverInterruptedAsync());
        Assert.Equal(2, (await queue.ClaimNextAsync("restart"))!.AttemptNumber);
        var published = await Service().PublishAsync(Request() with { AttemptNumber = 2 });
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Single(await db.ManagedFiles.Where(item => item.RemovedAt == null).ToArrayAsync());
        var reference = Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        Assert.Equal(Request().ReferenceKey, reference.ReferenceKey);
        Assert.Equal(published.ManagedFileId, reference.ManagedFileId);
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM recording_kept_routes").SingleAsync());
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task UpgradeAndRollbackPreserveLegacyPersonalArtifactBytesAndReferences()
    {
        await using var scope = services.CreateAsyncScope();
        var placed = await scope.ServiceProvider.GetRequiredService<FilePlacementService>().PlaceAsync(new(
            new(placement.RootId, Path.Combine(root, "private"), owner), artifact.SourcePath,
            "{title}", new("Existing personal download", "Artist", Extension: ".flac"),
            jobId, ManagedFileScopeKey.Create(owner, placement.RootId), true,
            ExpectedContentSha256: artifact.ContentSha256, ExpectedLength: artifact.Length)
        { ReferenceKey = "legacy-personal" });
        var artifactStore = new EfProviderDownloadArtifactStore(factory);
        await artifactStore.MarkPlacedAsync(artifact.Id, placed.File.Id, default);
        await using var db = new AllstarrDbContext(database.Options);
        const string before = "20261015064546_SimplifyDurableJobs";
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(before);
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE name='recording_kept_routes'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_table_info('provider_download_artifacts') WHERE name IN ('SourceExternalTrackId','SourceAccountRevision')").SingleAsync());
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var saved = await db.Set<ProviderDownloadArtifactEntity>().SingleAsync();
        Assert.Equal(artifact.Id, saved.Id);
        Assert.Equal(owner, saved.OwnerUserId);
        Assert.Equal(jobId, saved.DurableJobId);
        Assert.Equal(account, saved.ProviderAccountId);
        Assert.Equal(ProviderDownloadArtifactState.Placed, saved.State);
        Assert.Equal(placed.File.Id, saved.ManagedFileId);
        Assert.Null(saved.SourceExternalTrackId);
        Assert.Null(saved.SourceAccountRevision);
        Assert.Equal(artifact.ContentSha256, saved.ContentSha256);
        Assert.Equal(artifact.Length, saved.Length);
        var reference = Assert.Single(await db.ManagedFileReferences.AsNoTracking().ToArrayAsync());
        Assert.Equal("legacy-personal", reference.ReferenceKey);
        Assert.Equal(owner, reference.OwnerUserId);
        Assert.Null(reference.ReleasedAt);
        Assert.Equal(1, (await db.ManagedFiles.SingleAsync()).ReferenceCount);
        Assert.Empty(await db.Set<RecordingKeptRouteEntity>().ToArrayAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().PublishAsync(Request()));
        Assert.Equal(audio, await File.ReadAllBytesAsync(placed.File.CanonicalPath));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        await migrator.MigrateAsync(before);
        await db.Database.MigrateAsync();
        Assert.Equal(audio, await File.ReadAllBytesAsync(placed.File.CanonicalPath));
        Assert.Equal(1, await db.ManagedFileReferences.CountAsync(item => item.ReleasedAt == null));
    }

    private void ArmBoundary(Action<AllstarrDbContext> update) => placementBoundary = () =>
    {
        using var db = new AllstarrDbContext(database.Options);
        update(db);
        db.SaveChanges();
    };

    private RecordingKeptFiles Service() => new(factory, artifacts, services.GetRequiredService<IServiceScopeFactory>(),
        placement, identities, new SystemPlatformClock());
    private KeptRecordingPublication Request() => new(recordingId, owner, jobId, artifact.Id,
        "deezer", $"heart:{owner:N}:{recordingId:N}", new("Fixture", "Artist", Extension: ".flac"))
    { AttemptNumber = 1 };
    private static UserRecord User(Guid id) => new()
    {
        Id = id,
        DisplayName = "Listener",
        Enabled = true,
        BackendType = "jellyfin",
        BackendInstanceId = "fixture",
        BackendPrincipalId = id.ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    private static ProviderActorContext Actor(Guid id) => new(ProviderActorKind.User, id,
        new("jellyfin", "fixture", id.ToString("N")));
    private ProviderExecutionContext SourceContext() => new(Actor(owner), "deezer",
        new(account, "deezer", ProviderAccountScope.Personal, 7, ownerUserId: owner),
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false), ProviderExplicitContentPolicy.Allow,
            true, true, true), "fixture-source", "fixture-source", DateTimeOffset.UtcNow.AddMinutes(5), default);

    public async Task DisposeAsync()
    {
        if (services != null) await services.DisposeAsync();
        if (database != null) await database.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private sealed class BoundaryFiles(IManagedFileOperations inner, Action<string> boundary) : IManagedFileOperations
    {
        public bool TryCreateHardLink(string linkPath, string existingPath) => inner.TryCreateHardLink(linkPath, existingPath);
        public bool TryCreateReflink(string destinationPath, string sourcePath) => inner.TryCreateReflink(destinationPath, sourcePath);
        public Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) => inner.CopyAsync(sourcePath, destinationPath, cancellationToken);
        public void MoveNoReplace(string sourcePath, string destinationPath) => inner.MoveNoReplace(sourcePath, destinationPath);
        public bool TryGetFileIdentity(string path, out ManagedFileSystemIdentity identity)
        {
            var found = inner.TryGetFileIdentity(path, out identity);
            boundary(path);
            return found;
        }
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
