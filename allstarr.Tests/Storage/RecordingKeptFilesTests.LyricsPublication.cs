using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Services.Lyrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("publish")]
    [InlineData("favorite")]
    [InlineData("explicit")]
    [InlineData("playlist")]
    public async Task DurableKeptPublicationAndReuseSaveHouseholdLyrics(string operation)
    {
        UsePhysicalKeptLyricsRoot();
        var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
        resolver.Setup(item => item.FindAsync(It.Is<ProtocolExecutionContext>(context => context.Principal!.UserId == owner),
                It.Is<Song>(song => song.CanonicalRecordingId == recordingId && song.Title == "Fixture" && song.Artist == "Artist"),
                It.IsAny<string>(), It.Is<ProtocolLyricsSources>(sources => sources.HouseholdOnly)))
            .ReturnsAsync(new LyricsInfo { SyncedLyrics = "[00:01.234]Saved household words" });
        var kept = WithLyricsSidecars(new KeptLyricsSidecarService(resolver.Object,
            NullLogger<KeptLyricsSidecarService>.Instance));
        Func<Task<KeptRecordingFile?>> execute;
        if (operation == "publish") execute = async () => await kept.PublishAsync(Request());
        else
        {
            await Service().PublishAsync(Request());
            if (operation == "favorite")
            {
                var intent = await SeedFavoriteIntentAsync(owner, CanonicalItemId(), existingJobId: jobId);
                execute = () => kept.RetainFavoriteAsync(new(owner, intent.JobId, intent.AttemptNumber));
            }
            else if (operation == "explicit")
            {
                await SeedKeepCacheAsync();
                var claim = await AcceptKeepAsync();
                execute = () => kept.RetainRequestedKeepAsync(claim);
            }
            else
            {
                var payload = await SeedPlaylistInterestAsync();
                var claim = new DurableJobClaim(jobId, 1, PlaylistTrackRetentionJobHandler.JobTypeName,
                    JsonSerializer.SerializeToElement(payload), owner, null, null,
                    JsonSerializer.SerializeToElement(new { }), "lyrics-playlist");
                execute = () => kept.RetainPlaylistAsync(claim);
            }
        }

        var file = Assert.IsType<KeptRecordingFile>(await execute());
        var sidecar = Path.ChangeExtension(file.Path, ".lrc");
        Assert.True(File.Exists(sidecar));
        Assert.Contains("[00:01.234]Saved household words", await File.ReadAllTextAsync(sidecar), StringComparison.Ordinal);
        var household = await CreateKeptLyricsReader(root).ReadHouseholdAsync(LyricsContext(other),
            new Song { CanonicalRecordingId = recordingId, Title = "Fixture", Artist = "Artist" }, []);
        Assert.Contains("Saved household words", household!.SyncedLyrics, StringComparison.Ordinal);
        Assert.Equal(file.ManagedFileId, (await execute())!.ManagedFileId);
        resolver.Verify(item => item.FindAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(),
            It.IsAny<string>(), It.IsAny<ProtocolLyricsSources>()), Times.Once);
        Assert.Equal(audio, await File.ReadAllBytesAsync(file.Path));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task KeptLyricsProviderLookupDoesNotHoldThePublicationWriter()
    {
        UsePhysicalKeptLyricsRoot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
        resolver.Setup(item => item.FindAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(),
                It.IsAny<string>(), It.Is<ProtocolLyricsSources>(sources => sources.HouseholdOnly)))
            .Returns(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return new LyricsInfo { SyncedLyrics = "[00:00.000]After commit" };
            });
        var publication = WithLyricsSidecars(new KeptLyricsSidecarService(resolver.Object,
            NullLogger<KeptLyricsSidecarService>.Instance)).PublishAsync(Request());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var independent = new AllstarrDbContext(database.Options);
            Assert.Equal(1, await independent.Users.Where(item => item.Id == other)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.DisplayName, "Independent listener"))
                .WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally { release.TrySetResult(); await publication; }
        Assert.Contains("After commit", await File.ReadAllTextAsync(Path.ChangeExtension((await publication).Path, ".lrc")),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task InterruptedLyricsPublicationKeepsTheAudioAndRepairsTheSidecarOnRetry()
    {
        UsePhysicalKeptLyricsRoot();
        using var cancellation = new CancellationTokenSource();
        var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
        resolver.Setup(item => item.FindAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(),
                It.IsAny<string>(), It.Is<ProtocolLyricsSources>(sources => sources.HouseholdOnly)))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<LyricsInfo?>(cancellation.Token);
            });
        var kept = WithLyricsSidecars(new KeptLyricsSidecarService(resolver.Object,
            NullLogger<KeptLyricsSidecarService>.Instance));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kept.PublishAsync(Request(), cancellation.Token));
        var published = Assert.IsType<KeptRecordingFile>(await Service().FindAsync(Actor(other), recordingId));
        Assert.Equal(audio, await File.ReadAllBytesAsync(published.Path));
        Assert.False(File.Exists(Path.ChangeExtension(published.Path, ".lrc")));
        resolver.Setup(item => item.FindAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(),
                It.IsAny<string>(), It.Is<ProtocolLyricsSources>(sources => sources.HouseholdOnly)))
            .ReturnsAsync(new LyricsInfo { SyncedLyrics = "[00:00.000]Repaired on retry" });

        Assert.Equal(published.ManagedFileId, (await kept.PublishAsync(Request())).ManagedFileId);

        Assert.Contains("Repaired on retry", await File.ReadAllTextAsync(Path.ChangeExtension(published.Path, ".lrc")),
            StringComparison.Ordinal);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Single(await db.Set<RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.Single(await db.ManagedFiles.Where(item => item.RemovedAt == null).ToArrayAsync());
        Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
    }

    private RecordingKeptFiles WithLyricsSidecars(IKeptLyricsSidecarService writer)
    {
        var factoryScope = new Mock<IServiceScopeFactory>();
        factoryScope.Setup(item => item.CreateScope()).Returns(() =>
        {
            var inner = services.CreateScope();
            var provider = new Mock<IServiceProvider>();
            provider.Setup(item => item.GetService(It.IsAny<Type>())).Returns((Type type) =>
                type == typeof(IKeptLyricsSidecarService) ? writer : inner.ServiceProvider.GetService(type));
            var scope = new Mock<IServiceScope>();
            scope.SetupGet(item => item.ServiceProvider).Returns(provider.Object);
            scope.Setup(item => item.Dispose()).Callback(inner.Dispose);
            return scope.Object;
        });
        return new(factory, artifacts, factoryScope.Object, placement, identities, new SystemPlatformClock(),
            jobs: queue, libraryAccess: new TestBackendLibraryAccess(factory, "music"));
    }
}
