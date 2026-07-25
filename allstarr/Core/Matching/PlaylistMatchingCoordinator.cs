using System.Text.Json;
using allstarr.Core.Jobs;
using Cronos;

namespace allstarr.Core.Matching;

public interface IPlaylistMatchingCoordinator
{
    Task TriggerRebuildAllAsync(CancellationToken cancellationToken = default);
    Task TriggerRebuildForPlaylistAsync(string playlistName);
    Task TriggerMatchingAsync(CancellationToken cancellationToken = default);
    Task TriggerMatchingAsync(
        Func<PlaylistMatchingProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken = default) =>
        TriggerMatchingAsync(cancellationToken);
    Task TriggerMatchingForPlaylistAsync(string playlistName);
}

public sealed record PlaylistMatchingSchedule(string PlaylistName, string CronExpression);
public sealed record PlaylistMatchingProgress(
    string Stage,
    string Message,
    int Completed,
    int Total,
    string? ProviderId = null,
    string? PlaylistName = null,
    string? Track = null);

public interface IPlaylistMatchingAdapter : IPlaylistMatchingCoordinator
{
    string ProviderId { get; }
    bool Enabled { get; }
    IReadOnlyList<PlaylistMatchingSchedule> Schedules { get; }
    Task TriggerScheduledRebuildAsync(
        string playlistName,
        CancellationToken cancellationToken = default);
}

public sealed class PlaylistMatchingCoordinator(
    IEnumerable<IPlaylistMatchingAdapter> adapters,
    ILogger<PlaylistMatchingCoordinator> logger)
    : BackgroundService, IPlaylistMatchingCoordinator
{
    private readonly IPlaylistMatchingAdapter[] _adapters = adapters.ToArray();

    public Task TriggerRebuildAllAsync(CancellationToken cancellationToken = default) =>
        RunAllAsync(adapter => adapter.TriggerRebuildAllAsync(cancellationToken));

    public Task TriggerRebuildForPlaylistAsync(string playlistName) =>
        RunAllAsync(adapter => adapter.TriggerRebuildForPlaylistAsync(playlistName));

    public Task TriggerMatchingAsync(CancellationToken cancellationToken = default) =>
        TriggerMatchingAsync(null, cancellationToken);

    public async Task TriggerMatchingAsync(
        Func<PlaylistMatchingProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken = default)
    {
        var enabled = _adapters.Where(adapter => adapter.Enabled).ToArray();
        if (progress != null)
        {
            await progress(
                new PlaylistMatchingProgress(
                    "preparing",
                    $"Preparing {enabled.Length} playlist source{(enabled.Length == 1 ? "" : "s")}.",
                    0,
                    enabled.Length),
                cancellationToken);
        }

        var completed = 0;
        await Task.WhenAll(enabled.Select(async adapter =>
        {
            if (progress != null)
            {
                await progress(
                    new PlaylistMatchingProgress(
                        "provider-started",
                        $"Matching playlists from {adapter.ProviderId}.",
                        Volatile.Read(ref completed),
                        enabled.Length,
                        adapter.ProviderId),
                    cancellationToken);
            }

            await adapter.TriggerMatchingAsync(
                progress == null
                    ? null
                    : async (adapterProgress, token) =>
                    {
                        await progress(
                            adapterProgress with
                            {
                                ProviderId = adapterProgress.ProviderId ?? adapter.ProviderId
                            },
                            token);
                    },
                cancellationToken);
            var current = Interlocked.Increment(ref completed);
            if (progress != null)
            {
                await progress(
                    new PlaylistMatchingProgress(
                        "provider-completed",
                        $"Finished matching playlists from {adapter.ProviderId}.",
                        current,
                        enabled.Length,
                        adapter.ProviderId),
                    cancellationToken);
            }
        }));
    }

    public Task TriggerMatchingForPlaylistAsync(string playlistName) =>
        RunAllAsync(adapter => adapter.TriggerMatchingForPlaylistAsync(playlistName));

    private Task RunAllAsync(Func<IPlaylistMatchingAdapter, Task> operation) =>
        Task.WhenAll(_adapters.Where(adapter => adapter.Enabled).Select(operation));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        foreach (var adapter in _adapters.Where(item => item.Enabled))
        {
            try
            {
                await adapter.TriggerMatchingAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Initial playlist matching failed for adapter {ProviderId}",
                    adapter.ProviderId);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var due = new List<(IPlaylistMatchingAdapter Adapter, PlaylistMatchingSchedule Schedule)>();
            foreach (var adapter in _adapters.Where(item => item.Enabled))
            {
                foreach (var schedule in adapter.Schedules)
                {
                    try
                    {
                        var cron = CronExpression.Parse(schedule.CronExpression);
                        var occurrence = cron.GetNextOccurrence(
                            now.AddMinutes(-1),
                            TimeZoneInfo.Utc);
                        if (occurrence.HasValue && occurrence.Value <= now)
                            due.Add((adapter, schedule));
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "Invalid playlist schedule {Schedule} for {ProviderId}/{Playlist}",
                            schedule.CronExpression,
                            adapter.ProviderId,
                            schedule.PlaylistName);
                    }
                }
            }

            foreach (var item in due)
            {
                try
                {
                    await item.Adapter.TriggerScheduledRebuildAsync(
                        item.Schedule.PlaylistName,
                        stoppingToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Scheduled playlist matching failed for {ProviderId}/{Playlist}",
                        item.Adapter.ProviderId,
                        item.Schedule.PlaylistName);
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}

public sealed record PlaylistMatchAllJobPayload(long Generation);

public sealed class PlaylistMatchAllJobHandler(IPlaylistMatchingCoordinator matching)
    : IDurableJobHandler
{
    public string JobType => "playlist.match-all";

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        PlaylistMatchAllJobPayload? payload;
        try
        {
            payload = context.Claim.Payload.Deserialize<PlaylistMatchAllJobPayload>();
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload == null || payload.Generation <= 0)
            return DurableJobCompletion.Failure(
                "playlist_match_payload_invalid",
                "The playlist match request is invalid.");

        await context.ReportProgressAsync(
            new DurableJobProgressUpdate(
                "started",
                "Playlist matching started.",
                0,
                null),
            cancellationToken);
        await matching.TriggerMatchingAsync(
            async (progress, token) =>
            {
                await context.ReportProgressAsync(
                    new DurableJobProgressUpdate(
                        progress.Stage,
                        progress.Message,
                        progress.Completed,
                        progress.Total,
                        progress.ProviderId,
                        progress.PlaylistName,
                        progress.Track),
                    token);
            },
            cancellationToken);
        await context.ReportProgressAsync(
            new DurableJobProgressUpdate(
                "completed",
                "Playlist matching completed.",
                1,
                1),
            cancellationToken);
        return DurableJobCompletion.Success();
    }
}
