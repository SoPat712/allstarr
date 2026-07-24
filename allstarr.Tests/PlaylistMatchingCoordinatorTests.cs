using allstarr.Core.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class PlaylistMatchingCoordinatorTests
{
    [Fact]
    public async Task TriggerMatching_ForwardsProviderAndPlaylistProgress()
    {
        var adapter = new ProgressAdapter();
        var coordinator = new PlaylistMatchingCoordinator(
            new[] { adapter },
            NullLogger<PlaylistMatchingCoordinator>.Instance);
        var updates = new List<PlaylistMatchingProgress>();

        await coordinator.TriggerMatchingAsync(
            (update, _) =>
            {
                updates.Add(update);
                return Task.CompletedTask;
            });

        var playlist = Assert.Single(
            updates,
            update => update.Stage == "playlist-started");
        Assert.Equal("spotify", playlist.ProviderId);
        Assert.Equal("Release Radar", playlist.PlaylistName);
        Assert.Contains(updates, update => update.Stage == "provider-completed");
    }

    private sealed class ProgressAdapter : IPlaylistMatchingAdapter
    {
        public string ProviderId => "spotify";
        public bool Enabled => true;
        public IReadOnlyList<PlaylistMatchingSchedule> Schedules => [];

        public Task TriggerRebuildAllAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task TriggerRebuildForPlaylistAsync(string playlistName) =>
            Task.CompletedTask;

        public Task TriggerMatchingAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task TriggerMatchingAsync(
            Func<PlaylistMatchingProgress, CancellationToken, Task>? progress,
            CancellationToken cancellationToken = default)
        {
            if (progress != null)
            {
                await progress(
                    new PlaylistMatchingProgress(
                        "playlist-started",
                        "Matching playlist Release Radar.",
                        0,
                        1,
                        PlaylistName: "Release Radar"),
                    cancellationToken);
            }
        }

        public Task TriggerMatchingForPlaylistAsync(string playlistName) =>
            Task.CompletedTask;

        public Task TriggerScheduledRebuildAsync(
            string playlistName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
