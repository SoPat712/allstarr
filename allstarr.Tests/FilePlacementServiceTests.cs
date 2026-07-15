using allstarr.Core.ManagedFiles;
using allstarr.Core.Enrichment;
using System.Security.Cryptography;
using System.Text;

namespace allstarr.Tests;

public sealed class FilePlacementServiceTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"allstarr-placement-{Guid.NewGuid():N}");

    [Fact]
    public void ManagedScopeKey_IsStablePerOwnerRootAndLibraryButNotPerAction()
    {
        var tenant = Guid.CreateVersion7();
        var owner = Guid.CreateVersion7();
        var root = Guid.CreateVersion7();

        var first = ManagedFileScopeKey.Create(tenant, owner, root, "music");
        var retry = ManagedFileScopeKey.Create(tenant, owner, root, " music ");

        Assert.Equal(first, retry);
        Assert.NotEqual(first, ManagedFileScopeKey.Create(tenant, owner, root, "audiobooks"));
        Assert.NotEqual(first, ManagedFileScopeKey.Create(tenant, owner, Guid.CreateVersion7(), "music"));
    }

    [Fact]
    public async Task PlaceAsync_RendersSafeTemplateAndCopiesUnownedSource()
    {
        var source = CreateSource("source/song.flac", "audio-payload");
        var targetRoot = Path.Combine(testRoot, "managed");
        var store = new MemoryOwnershipStore();
        var operations = new RecordingOperations(hardLinkResult: true, reflinkResult: false);
        var service = new FilePlacementService(store, operations);

        var result = await service.PlaceAsync(Request(source, targetRoot, sourceManaged: false));

        Assert.Equal(ManagedFilePlacementMethod.Copy, result.File.PlacementMethod);
        Assert.Equal(Path.Combine(targetRoot, "Artist", "Album", "03 - A_B.flac"), result.File.CanonicalPath);
        Assert.Equal("audio-payload", await File.ReadAllTextAsync(result.File.CanonicalPath));
        Assert.False(result.Reused);
        Assert.Equal(1, operations.CopyCalls);
        Assert.Equal(0, operations.HardLinkCalls);
        Assert.True(result.File.IsManaged);
    }

    [Theory]
    [InlineData("../{artist}/{title}")]
    [InlineData("/{artist}/{title}")]
    [InlineData("{artist}/../../{title}")]
    [InlineData("{unknown}/{title}")]
    public async Task PlaceAsync_RejectsUnsafeTemplates(string template)
    {
        var source = CreateSource("source/song.flac", "untouched");
        var service = new FilePlacementService(new MemoryOwnershipStore(), new RecordingOperations(false, false));
        var request = Request(source, Path.Combine(testRoot, "managed"), false) with { PathTemplate = template };

        await Assert.ThrowsAnyAsync<Exception>(() => service.PlaceAsync(request));

        Assert.Equal("untouched", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task PlaceAsync_RejectsSymlinkEscapeAndLeavesSourceUntouched()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = CreateSource("source/song.flac", "untouched");
        var targetRoot = Path.Combine(testRoot, "managed");
        var outside = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(targetRoot, "Artist"), outside);
        var service = new FilePlacementService(new MemoryOwnershipStore(), new RecordingOperations(false, false));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PlaceAsync(Request(source, targetRoot, false)));

        Assert.Empty(Directory.EnumerateFiles(outside));
        Assert.Equal("untouched", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task PlaceAsync_DoesNotTrustAnUnrecordedManagedSourceClaim()
    {
        var source = CreateSource("source/song.flac", "audio");
        var operations = new RecordingOperations(hardLinkResult: false, reflinkResult: false);
        var service = new FilePlacementService(new MemoryOwnershipStore(), operations);

        var result = await service.PlaceAsync(Request(source, Path.Combine(testRoot, "managed"), true));

        Assert.Equal(0, operations.HardLinkCalls);
        Assert.Equal(1, operations.ReflinkCalls);
        Assert.Equal(1, operations.CopyCalls);
        Assert.Equal(ManagedFilePlacementMethod.Copy, result.File.PlacementMethod);
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task PlaceAsync_UsesIndependentCopyUntilDurableImmutabilityLeasesExist()
    {
        var source = CreateSource("source/song.flac", "audio");
        var operations = new RecordingOperations(hardLinkResult: true, reflinkResult: false);
        var store = new MemoryOwnershipStore();
        var service = new FilePlacementService(store, operations);
        var request = Request(source, Path.Combine(testRoot, "managed"), true) with
        {
            DestinationIsImmutable = true,
            ReferenceKey = "destination-reference"
        };
        Assert.True(operations.TryGetFileIdentity(source, out var identity));
        store.Seed(new ManagedFileRecord(Guid.NewGuid(), Guid.NewGuid(), source,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("audio"))).ToLowerInvariant(), 5,
            ManagedFilePlacementMethod.Copy, request.Root.TenantId, request.Root.OwnerUserId,
            request.Root.LibraryScopeId, Guid.NewGuid(), request.ScopeKey, 1, true, DateTimeOffset.UtcNow)
        {
            TargetRootPath = Path.GetDirectoryName(source)!,
            FileSystemDeviceId = identity.DeviceId,
            FileSystemFileId = identity.FileId,
            FileSystemLinkCount = identity.LinkCount
        });

        var result = await service.PlaceAsync(request);

        Assert.Equal(ManagedFilePlacementMethod.Copy, result.File.PlacementMethod);
        Assert.Equal(0, operations.HardLinkCalls);
        Assert.Equal(1, operations.ReflinkCalls);
        Assert.Equal(1, operations.CopyCalls);
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task PlaceAsync_RejectsSourceThatNoLongerMatchesVerifiedArtifact()
    {
        var source = CreateSource("source/song.flac", "modified-audio");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("verified-audio")))
            .ToLowerInvariant();
        var operations = new RecordingOperations(hardLinkResult: true, reflinkResult: false);
        var service = new FilePlacementService(new MemoryOwnershipStore(), operations);
        var request = Request(source, Path.Combine(testRoot, "managed"), true) with
        {
            SourceIsImmutable = false,
            ExpectedContentSha256 = expected,
            ExpectedLength = Encoding.UTF8.GetByteCount("verified-audio")
        };

        await Assert.ThrowsAsync<IOException>(() => service.PlaceAsync(request));

        Assert.Equal(0, operations.HardLinkCalls);
        Assert.False(Directory.Exists(Path.Combine(testRoot, "managed")));
    }

    [Fact]
    public async Task PlaceAsync_DoesNotHardLinkProviderWritableSource()
    {
        var source = CreateSource("source/song.flac", "verified-audio");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("verified-audio")))
            .ToLowerInvariant();
        var operations = new RecordingOperations(hardLinkResult: true, reflinkResult: false);
        var service = new FilePlacementService(new MemoryOwnershipStore(), operations);
        var request = Request(source, Path.Combine(testRoot, "managed"), true) with
        {
            SourceIsImmutable = false,
            ExpectedContentSha256 = expected,
            ExpectedLength = Encoding.UTF8.GetByteCount("verified-audio")
        };

        var result = await service.PlaceAsync(request);

        Assert.Equal(0, operations.HardLinkCalls);
        Assert.Equal(1, operations.ReflinkCalls);
        Assert.Equal(ManagedFilePlacementMethod.Copy, result.File.PlacementMethod);
    }

    [Fact]
    public async Task PlaceAsync_PhysicalOperationsNeverShareSourceLibraryInode()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = CreateSource("library/song.flac", "original-library-audio");
        var service = new FilePlacementService(new MemoryOwnershipStore(), new PhysicalManagedFileOperations());
        var request = Request(source, Path.Combine(testRoot, "managed"), sourceManaged: true) with
        {
            DestinationIsImmutable = true,
            ReferenceKey = "library-safety"
        };

        var result = await service.PlaceAsync(request);
        await File.WriteAllTextAsync(result.File.CanonicalPath, "changed-managed-audio");

        Assert.NotEqual(ManagedFilePlacementMethod.HardLink, result.File.PlacementMethod);
        Assert.Equal("original-library-audio", await File.ReadAllTextAsync(source));
        Assert.NotNull(result.File.FileSystemDeviceId);
        Assert.NotNull(result.File.FileSystemFileId);
    }

    [Fact]
    public void PhysicalOperations_ReturnStableFilesystemIdentityWhereSupported()
    {
        var path = CreateSource("identity/song.flac", "identity");
        var operations = new PhysicalManagedFileOperations();

        var supported = operations.TryGetFileIdentity(path, out var first);
        var repeated = operations.TryGetFileIdentity(path, out var second);

        if (!supported || !repeated) return;
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal(first.FileId, second.FileId);
        Assert.True(first.LinkCount >= 1);
    }

    [Fact]
    public async Task PhysicalOperations_ReflinkIsIndependentOrLeavesNoPartialDestination()
    {
        var source = CreateSource("reflink/source.flac", "copy-on-write-audio");
        var destination = Path.Combine(testRoot, "reflink", "destination.flac");
        var operations = new PhysicalManagedFileOperations();

        var cloned = operations.TryCreateReflink(destination, source);

        if (!cloned)
        {
            Assert.False(File.Exists(destination));
            return;
        }
        await File.WriteAllTextAsync(destination, "changed-clone");
        Assert.Equal("copy-on-write-audio", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task PlacementEnrichmentReuseAndRemoval_RefreshesIdentityAndNeverTouchesSource()
    {
        var source = CreateSource("source/lifecycle.flac", "source-audio");
        var targetRoot = Path.Combine(testRoot, "managed-lifecycle");
        var store = new MemoryOwnershipStore();
        var operations = new PhysicalManagedFileOperations();
        var placement = new FilePlacementService(store, operations);
        var request = Request(source, targetRoot, false) with { ReferenceKey = "favorite:first" };
        var first = await placement.PlaceAsync(request);
        var writer = new TagLibManagedMetadataWriter(new TextAppendingMutator(), operations);
        var operation = new string('9', 64);
        var write = await writer.WriteAsync(new(first.File.CanonicalPath, first.File.ContentSha256, true, false)
        {
            TargetRootPath = first.File.TargetRootPath,
            FileSystemDeviceId = first.File.FileSystemDeviceId,
            FileSystemFileId = first.File.FileSystemFileId,
            OperationFingerprint = operation
        }, new Dictionary<string, string> { ["title"] = "Tagged" }, default);
        var updated = first.File with
        {
            ContentSha256 = write.ContentSha256,
            Length = write.Length,
            FileSystemDeviceId = write.FileSystemDeviceId,
            FileSystemFileId = write.FileSystemFileId,
            FileSystemLinkCount = write.FileSystemLinkCount
        };
        store.Update(updated);
        await write.Lease!.CommitAsync(default);

        var reused = await placement.PlaceAsync(request with
        {
            SourcePath = updated.CanonicalPath,
            ReferenceKey = "playlist:second"
        });
        var released = await store.ReleaseReferenceAsync(reused.File.Id, "playlist:second", default);
        var removalStore = new MemoryRemovalStore(released);
        await new ManagedFileRemovalService(removalStore, operations)
            .RemoveAsync(released.Id, released.ScopeKey, explicitlyConfirmed: true);

        Assert.True(reused.Reused);
        Assert.NotEqual(first.File.FileSystemFileId, updated.FileSystemFileId);
        Assert.False(File.Exists(updated.CanonicalPath));
        Assert.Equal("source-audio", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task PlaceAsync_RetryReusesStableReferenceWithoutInflatingCount()
    {
        var source = CreateSource("source/song.flac", "same-audio");
        var store = new MemoryOwnershipStore();
        var operations = new RecordingOperations(false, false);
        var service = new FilePlacementService(store, operations);

        var request = Request(source, Path.Combine(testRoot, "managed"), false) with { ReferenceKey = "operation-1" };
        var first = await service.PlaceAsync(request);
        var second = await service.PlaceAsync(request);

        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.File.Id, second.File.Id);
        Assert.Equal(1, second.File.ReferenceCount);
        Assert.Equal(1, operations.CopyCalls);
    }

    [Fact]
    public async Task PlaceAsync_DistinctDurableReferenceIncrementsAndReleaseDecrementsOnce()
    {
        var source = CreateSource("source/song.flac", "same-audio");
        var store = new MemoryOwnershipStore();
        var service = new FilePlacementService(store, new RecordingOperations(false, false));
        var request = Request(source, Path.Combine(testRoot, "managed"), false);
        var first = await service.PlaceAsync(request with { ReferenceKey = "playlist:a" });
        var second = await service.PlaceAsync(request with { ReferenceKey = "playlist:b" });

        var released = await store.ReleaseReferenceAsync(second.File.Id, "playlist:b", default);
        var repeated = await store.ReleaseReferenceAsync(second.File.Id, "playlist:b", default);

        Assert.Equal(2, second.File.ReferenceCount);
        Assert.Equal(1, released.ReferenceCount);
        Assert.Equal(1, repeated.ReferenceCount);
        Assert.Equal(first.File.Id, second.File.Id);
    }

    [Fact]
    public async Task PlaceAsync_RejectsMutatedManagedFileInsteadOfAddingAReference()
    {
        var source = CreateSource("source/song.flac", "same-audio");
        var store = new MemoryOwnershipStore();
        var service = new FilePlacementService(store, new RecordingOperations(false, false));
        var request = Request(source, Path.Combine(testRoot, "managed"), false);
        var first = await service.PlaceAsync(request);
        await File.WriteAllTextAsync(first.File.CanonicalPath, "evil-audio");

        await Assert.ThrowsAsync<IOException>(() => service.PlaceAsync(request));

        Assert.Equal(1, first.File.ReferenceCount);
    }

    [Fact]
    public async Task PlaceAsync_UnrelatedCollisionGetsDeterministicFingerprintSuffix()
    {
        var source = CreateSource("source/song.flac", "new-audio");
        var targetRoot = Path.Combine(testRoot, "managed");
        var occupied = Path.Combine(targetRoot, "Artist", "Album", "03 - A_B.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        await File.WriteAllTextAsync(occupied, "unrelated");
        var service = new FilePlacementService(new MemoryOwnershipStore(), new RecordingOperations(false, false));

        var result = await service.PlaceAsync(Request(source, targetRoot, false));

        Assert.Matches(@"03 - A_B \[[0-9a-f]{12}\]\.flac$", result.File.CanonicalPath);
        Assert.Equal("unrelated", await File.ReadAllTextAsync(occupied));
    }

    [Fact]
    public async Task PlaceAsync_OwnershipFailureRemovesPartialOutputButNeverSource()
    {
        var source = CreateSource("source/song.flac", "source-safe");
        var store = new MemoryOwnershipStore { FailAdd = true };
        var targetRoot = Path.Combine(testRoot, "managed");
        var service = new FilePlacementService(store, new RecordingOperations(false, false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PlaceAsync(Request(source, targetRoot, false)));

        Assert.Equal("source-safe", await File.ReadAllTextAsync(source));
        Assert.Empty(Directory.EnumerateFiles(targetRoot, "*.flac", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(targetRoot, ".allstarr-staging")));
    }

    [Fact]
    public async Task RemoveAsync_RequiresExplicitOwnershipAndSingleReference()
    {
        var path = CreateSource("managed/song.flac", "managed");
        var record = new ManagedFileRecord(Guid.NewGuid(), Guid.NewGuid(), path, new string('a', 64), 7,
            ManagedFilePlacementMethod.Copy, Guid.NewGuid(), Guid.NewGuid(), "library", Guid.NewGuid(),
            "owned-scope", 2, true, DateTimeOffset.UtcNow)
        { TargetRootPath = Path.GetDirectoryName(path)! };
        var store = new MemoryRemovalStore(record);
        var service = new ManagedFileRemovalService(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(record.Id, "owned-scope", false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemoveAsync(record.Id, "other-scope", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(record.Id, "owned-scope", true));
        Assert.True(File.Exists(path));
        Assert.False(store.Removed);
    }

    [Fact]
    public async Task RemoveAsync_DeletesOnlyExplicitlySelectedOwnedOutput()
    {
        var path = CreateSource("managed/song.flac", "managed");
        var record = new ManagedFileRecord(Guid.NewGuid(), Guid.NewGuid(), path, new string('a', 64), 7,
            ManagedFilePlacementMethod.Copy, Guid.NewGuid(), Guid.NewGuid(), "library", Guid.NewGuid(),
            "owned-scope", 1, true, DateTimeOffset.UtcNow)
        { TargetRootPath = Path.GetDirectoryName(path)! };
        var store = new MemoryRemovalStore(record);

        await new ManagedFileRemovalService(store).RemoveAsync(record.Id, "owned-scope", true);

        Assert.False(File.Exists(path));
        Assert.True(store.Removed);
    }

    [Fact]
    public async Task RemoveAsync_RejectsAncestorSymlinkSwapOutsideRecordedRoot()
    {
        if (OperatingSystem.IsWindows()) return;
        var managedRoot = Path.Combine(testRoot, "managed");
        var artistDirectory = Path.Combine(managedRoot, "Artist");
        var original = CreateSource("managed/Artist/song.flac", "managed");
        var outside = CreateSource("outside/song.flac", "outside-safe");
        Directory.Delete(artistDirectory, recursive: true);
        Directory.CreateSymbolicLink(artistDirectory, Path.GetDirectoryName(outside)!);
        var record = new ManagedFileRecord(Guid.NewGuid(), Guid.NewGuid(), original, new string('a', 64), 7,
            ManagedFilePlacementMethod.Copy, Guid.NewGuid(), Guid.NewGuid(), "library", Guid.NewGuid(),
            "owned-scope", 1, true, DateTimeOffset.UtcNow)
        { TargetRootPath = managedRoot };
        var store = new MemoryRemovalStore(record);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new ManagedFileRemovalService(store).RemoveAsync(record.Id, "owned-scope", true));

        Assert.Equal("outside-safe", await File.ReadAllTextAsync(outside));
        Assert.False(store.Removed);
    }

    [Fact]
    public async Task RemoveAsync_RejectsAFileReplacedAfterItsIdentityWasRecorded()
    {
        var path = CreateSource("managed/identity.flac", "managed");
        var operations = new PhysicalManagedFileOperations();
        if (!operations.TryGetFileIdentity(path, out var identity)) return;
        var record = new ManagedFileRecord(Guid.NewGuid(), Guid.NewGuid(), path, new string('a', 64), 7,
            ManagedFilePlacementMethod.Copy, Guid.NewGuid(), Guid.NewGuid(), "library", Guid.NewGuid(),
            "owned-scope", 1, true, DateTimeOffset.UtcNow)
        {
            TargetRootPath = Path.GetDirectoryName(path)!,
            FileSystemDeviceId = identity.DeviceId,
            FileSystemFileId = identity.FileId,
            FileSystemLinkCount = identity.LinkCount
        };
        File.Delete(path);
        await File.WriteAllTextAsync(path, "replacement");
        var store = new MemoryRemovalStore(record);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new ManagedFileRemovalService(store, operations).RemoveAsync(record.Id, "owned-scope", true));

        Assert.Equal("replacement", await File.ReadAllTextAsync(path));
        Assert.False(store.Removed);
    }

    private ManagedFilePlacementRequest Request(string source, string root, bool sourceManaged) => new(
        new(Guid.NewGuid(), Path.GetFullPath(root), Guid.NewGuid(), Guid.NewGuid(), "library-1"), source,
        "{albumArtist}/{album}/{track:00} - {title}",
        new("A/B", "Artist", "Album", Track: 3, Extension: ".flac"), Guid.NewGuid(), "tenant/user/library", sourceManaged);

    private string CreateSource(string relative, string content)
    {
        var path = Path.Combine(testRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
    }

    private sealed class RecordingOperations(bool hardLinkResult, bool reflinkResult) : IManagedFileOperations
    {
        public int HardLinkCalls { get; private set; }
        public int ReflinkCalls { get; private set; }
        public int CopyCalls { get; private set; }

        public bool TryCreateHardLink(string linkPath, string existingPath)
        {
            HardLinkCalls++;
            if (hardLinkResult) File.Copy(existingPath, linkPath);
            return hardLinkResult;
        }

        public bool TryCreateReflink(string destinationPath, string sourcePath)
        {
            ReflinkCalls++;
            if (reflinkResult) File.Copy(sourcePath, destinationPath);
            return reflinkResult;
        }

        public bool TryGetFileIdentity(string path, out ManagedFileSystemIdentity identity)
        {
            identity = new ManagedFileSystemIdentity("test-device", Path.GetFullPath(path), 1);
            return File.Exists(path);
        }

        public async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            CopyCalls++;
            await using var source = File.OpenRead(sourcePath);
            await using var destination = new FileStream(destinationPath, FileMode.CreateNew);
            await source.CopyToAsync(destination, cancellationToken);
        }

        public void MoveNoReplace(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, false);
    }

    private sealed class MemoryOwnershipStore : IManagedFileOwnershipStore
    {
        private readonly Dictionary<Guid, ManagedFileRecord> records = [];
        private readonly Dictionary<(Guid FileId, string Key), bool> references = [];
        public bool FailAdd { get; init; }

        public void Seed(ManagedFileRecord record) => records.Add(record.Id, record);
        public void Update(ManagedFileRecord record) => records[record.Id] = record;

        public Task<ManagedFileRecord?> FindByPathAsync(string canonicalPath, CancellationToken cancellationToken) =>
            Task.FromResult(records.Values.SingleOrDefault(item => item.CanonicalPath == canonicalPath));

        public Task<ManagedFileRecord?> FindCompatibleAsync(Guid rootId, string contentSha256, string scopeKey, CancellationToken cancellationToken) =>
            Task.FromResult(records.Values.SingleOrDefault(item => item.RootId == rootId && item.ContentSha256 == contentSha256 && item.ScopeKey == scopeKey));

        public Task<ManagedFileRecord> AddAsync(ManagedFileRecord record, ManagedFileReference reference, CancellationToken cancellationToken)
        {
            if (FailAdd) throw new InvalidOperationException("simulated ownership failure");
            records.Add(record.Id, record);
            references[(record.Id, reference.ReferenceKey)] = true;
            return Task.FromResult(record);
        }

        public Task<ManagedFileRecord> AddReferenceAsync(Guid id, ManagedFileReference reference, CancellationToken cancellationToken)
        {
            if (references.TryGetValue((id, reference.ReferenceKey), out var active) && active)
                return Task.FromResult(records[id]);
            var record = records[id] with { ReferenceCount = records[id].ReferenceCount + 1 };
            records[id] = record;
            references[(id, reference.ReferenceKey)] = true;
            return Task.FromResult(record);
        }

        public Task<ManagedFileRecord> ReleaseReferenceAsync(Guid id, string referenceKey, CancellationToken cancellationToken)
        {
            if (!references.TryGetValue((id, referenceKey), out var active)) throw new KeyNotFoundException();
            if (!active) return Task.FromResult(records[id]);
            references[(id, referenceKey)] = false;
            records[id] = records[id] with { ReferenceCount = records[id].ReferenceCount - 1 };
            return Task.FromResult(records[id]);
        }
    }

    private sealed class TextAppendingMutator : IManagedTagFileMutator
    {
        public void Apply(string path, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken) =>
            File.AppendAllText(path, "\ntagged");

        public bool Matches(string path, IReadOnlyDictionary<string, string> tags) =>
            File.ReadAllText(path).EndsWith("\ntagged", StringComparison.Ordinal);
    }

    private sealed class MemoryRemovalStore(ManagedFileRecord record) : IManagedFileRemovalStore
    {
        public bool Removed { get; private set; }
        public Task<ManagedFileRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<ManagedFileRecord?>(id == record.Id ? record : null);
        public Task MarkRemovedAsync(Guid id, CancellationToken cancellationToken)
        {
            Removed = true;
            return Task.CompletedTask;
        }
    }
}
