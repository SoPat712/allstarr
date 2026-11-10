using System.Data.Common;
using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task SearchSavedLibraryWarmUpLeavesSubsonicVisibilityInTheRequest()
    {
        var factory = new Mock<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict);
        var execution = new ProtocolExecutionContext(ProtocolKind.Subsonic, "primary", "user-1",
            new(FavoriteViewer, "subsonic", "primary", "user-1", "Viewer", false),
            "saved-warm-up", DateTimeOffset.UtcNow.AddMinutes(5), default);
        await new SavedLibraryReader(factory.Object).WarmSearchAsync(execution);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SearchSavedLibraryJoinsWarmUpAndKeepsIdenticalProjection()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SetSavedSongMetadataAsync(storage);
        var coldReads = new SavedSearchReadGate();
        var coldReader = new SavedLibraryReader(new FavoriteViewFactory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .AddInterceptors(coldReads).Options));
        var baseline = new[] { WarmUpSong() };
        await coldReader.PreferSavedIdsAsync(SavedMembershipContext(), baseline, [], default);
        Assert.Equal(1, coldReads.Reads);
        var gate = new SavedSearchReadGate { Block = true };
        var reader = new SavedLibraryReader(new FavoriteViewFactory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .AddInterceptors(gate).Options));
        using var stop = new CancellationTokenSource();
        var warming = reader.WarmSearchAsync(SavedMembershipContext(), stop.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var songs = new[] { WarmUpSong() };
        var searching = reader.PreferSavedIdsAsync(SavedMembershipContext(), songs, [], default);
        Assert.False(searching.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);
        gate.Release.TrySetResult();
        await searching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, gate.Reads);
        Assert.Equal("ext-deezer-song-saved", songs[0].Id);
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(songs));
        songs = [WarmUpSong()];
        await reader.PreferSavedIdsAsync(SavedMembershipContext(), songs, [], default);
        Assert.Equal(1, gate.Reads);
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(songs));
    }

    [Fact]
    public async Task SearchSavedLibraryWarmUpStillInvalidatesWhenAHeartChanges()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SetSavedSongMetadataAsync(storage);
        var reads = new SavedSearchReadGate();
        var reader = new SavedLibraryReader(new FavoriteViewFactory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .AddInterceptors(reads).Options));
        await reader.WarmSearchAsync(SavedMembershipContext());
        Assert.Equal(1, reads.Reads);
        var songs = new[] { WarmUpSong() };
        await reader.PreferSavedIdsAsync(SavedMembershipContext(), songs, [], default);
        Assert.Equal("ext-deezer-song-saved", songs[0].Id);
        Assert.Equal(1, reads.Reads);

        var options = new DurableJobOptions();
        var clock = new SystemPlatformClock();
        var jobs = new DurableJobQueue(storage, options, new JobPayloadPolicy(options), clock);
        await new FavoriteActionPipeline(storage, jobs, clock).RecordAsync(new(SavedMembershipContext(),
            "ext-deezer-song-saved", FavoriteOperation.Unfavorite, "removed"));
        songs = [WarmUpSong()];
        await reader.PreferSavedIdsAsync(SavedMembershipContext(), songs, [], default);
        Assert.Equal("ext-spotify-song-original", songs[0].Id);
        Assert.Equal(2, reads.Reads);
    }

    private static Song WarmUpSong() => new()
    {
        Id = "ext-spotify-song-original",
        ExternalProvider = "spotify",
        ExternalId = "original",
        Title = "Saved song",
        Artist = "Saved artist",
        Album = "Saved album",
        Duration = 180
    };

    private sealed class SavedSearchReadGate : DbCommandInterceptor
    {
        public bool Block { get; init; }
        public int Reads;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("'playlist-membership'", StringComparison.Ordinal)) return result;
            Interlocked.Increment(ref Reads);
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
