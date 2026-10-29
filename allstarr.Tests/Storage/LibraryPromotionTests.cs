using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Enrichment;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class LibraryPromotionTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "allstarr-promotion", Guid.NewGuid().ToString("N"));
    private readonly Guid owner = Guid.CreateVersion7();
    private readonly Guid rootId = Guid.CreateVersion7();
    private readonly Guid artifactId = Guid.CreateVersion7();
    private readonly Guid recordingId = Guid.CreateVersion7();
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private DurableJobQueue queue = null!;
    private ServiceProvider services = null!;
    private LibraryPromotionService service = null!;
    private PromotionRoots roots = null!;
    private readonly Mock<IDurableRuntimeSettings> settings = new();
    private readonly Mutator mutator = new();
    private ProtocolExecutionContext Context => new(ProtocolKind.Jellyfin, "backend", "principal",
        new AllstarrPrincipal(owner, "jellyfin", "backend", "principal", "Owner", false), "promotion-test",
        DateTimeOffset.UtcNow.AddMinutes(10), default);
    private PromoteSongRequest Request(string key = "request") => new(recordingId, artifactId, rootId, "music", key);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Promotion:AllowedBase"] = Path.Combine(directory, "library")
        }).Build();
        Directory.CreateDirectory(Path.Combine(directory, "library", "music"));
        var value = JsonSerializer.Serialize(new[] { new PromotionRoot(rootId, "Music", Path.Combine(directory, "library", "music")) });
        settings.Setup(item => item.GetAsync(PromotionRoots.Key, It.IsAny<CancellationToken>())).ReturnsAsync(
            new EffectiveRuntimeSetting(PromotionRoots.Key, RuntimeSettingValueType.String, value, value,
                RuntimeSettingOrigin.Durable, 1, "test", null, null));
        roots = new(settings.Object, factory, configuration);
        var options = new DurableJobOptions();
        queue = new(factory, options, new JobPayloadPolicy(options), new SystemPlatformClock());
        await using var db = factory.CreateDbContext();
        db.Users.Add(new UserRecord
        {
            Id = owner,
            Enabled = true,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = "principal",
            DisplayName = "Owner",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.CanonicalRecordings.Add(new() { Id = recordingId, CreatedByUserId = owner, PublicId = "song", Title = "Song" });
        await db.SaveChangesAsync();
        var sourceJob = (await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("recording.keep", "source", new { }, owner))).JobId;
        var sourceDirectory = Path.Combine(directory, "workspaces", "source");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "song.flac");
        await File.WriteAllTextAsync(sourcePath, "complete original");
        var workspace = new ProviderDownloadWorkspaceEntity
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = "source",
            OwnerUserId = owner,
            DurableJobId = sourceJob,
            ProviderId = "deezer",
            IdempotencyKey = "source"
        };
        db.Set<ProviderDownloadWorkspaceEntity>().Add(workspace);
        db.Set<ProviderDownloadArtifactEntity>().Add(new()
        {
            Id = artifactId,
            WorkspaceRecordId = workspace.Id,
            WorkspaceId = "source",
            OwnerUserId = owner,
            DurableJobId = sourceJob,
            ProviderId = "deezer",
            SourceExternalTrackId = "track",
            ProviderArtifactId = "song.flac",
            RelativePath = "song.flac",
            State = ProviderDownloadArtifactState.Verified,
            VerifiedAt = DateTimeOffset.UtcNow,
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))),
            Length = 17,
            QualityCheckJson = JsonSerializer.Serialize(new AudioQualityCheck(44100, 16, 21000, "standard", .9))
        });
        db.ProviderTrackIdentities.Add(new()
        {
            Id = Guid.CreateVersion7(),
            CanonicalRecordingId = recordingId,
            ProviderId = "deezer",
            ExternalId = "track",
            ExternalIdHash = new string('a', 64),
            ResourceKind = ProviderResourceKind.Track,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "fixture",
            DecisionVersion = 1,
            Scope = ProviderIdentityScope.Catalog
        });
        await db.SaveChangesAsync();
        var collection = new ServiceCollection();
        collection.AddDbContext<AllstarrDbContext>(builder => builder.UseSqlite(database.ConnectionString));
        collection.AddManagedFilePlacement();
        services = collection.BuildServiceProvider();
        var workspaces = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(directory, "workspaces") };
        var resolver = new ProviderDownloadArtifactResolver(new EfProviderDownloadArtifactStore(factory), workspaces);
        var cache = new FileMediaApplicationCache(new FileMediaCacheOptions(Path.Combine(directory, "cache")), new SystemPlatformClock(),
            NullLogger<FileMediaApplicationCache>.Instance);
        service = new(factory, queue, roots, resolver, workspaces, services.GetRequiredService<IServiceScopeFactory>(),
            new InterruptingWriter(new TagLibManagedMetadataWriter(mutator, new PhysicalManagedFileOperations()), mutator), new AudioQualityAnalyzer(cache),
            new TestBackendLibraryAccess(factory, "music"));
    }

    [Fact]
    public async Task Request_IsIdempotentAndRejectsDifferentInputForSameKey()
    {
        var first = await service.StartAsync(Context, Request());
        Assert.Equal(first.Id, (await service.StartAsync(Context, Request())).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(Context, Request() with { RootId = Guid.NewGuid() }));
        await using var db = factory.CreateDbContext();
        Assert.Single(await db.Set<PromotionRunRecord>().ToArrayAsync());
        Assert.Single(await db.Jobs.Where(item => item.Type == LibraryPromotionService.JobType).ToArrayAsync());
    }

    [Fact]
    public async Task Preview_RejectsFragmentAndForeignArtifactWithoutWritingWorkspace()
    {
        await using var db = factory.CreateDbContext();
        var artifact = await db.Set<ProviderDownloadArtifactEntity>().SingleAsync();
        artifact.State = ProviderDownloadArtifactState.Pending;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(Context, Request()));
        artifact.State = ProviderDownloadArtifactState.Verified;
        artifact.OwnerUserId = null;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewAsync(Context, Request()));
        Assert.False(Directory.Exists(Path.Combine(directory, "workspaces", "promotions")));
    }

    [Fact]
    public async Task Roots_RejectEscapesSymlinksAndNonAdministratorWrites()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => roots.SaveAsync(owner,
            [new(rootId, "Music", Path.Combine(directory, "outside"))]));
        await using (var db = factory.CreateDbContext())
        {
            (await db.Users.SingleAsync()).IsAdmin = true;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => roots.SaveAsync(owner,
            [new(rootId, "Music", Path.Combine(directory, "library-other"))]));
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(Path.Combine(directory, "library", "linked"), Path.Combine(directory, "workspaces"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => roots.SaveAsync(owner,
            [new(rootId, "Music", Path.Combine(directory, "library", "linked"))]));
    }

    [Theory]
    [InlineData("lossy-transcode-suspected")]
    [InlineData("upsampled")]
    [InlineData("padded-bit-depth")]
    [InlineData("unknown")]
    public async Task SuspiciousAudio_RequiresRecordedApprovalBeforePlacement(string verdict)
    {
        await using (var db = factory.CreateDbContext())
        {
            (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).QualityCheckJson =
                JsonSerializer.Serialize(new AudioQualityCheck(96000, 24, 18000, verdict, .8));
            await db.SaveChangesAsync();
        }
        var run = await service.StartAsync(Context, Request());
        await ExecuteAsync();
        var review = await service.GetAsync(Context, run.Id);
        Assert.Equal("Needs review", review.Outcome);
        Assert.Null(review.ManagedFileId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApproveAsync(Context, run.Id, ""));
        await service.ApproveAsync(Context, run.Id, "Checked the source");
        await ExecuteAsync();
        var placed = await service.GetAsync(Context, run.Id);
        Assert.Equal("Placed", placed.Outcome);
        Assert.Equal(owner, placed.ApprovedByUserId);
        Assert.Equal("Checked the source", placed.ApprovalReason);
        Assert.Equal(1, mutator.Writes);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(1, await verify.Jobs.CountAsync(item => item.Type == "library.refresh"));
    }

    [Fact]
    public async Task Placement_RefusesExistingFileAndDirectorySymlink()
    {
        var preview = await service.PreviewAsync(Context, Request());
        Assert.Contains("MusicBrainz tags and cover", preview.Steps);
        Assert.EndsWith("00 - Song.flac", preview.DestinationPath);
        Assert.Equal(17, preview.Size);
        Directory.CreateDirectory(Path.GetDirectoryName(preview.DestinationPath)!);
        await File.WriteAllTextAsync(preview.DestinationPath, "unowned");
        var run = await service.StartAsync(Context, Request());
        await ExecuteAsync();
        Assert.Equal("Failed", (await service.GetAsync(Context, run.Id)).Outcome);
        Assert.Equal("unowned", await File.ReadAllTextAsync(preview.DestinationPath));
        if (OperatingSystem.IsWindows()) return;
        File.Delete(preview.DestinationPath);
        var artist = Directory.GetParent(Path.GetDirectoryName(preview.DestinationPath)!)!.FullName;
        Directory.Delete(artist, true);
        Directory.CreateSymbolicLink(artist, Path.Combine(directory, "workspaces"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewAsync(Context, Request("linked")));
    }

    [Fact]
    public async Task Cancel_CleansOnlyPrivateWorkspaceAndCannotPublishLater()
    {
        await using (var db = factory.CreateDbContext())
        {
            (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).QualityCheckJson = null;
            await db.SaveChangesAsync();
        }
        var run = await service.StartAsync(Context, Request());
        await ExecuteAsync();
        Assert.Equal("Needs review", (await service.GetAsync(Context, run.Id)).Outcome);
        await service.CancelAsync(Context, run.Id);
        Assert.Equal("Cancelled", (await service.GetAsync(Context, run.Id)).Outcome);
        Assert.False(Directory.Exists(Path.Combine(directory, "workspaces", "promotions", run.Id.ToString("N"))));
        Assert.True(File.Exists(Path.Combine(directory, "workspaces", "source", "song.flac")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(Context, run.Id, "Too late"));
    }

    [Fact]
    public async Task Approval_RejectsChangedWorkingBytesAndForeignListener()
    {
        await using (var db = factory.CreateDbContext())
        {
            (await db.Set<ProviderDownloadArtifactEntity>().SingleAsync()).QualityCheckJson = null;
            db.Users.Add(new()
            {
                Id = Guid.NewGuid(),
                Enabled = true,
                BackendType = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = "other",
                DisplayName = "Other"
            });
            await db.SaveChangesAsync();
        }
        var run = await service.StartAsync(Context, Request());
        await ExecuteAsync();
        await using (var db = factory.CreateDbContext())
        {
            var other = await db.Users.SingleAsync(item => item.Id != owner);
            var foreign = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "other",
                new AllstarrPrincipal(other.Id, "jellyfin", "backend", "other", "Other", false), "test",
                DateTimeOffset.UtcNow.AddMinutes(1), default);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ApproveAsync(foreign, run.Id, "Reviewed"));
        }
        await File.AppendAllTextAsync(Path.Combine(directory, "workspaces", "promotions", run.Id.ToString("N"), "song.flac"), "changed");
        await service.ApproveAsync(Context, run.Id, "Reviewed");
        await ExecuteAsync();
        Assert.Equal("Failed", (await service.GetAsync(Context, run.Id)).Outcome);
        await using var verify = factory.CreateDbContext();
        Assert.Empty(await verify.ManagedFiles.ToArrayAsync());
    }

    [Fact]
    public async Task Restart_AfterPlacementDoesNotRepeatTagsOrCreateReferences()
    {
        var run = await service.StartAsync(Context, Request());
        await ExecuteAsync();
        await using (var db = factory.CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == run.JobId);
            job.State = DurableJobState.Running;
            await db.SaveChangesAsync();
        }
        await queue.RecoverInterruptedAsync();
        await ExecuteAsync();
        Assert.Equal("Placed", (await service.GetAsync(Context, run.Id)).Outcome);
        Assert.Equal(1, mutator.Writes);
        await using var verify = factory.CreateDbContext();
        Assert.Single(await verify.ManagedFileReferences.ToArrayAsync());
        Assert.Single(await verify.Jobs.Where(item => item.Type == "library.refresh").ToArrayAsync());
    }

    [Fact]
    public async Task Cancel_AfterPlacementRetainsPlacedStatusAndLibraryFile()
    {
        var run = await service.StartAsync(Context, Request());
        await ExecuteAsync();
        var result = await service.CancelAsync(Context, run.Id);
        Assert.Equal("Placed", result.Outcome);
        await using var db = factory.CreateDbContext();
        Assert.True(File.Exists((await db.ManagedFiles.SingleAsync()).CanonicalPath));
        Assert.Null((await db.ManagedFileReferences.SingleAsync()).ReleasedAt);
    }

    [Fact]
    public async Task Cancel_RefusesWorkspaceSymlinkWithoutDeletingSource()
    {
        if (OperatingSystem.IsWindows()) return;
        var run = await service.StartAsync(Context, Request());
        var privateRoot = Path.Combine(directory, "workspaces", "promotions");
        Directory.CreateDirectory(privateRoot);
        Directory.CreateSymbolicLink(Path.Combine(privateRoot, run.Id.ToString("N")), Path.Combine(directory, "workspaces", "source"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CancelAsync(Context, run.Id));
        Assert.Equal("complete original", await File.ReadAllTextAsync(Path.Combine(directory, "workspaces", "source", "song.flac")));
    }

    [Fact]
    public async Task Preview_SanitizesMetadataBeforeBuildingDestination()
    {
        await using (var db = factory.CreateDbContext())
        {
            (await db.CanonicalRecordings.SingleAsync()).Title = "../../outside";
            await db.SaveChangesAsync();
        }
        var preview = await service.PreviewAsync(Context, Request());
        Assert.StartsWith(Path.Combine(directory, "library", "music") + Path.DirectorySeparatorChar, preview.DestinationPath);
        Assert.EndsWith("00 - .._.._outside.flac", preview.DestinationPath);
    }

    [Fact]
    public async Task Tags_UsePersistedRecordingIdentityAndClearUnsupportedReleaseIdentity()
    {
        var recordingMbid = Guid.NewGuid().ToString("D");
        await using (var db = factory.CreateDbContext())
        {
            db.Set<RecordingIdentifierRecord>().Add(new()
            {
                Id = Guid.NewGuid(),
                RecordingId = recordingId,
                Kind = RecordingIdentifierKinds.MusicBrainz,
                Value = recordingMbid,
                Source = "verified"
            });
            await db.SaveChangesAsync();
        }
        await service.StartAsync(Context, Request());
        await ExecuteAsync();
        Assert.Equal(recordingMbid, mutator.Tags!["musicbrainz_recordingid"]);
        Assert.Equal("", mutator.Tags["musicbrainz_releaseid"]);
    }

    [Fact]
    public void TagWriter_EmbedsExistingCoverAndClearsUnsupportedIdentity()
    {
        var tag = new Mock<TagLib.Tag>();
        tag.SetupAllProperties();
        tag.Object.MusicBrainzReleaseId = Guid.NewGuid().ToString("D");
        var cover = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a2ioAAAAASUVORK5CYII=");
        TagLibManagedTagFileMutator.ApplyTags(tag.Object, new Dictionary<string, string>
        { ["cover"] = Convert.ToBase64String(cover), ["musicbrainz_releaseid"] = "" });
        Assert.Equal("", tag.Object.MusicBrainzReleaseId);
        Assert.Equal(cover, Assert.Single(tag.Object.Pictures).Data.Data);
        Assert.Equal(TagLib.PictureType.FrontCover, tag.Object.Pictures[0].Type);
    }

    [Fact]
    public async Task Restart_ResumesAfterTagSwapAndKeepsPublicationIndependentOfHearts()
    {
        var run = await service.StartAsync(Context, Request());
        mutator.Interrupt = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => ExecuteAsync());
        await queue.RecoverInterruptedAsync();
        await ExecuteAsync();
        var placed = await service.GetAsync(Context, run.Id);
        Assert.Equal("Placed", placed.Outcome);
        Assert.Equal(1, mutator.Writes);
        await using var db = factory.CreateDbContext();
        var file = await db.ManagedFiles.SingleAsync(item => item.Id == placed.ManagedFileId);
        Assert.NotEqual(ManagedFilePlacementMethod.HardLink, file.PlacementMethod);
        var reference = await db.ManagedFileReferences.SingleAsync(item => item.ManagedFileId == file.Id);
        Assert.StartsWith("promotion:", reference.ReferenceKey);
        Assert.Equal(owner, file.OwnerUserId);
        Assert.Equal(run.JobId, file.SourceJobId);
        Assert.Equal(owner, reference.OwnerUserId);
        Assert.Equal(ManagedFileScopeKey.Create(owner, rootId), file.ScopeKey);
        Assert.Empty(await db.Set<RecordingKeptRouteEntity>().Where(item => item.ManagedFileId == file.Id).ToArrayAsync());
        Assert.Equal("complete original", await File.ReadAllTextAsync(Path.Combine(directory, "workspaces", "source", "song.flac")));
        Assert.True(File.Exists(file.CanonicalPath));
    }

    private async Task ExecuteAsync()
    {
        var claim = await queue.ClaimNextAsync("promotion-test", [LibraryPromotionService.JobType]);
        Assert.NotNull(claim);
        var completion = await service.ExecuteAsync(new(claim, services), default);
        await queue.CompleteAsync(claim, completion);
    }

    public async Task DisposeAsync()
    {
        await services.DisposeAsync();
        await database.DisposeAsync();
        Directory.Delete(directory, true);
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class Mutator : IManagedTagFileMutator
    {
        public int Writes;
        public bool Interrupt;
        public IReadOnlyDictionary<string, string>? Tags;
        public void Apply(string path, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
        {
            Writes++;
            Tags = tags;
            File.AppendAllText(path, " tagged");
        }
        public bool Matches(string path, IReadOnlyDictionary<string, string> tags)
        {
            return File.ReadAllText(path).EndsWith(" tagged", StringComparison.Ordinal);
        }
    }

    private sealed class InterruptingWriter(IManagedMetadataWriter inner, Mutator mutator) : IManagedMetadataWriter
    {
        public async Task<ManagedMetadataWriteResult> WriteAsync(ManagedMetadataArtifact artifact,
            IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
        {
            var result = await inner.WriteAsync(artifact, tags, cancellationToken);
            if (mutator.Interrupt)
            {
                mutator.Interrupt = false;
                if (result.Lease != null) await result.Lease.DisposeAsync();
                throw new OperationCanceledException();
            }
            return result;
        }
    }
}
