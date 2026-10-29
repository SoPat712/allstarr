using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class ManagedAudioCapacityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedUnplayedFilesEvictBeforeOlderPlayedFilesAcrossRestart(bool played)
    {
        await using var fixture = await AudioFixture.CreateAsync(24);
        var older = await fixture.PublishAsync("older", 8);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var prepared = await fixture.PublishAsync("prepared", 8, preparation: true);
        if (played)
        {
            using var opened = (await fixture.Service.TryOpenAsync(new("fixture", ProviderResourceKind.Track, "prepared"), fixture.Scope, default))!.Response;
        }
        await fixture.Service.DisposeAsync();
        fixture.Restart();
        await fixture.PublishAsync("new", 16);
        Assert.Equal(!played, File.Exists(older.LocalPath));
        Assert.Equal(played, File.Exists(prepared.LocalPath));
    }

    [Fact]
    public async Task PreparationCacheProbePreservesPriorityAndExpiryStillFindsOlderPlayedFiles()
    {
        await using var fixture = await AudioFixture.CreateAsync(24);
        var older = await fixture.PublishAsync("older", 8);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        var prepared = await fixture.PublishAsync("prepared", 8, preparation: true);
        using (var probe = (await fixture.Service.TryOpenAsync(new("fixture", ProviderResourceKind.Track, "prepared"),
                   fixture.Scope, default, preparation: true))!.Response) { }
        await fixture.Service.CleanupExpiredAudioAsync(TimeSpan.FromHours(1));
        Assert.False(File.Exists(older.LocalPath));
        Assert.True(File.Exists(prepared.LocalPath));
        await fixture.PublishAsync("played", 8);
        await fixture.PublishAsync("new", 16);
        Assert.False(File.Exists(prepared.LocalPath));
    }

    [Fact]
    public async Task PlaybackTakingOverPreparationIsPublishedWithoutUnplayedPriority()
    {
        await using var fixture = await AudioFixture.CreateAsync(16);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<Song?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var producer = await fixture.OpenProducerAsync("prepared", 8, preparation: true, metadata: () =>
        {
            entered.TrySetResult();
            return metadata.Task;
        });
        var reading = producer.Content.ReadAsByteArrayAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var playback = (await fixture.Service.TryOpenAsync(new("fixture", ProviderResourceKind.Track, "prepared"),
            fixture.Scope, default, 1))!.Response;
        metadata.TrySetResult(new() { Title = "Prepared", Artist = "Fixture" });
        await reading.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Service.TransferCompletionAsync(new("fixture", ProviderResourceKind.Track, "prepared"), fixture.Scope, 1)
            .WaitAsync(TimeSpan.FromSeconds(10));
        var row = (await fixture.Store.FindAsync(fixture.Scope, "fixture", "prepared"))!;
        using var identity = System.Text.Json.JsonDocument.Parse(row.CacheIdentityJson!);
        Assert.False(identity.RootElement.GetProperty("PreparedUnplayed").GetBoolean());
    }

    [Fact]
    public async Task LruEvictionRemovesOnlyTheUnusedOwnedFileAndItsExactMapping()
    {
        await using var fixture = await AudioFixture.CreateAsync(24);
        var one = await fixture.PublishAsync("one", 8);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var two = await fixture.PublishAsync("two", 8);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        using (var opened = (await fixture.Service.TryOpenAsync(new("fixture", ProviderResourceKind.Track, "one"), fixture.Scope, default))!.Response) { }
        var three = await fixture.PublishAsync("three", 16);
        Assert.True(File.Exists(one.LocalPath));
        Assert.False(File.Exists(two.LocalPath));
        Assert.True(File.Exists(three.LocalPath));
        Assert.NotNull(await fixture.Store.FindAsync(fixture.Scope, "fixture", "one"));
        Assert.Null(await fixture.Store.FindAsync(fixture.Scope, "fixture", "two"));
        Assert.NotNull(three.CacheIdentityJson);
    }

    [Fact]
    public async Task CompletedPublishedReaderPinsFollowRenameAndSurviveALoweredLimit()
    {
        await using var fixture = await AudioFixture.CreateAsync(16);
        using var reading = await fixture.OpenProducerAsync("pinned", 16);
        await reading.Content.ReadAsByteArrayAsync();
        var row = (await fixture.Store.FindAsync(fixture.Scope, "fixture", "pinned"))!;
        fixture.Limit = 8;
        await Assert.ThrowsAsync<LocalCapacityException>(() => fixture.PublishAsync("other", 8));
        Assert.True(File.Exists(row.LocalPath));
        Assert.Equal(16, (await reading.Content.ReadAsByteArrayAsync()).Length);
        reading.Dispose();
        await fixture.PublishAsync("other", 8);
        Assert.False(File.Exists(row.LocalPath));
        Assert.Null(await fixture.Store.FindAsync(fixture.Scope, "fixture", "pinned"));
    }

    [Fact]
    public async Task RetiredUnlinkedOriginalKeepsItsBudgetUntilThePhysicalReaderCloses()
    {
        await using var fixture = await AudioFixture.CreateAsync(16);
        using var reading = await fixture.OpenProducerAsync("original", 16, original: true);
        await reading.Content.ReadAsByteArrayAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Service.TransferCompletionAsync(new("fixture", ProviderResourceKind.Track, "original"), fixture.Scope, 1, true)
            .WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<LocalCapacityException>(() => fixture.PublishAsync("other", 8));
        var reader = await reading.Content.ReadAsStreamAsync();
        reader.Position = 0;
        Assert.Equal(0, reader.ReadByte());
        reading.Dispose();
        await fixture.PublishAsync("other", 8);
        Assert.Null(await fixture.Store.FindAsync(fixture.Scope, "fixture", "original"));
    }

    [Fact]
    public async Task LegacyUnknownSymlinkAndReplacedFilesAreProtectedAndStillCountAgainstAdmission()
    {
        await using var fixture = await AudioFixture.CreateAsync(16);
        var owned = await fixture.PublishAsync("owned", 8);
        File.Delete(owned.LocalPath);
        await File.WriteAllBytesAsync(owned.LocalPath, new byte[8]);
        var unknown = Path.Combine(fixture.Root, "cache", "unknown.audio");
        await File.WriteAllBytesAsync(unknown, new byte[8]);
        var outside = Path.Combine(fixture.Root, "backend.flac");
        await File.WriteAllBytesAsync(outside, new byte[64]);
        var link = Path.Combine(fixture.Root, "cache", "library-link");
        File.CreateSymbolicLink(link, outside);
        var busy = await Assert.ThrowsAsync<LocalCapacityException>(() => fixture.PublishAsync("other", 8));
        Assert.Contains("Increase", busy.SafeMessage);
        Assert.True(File.Exists(owned.LocalPath));
        Assert.True(File.Exists(unknown));
        Assert.Equal(64, new FileInfo(outside).Length);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.NotNull(await fixture.Store.FindAsync(fixture.Scope, "fixture", "owned"));
    }

    [Fact]
    public async Task RestartUsesPersistedOwnershipButNeverAdoptsAnUnmarkedLegacyMapping()
    {
        await using var fixture = await AudioFixture.CreateAsync(16);
        var owned = await fixture.PublishAsync("owned", 8);
        var legacy = Path.Combine(fixture.Root, "cache", "legacy.flac");
        await File.WriteAllBytesAsync(legacy, new byte[8]);
        await fixture.Local.RegisterDownloadedSongAsync(fixture.Scope, new Song { ExternalProvider = "fixture", ExternalId = "legacy" }, legacy);
        await fixture.Service.DisposeAsync();
        fixture.Restart();
        await fixture.PublishAsync("other", 8);
        Assert.False(File.Exists(owned.LocalPath));
        Assert.True(File.Exists(legacy));
        Assert.Null((await fixture.Store.FindAsync(fixture.Scope, "fixture", "legacy"))!.CacheIdentityJson);
        using var readable = (await fixture.Service.TryOpenAsync(new("fixture", ProviderResourceKind.Track, "legacy"), fixture.Scope, default))!.Response;
        Assert.Equal(new byte[8], await readable.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PublicationCanOverlapIndependentReaderOpensAndClosesWithoutLosingPins()
    {
        await using var fixture = await AudioFixture.CreateAsync(16);
        var metadataEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMetadata = new TaskCompletionSource<Song?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var original = await fixture.OpenProducerAsync("overlap", 16, metadata: () =>
        {
            metadataEntered.TrySetResult();
            return releaseMetadata.Task;
        });
        var initial = (await original.Content.ReadAsStreamAsync()).ReadAsync(new byte[1]).AsTask();
        await metadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var track = new ProviderExternalResourceId("fixture", ProviderResourceKind.Track, "overlap");
        using var retained = (await fixture.Service.TryOpenAsync(track, fixture.Scope, default, 1))!.Response;
        var overlapping = Task.Run(async () =>
        {
            for (var i = 0; i < 32; i++)
            {
                using var opened = (await fixture.Service.TryOpenAsync(track, fixture.Scope, default, 1))!.Response;
                Assert.Equal(0, (await opened.Content.ReadAsStreamAsync()).ReadByte());
            }
        });
        await initial.WaitAsync(TimeSpan.FromSeconds(10));
        original.Dispose();
        releaseMetadata.TrySetResult(new() { Title = "Overlap", Artist = "Fixture" });
        await overlapping.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Service.TransferCompletionAsync(track, fixture.Scope, 1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(await fixture.Store.FindAsync(fixture.Scope, "fixture", "overlap"));
        await Assert.ThrowsAsync<LocalCapacityException>(() => fixture.PublishAsync("other", 8));
        Assert.Equal(16, (await retained.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task RestartCountsAndPreservesOriginalCrashOrphansInPermanentMode()
    {
        await using var fixture = await AudioFixture.CreateAsync(16);
        var root = fixture.Service.OriginalAudioRoot;
        CreatePrivateDirectory(root);
        var orphan = Path.Combine(root, "orphan.partial");
        await File.WriteAllBytesAsync(orphan, new byte[12]);
        await fixture.Service.DisposeAsync();
        fixture.Restart(mode: StorageMode.Permanent);
        var busy = await Assert.ThrowsAsync<LocalCapacityException>(() => fixture.OpenProducerAsync("other", 8, original: true));
        Assert.Contains("Increase", busy.SafeMessage);
        Assert.Equal(new byte[12], await File.ReadAllBytesAsync(orphan));
        fixture.Limit = 32;
        using var original = await fixture.OpenProducerAsync("other", 8, original: true);
        Assert.Equal(8, (await original.Content.ReadAsByteArrayAsync()).Length);
        Assert.Null(await fixture.Store.FindAsync(fixture.Scope, "fixture", "other"));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "cache")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalTempRootsRejectSymlinksAndReplacementsBeforeOpening(bool replace)
    {
        await using var fixture = await AudioFixture.CreateAsync(32);
        var root = fixture.Service.OriginalAudioRoot;
        var outside = Path.Combine(fixture.Root, "backend");
        Directory.CreateDirectory(outside);
        if (replace)
        {
            using var original = await fixture.OpenProducerAsync("one", 8, original: true);
            await original.Content.ReadAsByteArrayAsync();
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
            original.Dispose();
            await fixture.Service.TransferCompletionAsync(new("fixture", ProviderResourceKind.Track, "one"), fixture.Scope, 1, true);
            Directory.Move(root, root + ".previous");
            CreatePrivateDirectory(root);
        }
        else Directory.CreateSymbolicLink(root, outside);
        await Assert.ThrowsAsync<LocalCapacityException>(() => fixture.OpenProducerAsync("other", 8, original: true));
        Assert.Empty(Directory.GetFiles(outside));
        if (replace) Directory.Delete(root + ".previous", true);
    }

    [Fact]
    public async Task ExpiryUsesOwnedLastUseAndPreservesPinnedLegacyAndUnknownFiles()
    {
        await using var fixture = await AudioFixture.CreateAsync(64);
        var unused = await fixture.PublishAsync("unused", 8);
        var pinned = await fixture.PublishAsync("pinned", 8);
        using var reading = (await fixture.Service.TryOpenAsync(new("fixture", ProviderResourceKind.Track, "pinned"), fixture.Scope, default))!.Response;
        var legacy = Path.Combine(fixture.Root, "cache", "legacy.mp3");
        var unknown = Path.Combine(fixture.Root, "cache", "unknown.mp3");
        await File.WriteAllBytesAsync(legacy, new byte[8]);
        await File.WriteAllBytesAsync(unknown, new byte[8]);
        await fixture.Local.RegisterDownloadedSongAsync(fixture.Scope, new Song { ExternalProvider = "fixture", ExternalId = "legacy" }, legacy);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        await fixture.Service.CleanupExpiredAudioAsync(TimeSpan.FromHours(1));
        Assert.False(File.Exists(unused.LocalPath));
        Assert.Null(await fixture.Store.FindAsync(fixture.Scope, "fixture", "unused"));
        Assert.True(File.Exists(pinned.LocalPath));
        Assert.True(File.Exists(legacy));
        Assert.True(File.Exists(unknown));
        Assert.Equal(8, (await reading.Content.ReadAsByteArrayAsync()).Length);
        reading.Dispose();
        await fixture.Service.CleanupExpiredAudioAsync(TimeSpan.FromHours(1));
        Assert.False(File.Exists(pinned.LocalPath));
        Assert.Null(await fixture.Store.FindAsync(fixture.Scope, "fixture", "pinned"));
        Assert.NotNull(await fixture.Store.FindAsync(fixture.Scope, "fixture", "legacy"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledSourcesReleaseTheirReservation(bool cancel)
    {
        await using var fixture = await AudioFixture.CreateAsync(16);
        var source = new FailingSource(cancel);
        using (var reading = await fixture.OpenProducerAsync("broken", 16, content: new StreamContent(source)))
            await Assert.ThrowsAnyAsync<Exception>(() => reading.Content.ReadAsByteArrayAsync());
        await fixture.Service.TransferCompletionAsync(new("fixture", ProviderResourceKind.Track, "broken"), fixture.Scope, 1);
        await fixture.PublishAsync("other", 16);
        Assert.Null(await fixture.Store.FindAsync(fixture.Scope, "fixture", "broken"));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "cache", ".incoming")));
    }

    [Fact]
    public async Task UnknownLengthGrowthExhaustionIsLocalAndReleasesAllTemporaryBytes()
    {
        await using var fixture = await AudioFixture.CreateAsync(8);
        using (var reading = await fixture.OpenProducerAsync("unknown", 16,
                   content: new StreamContent(new SequentialSource(new byte[16]))))
            await Assert.ThrowsAsync<LocalCapacityException>(() => reading.Content.ReadAsByteArrayAsync());
        await fixture.Service.TransferCompletionAsync(new("fixture", ProviderResourceKind.Track, "unknown"), fixture.Scope, 1);
        await fixture.PublishAsync("other", 8);
        Assert.Null(await fixture.Store.FindAsync(fixture.Scope, "fixture", "unknown"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementOrNewHardLinkBetweenHashVerificationAndRemovalIsPreserved(bool hardLink)
    {
        await using var fixture = await AudioFixture.CreateAsync(8);
        var owned = await fixture.PublishAsync("owned", 8);
        var calls = 0;
        var physical = new PhysicalManagedFileOperations();
        var operations = new Mock<IManagedFileOperations>();
        operations.Setup(item => item.TryGetFileIdentity(It.IsAny<string>(), out It.Ref<ManagedFileSystemIdentity>.IsAny))
            .Returns((string path, out ManagedFileSystemIdentity identity) =>
            {
                if (path == owned.LocalPath && ++calls == 2)
                {
                    if (hardLink) Assert.True(physical.TryCreateHardLink(path + ".kept", path));
                    else
                    {
                        File.Move(path, path + ".saved");
                        File.WriteAllBytes(path, new byte[8]);
                    }
                }
                return physical.TryGetFileIdentity(path, out identity);
            });
        operations.Setup(item => item.TryGetFileIdentity(It.IsAny<Stream>(), out It.Ref<ManagedFileSystemIdentity>.IsAny))
            .Returns((Stream stream, out ManagedFileSystemIdentity identity) => physical.TryGetFileIdentity(stream, out identity));
        await fixture.Service.DisposeAsync();
        fixture.Restart(operations.Object);
        await Assert.ThrowsAsync<LocalCapacityException>(() => fixture.PublishAsync("other", 8));
        Assert.Equal(2, calls);
        Assert.True(File.Exists(owned.LocalPath));
        Assert.NotNull(await fixture.Store.FindAsync(fixture.Scope, "fixture", "owned"));
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private class SequentialSource(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class FailingSource(bool cancel) : SequentialSource([])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            ValueTask.FromException<int>(cancel ? new OperationCanceledException() : new IOException("fixture failure"));
    }

    private sealed class AudioFixture : IAsyncDisposable
    {
        private readonly SqliteTestDatabase database;
        private readonly Factory factory;
        private readonly IConfiguration configuration;
        public readonly ManualTimeProvider Clock = new();
        public readonly DownloadedSongMappingScope Scope = new(Guid.NewGuid(), ProviderAudioQuality.Any);
        public readonly EfDownloadedSongMappingStore Store;
        public readonly LocalLibraryService Local;
        public ManagedTrackCacheService Service = null!;
        public readonly string Root;
        public long Limit;
        private AudioFixture(SqliteTestDatabase database, long limit)
        {
            this.database = database;
            factory = new(database.Options);
            Root = Path.Combine(database.StorageOptions.DataDirectory, "audio");
            configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = Root }).Build();
            Limit = limit;
            Store = new(factory);
            var clients = new Mock<IHttpClientFactory>();
            clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
            Local = new(configuration, clients.Object, Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }), Store,
                NullLogger<LocalLibraryService>.Instance);
            Restart();
        }
        public static async Task<AudioFixture> CreateAsync(long limit) => new(await SqliteTestDatabase.CreateAsync(), limit);
        public void Restart(IManagedFileOperations? operations = null, StorageMode mode = StorageMode.Cache) => Service = new(configuration, Options.Create(new SubsonicSettings { StorageMode = mode }), Local,
            NullLogger<ManagedTrackCacheService>.Instance, timeProvider: Clock, mappings: Store, files: operations, audioMaximumBytes: () => Limit,
            audioVerifier: FixtureAudioVerifier.Instance);
        public async Task<HttpResponseMessage> OpenProducerAsync(string track, int bytes, bool original = false, HttpContent? content = null, Func<Task<Song?>>? metadata = null, bool preparation = false)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content ?? new ByteArrayContent(Enumerable.Range(0, bytes).Select(value => (byte)value).ToArray()) };
            var stream = new ProtocolProviderStream(response, new("fixture", new Uri("https://media.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
                false, false, new("audio/mpeg", "mp3", "mp3"), ProviderStreamRetryBehavior.DoNotRetry), "fixture", track,
                Scope.ProviderAccountId, ServingAccountRevision: 1);
            var user = Guid.NewGuid();
            var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener",
                new AllstarrPrincipal(user, "jellyfin", "primary", "listener", "Listener", false), "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default)
            { IsOriginalDownload = original, IsPreparation = preparation };
            try
            {
                await Service.WrapAsync(stream, context, "fixture", track, false,
                    metadata ?? (() => Task.FromResult<Song?>(new() { Title = track, Artist = "Fixture", Album = "Fixture" })), default);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        public async Task<DownloadedSongMappingEntity> PublishAsync(string track, int bytes, bool preparation = false)
        {
            using var response = await OpenProducerAsync(track, bytes, preparation: preparation);
            Assert.Equal(bytes, (await response.Content.ReadAsByteArrayAsync()).Length);
            return (await Store.FindAsync(Scope, "fixture", track))!;
        }
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (new DirectoryInfo(Service.OriginalAudioRoot).LinkTarget != null) Directory.Delete(Service.OriginalAudioRoot);
            else if (Directory.Exists(Service.OriginalAudioRoot)) Directory.Delete(Service.OriginalAudioRoot, true);
            await database.DisposeAsync();
        }
        private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
        {
            public AllstarrDbContext CreateDbContext() => new(options);
            public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken token = default) => Task.FromResult(CreateDbContext());
        }
    }
}
