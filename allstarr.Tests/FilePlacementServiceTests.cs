using allstarr.Core.ManagedFiles;

namespace allstarr.Tests;

public sealed class FilePlacementServiceTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"allstarr-placement-{Guid.NewGuid():N}");

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
    public async Task PlaceAsync_UsesHardLinkOnlyForImmutableManagedSourceAndFallsBackAcrossVolumes()
    {
        var source = CreateSource("source/song.flac", "audio");
        var operations = new RecordingOperations(hardLinkResult: false, reflinkResult: false);
        var service = new FilePlacementService(new MemoryOwnershipStore(), operations);

        var result = await service.PlaceAsync(Request(source, Path.Combine(testRoot, "managed"), true));

        Assert.Equal(1, operations.HardLinkCalls);
        Assert.Equal(1, operations.ReflinkCalls);
        Assert.Equal(1, operations.CopyCalls);
        Assert.Equal(ManagedFilePlacementMethod.Copy, result.File.PlacementMethod);
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task PlaceAsync_RepeatedRequestReusesOwnedContentAndIncrementsReference()
    {
        var source = CreateSource("source/song.flac", "same-audio");
        var store = new MemoryOwnershipStore();
        var operations = new RecordingOperations(false, false);
        var service = new FilePlacementService(store, operations);

        var first = await service.PlaceAsync(Request(source, Path.Combine(testRoot, "managed"), false));
        var second = await service.PlaceAsync(Request(source, Path.Combine(testRoot, "managed"), false));

        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.File.Id, second.File.Id);
        Assert.Equal(2, second.File.ReferenceCount);
        Assert.Equal(1, operations.CopyCalls);
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
            "owned-scope", 2, true, DateTimeOffset.UtcNow);
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
            "owned-scope", 1, true, DateTimeOffset.UtcNow);
        var store = new MemoryRemovalStore(record);

        await new ManagedFileRemovalService(store).RemoveAsync(record.Id, "owned-scope", true);

        Assert.False(File.Exists(path));
        Assert.True(store.Removed);
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
        public bool FailAdd { get; init; }

        public Task<ManagedFileRecord?> FindByPathAsync(string canonicalPath, CancellationToken cancellationToken) =>
            Task.FromResult(records.Values.SingleOrDefault(item => item.CanonicalPath == canonicalPath));

        public Task<ManagedFileRecord?> FindCompatibleAsync(Guid rootId, string contentSha256, string scopeKey, CancellationToken cancellationToken) =>
            Task.FromResult(records.Values.SingleOrDefault(item => item.RootId == rootId && item.ContentSha256 == contentSha256 && item.ScopeKey == scopeKey));

        public Task<ManagedFileRecord> AddAsync(ManagedFileRecord record, CancellationToken cancellationToken)
        {
            if (FailAdd) throw new InvalidOperationException("simulated ownership failure");
            records.Add(record.Id, record);
            return Task.FromResult(record);
        }

        public Task<ManagedFileRecord> AddReferenceAsync(Guid id, CancellationToken cancellationToken)
        {
            var record = records[id] with { ReferenceCount = records[id].ReferenceCount + 1 };
            records[id] = record;
            return Task.FromResult(record);
        }
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
