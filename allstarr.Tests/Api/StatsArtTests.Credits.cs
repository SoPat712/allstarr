using allstarr.Core.Intelligence;
using System.Data.Common;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class StatsArtTests
{
    [Fact]
    public async Task ArtistWarmUpSharesCreditsAcrossImagesAndRequestsUntilPlaysChange()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var reads = new CreditReads();
        var factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(reads).Options);
        var user = User();
        await SeedAsync(factory, user, "Song", DateTimeOffset.UtcNow);
        using var reader = new RecapArtworkReader(Gateway().Object,
            new MediaAssetResolver(new TestMemoryApplicationCache(), NullLogger<MediaAssetResolver>.Instance), new Mock<IHttpClientFactory>().Object, factory);
        using var warmer = new RecapArtworkWarmUp(reader, NullLogger<RecapArtworkWarmUp>.Instance);
        warmer.Queue(user, Recap() with { TopSongs = [], TopAlbums = [], TopArtists = [new("First", 1), new("Second", 1), new("Third", 1)] });
        await warmer.StartAsync(default);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (reader.IsNegative(RecapArtworkReader.Identity(user.Id, "Third", null, null, null)) == false)
                await Task.Delay(10, deadline.Token);
            Assert.Equal(1, reads.Sources);
            await reader.ReadAsync(Execution(user), "Fourth", null, null, default);
            Assert.Equal(1, reads.Sources);
            await SeedAsync(factory, user, "Another song", DateTimeOffset.UtcNow, addUser: false);
            await reader.ReadAsync(Execution(user), "Fifth", null, null, default);
            Assert.Equal(2, reads.Sources);
            await using var db = factory.CreateDbContext();
            var play = await db.ListeningEvents.FirstAsync();
            play.State = ListeningEventState.Skipped;
            await db.SaveChangesAsync();
            await reader.ReadAsync(Execution(user), "Sixth", null, null, default);
            Assert.Equal(3, reads.Sources);
        }
        finally { await warmer.StopAsync(default); }
    }

    [Fact]
    public async Task EmptyArtworkIsNotQueuedAgainUntilItsNegativeEntryExpires()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = Gateway();
        gateway.Setup(item => item.SearchAsync(It.IsAny<allstarr.Core.Protocols.ProtocolExecutionContext>(), It.IsAny<string>(), 8, 0, 0, It.IsAny<string?>()))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult());
        using var reader = new RecapArtworkReader(gateway.Object,
            new MediaAssetResolver(new TestMemoryApplicationCache(), NullLogger<MediaAssetResolver>.Instance), new Mock<IHttpClientFactory>().Object, time: time);
        using var warmer = new RecapArtworkWarmUp(reader, NullLogger<RecapArtworkWarmUp>.Instance);
        var user = User();
        Assert.Null(await reader.PrepareAsync(Execution(user), "Artist", "Song", null, default));
        var identity = RecapArtworkReader.Identity(user.Id, "Artist", "Song", null, null);
        Assert.True(reader.IsNegative(identity));
        warmer.Queue(user, Recap() with { TopAlbums = [], TopArtists = [] });
        var queued = (System.Collections.IEnumerable)typeof(RecapArtworkWarmUp).GetField("queued", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(warmer)!;
        Assert.Empty(queued.Cast<object>());
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(reader.IsNegative(identity));
        warmer.Queue(user, Recap() with { TopAlbums = [], TopArtists = [] });
        Assert.Single(queued.Cast<object>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledArtistPreparationStopsBeforeAnotherMetadataStatement(bool budget)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        using var stop = new CancellationTokenSource();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var reads = new CreditReads { Cancel = budget ? () => time.Advance(TimeSpan.FromSeconds(30)) : stop.Cancel };
        var factory = new Factory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(reads).Options);
        var user = User();
        await SeedAsync(factory, user, "Song", DateTimeOffset.UtcNow, cover: true);
        using var reader = new RecapArtworkReader(Gateway().Object,
            new MediaAssetResolver(new TestMemoryApplicationCache(), NullLogger<MediaAssetResolver>.Instance), new Mock<IHttpClientFactory>().Object, factory, time: time);
        if (budget) Assert.Null(await reader.PrepareAsync(Execution(user), "Artist", null, null, stop.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.PrepareAsync(Execution(user), "Artist", null, null, stop.Token));
        await reads.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, reads.Metadata);
        Assert.False(reader.IsNegative(RecapArtworkReader.Identity(user.Id, "Artist", null, null, null)));
    }

    private sealed class CreditReads : DbCommandInterceptor
    {
        private int sources, metadata;
        public int Sources => Volatile.Read(ref sources);
        public int Metadata => Volatile.Read(ref metadata);
        public Action? Cancel { get; init; }
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT DISTINCT", StringComparison.Ordinal) && command.CommandText.Contains("listening_events", StringComparison.Ordinal))
                Interlocked.Increment(ref sources);
            if (command.CommandText.Contains("FROM \"external_metadata_snapshots\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref metadata);
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Cancel != null && command.CommandText.Contains("FROM \"external_metadata_snapshots\"", StringComparison.Ordinal))
            {
                Cancel();
                Cancelled.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }
}
