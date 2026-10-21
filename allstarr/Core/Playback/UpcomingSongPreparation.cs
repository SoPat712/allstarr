using allstarr.Core.Capabilities;
using allstarr.Services.Common;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Playback;

public interface IUpcomingTrackPreparation
{
    Task PrepareAsync(ProtocolExecutionContext context, string itemId);
}

public sealed class UpcomingSongPreparation(
    IUpcomingTrackPreparation preparation, IOptions<PlaybackSettings> settings, TimeProvider? timeProvider = null,
    IPlaybackDeliveryActivitySource? activity = null) : BackgroundService
{
    private readonly object gate = new();
    private readonly Dictionary<DeviceKey, DeviceState> devices = [];
    private readonly HashSet<Task> work = [];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private int running;

    public void RememberPlaylist(ProtocolExecutionContext context, IEnumerable<string> items)
    {
        if (Key(context, null) is not { } key) return;
        lock (gate)
        {
            var state = Get(key);
            state.Playlist = items.Take(10000).ToArray();
            state.PlaylistAt = clock.GetUtcNow();
        }
    }

    public void RememberQueue(ProtocolExecutionContext context, string? current, IReadOnlyList<string> next)
    {
        if (Key(context, null) is not { } key) return;
        lock (gate)
        {
            var state = Get(key);
            state.QueueCurrent = current;
            state.HasReportedQueue = true;
            state.QueueNext = next.Take(2).ToArray();
            if (state.Current != current || !state.Next.SequenceEqual(state.QueueNext))
            {
                state.Cancel();
                state.Cancellation = new();
                state.Attempted.Clear();
            }
            state.Next = state.Current == current ? state.QueueNext : [];
            if (state.Context == null) state.PlaylistAt = clock.GetUtcNow();
        }
    }

    public void Observe(ProtocolExecutionContext context, PlaybackTransition transition, string current,
        string? deviceId, string? sessionId, long? positionTicks, IReadOnlyList<string>? next = null, bool paused = false)
    {
        if (Key(context, deviceId) is not { } key) return;
        lock (gate)
        {
            if (transition is PlaybackTransition.Stop or PlaybackTransition.InferredStop || !settings.Value.PrepareUpcomingSongs)
            {
                if (devices.Remove(key, out var stopped)) stopped.Cancel();
                return;
            }
            if (string.IsNullOrWhiteSpace(current) || current.Length > 500) return;
            if (transition is not (PlaybackTransition.Start or PlaybackTransition.Progress or PlaybackTransition.InferredStart)) return;
            var state = Get(key);
            var now = clock.GetUtcNow();
            var samePlayback = state.Current == current && state.Session == sessionId;
            if (!samePlayback) state.HasReportedQueue = false;
            if (next != null) state.HasReportedQueue = true;
            if (next == null && samePlayback && state.HasReportedQueue) next = state.Next;
            if (next == null && state.QueueCurrent == current)
            {
                next = state.QueueNext;
                state.HasReportedQueue = true;
            }
            next ??= now - state.PlaylistAt < TimeSpan.FromMinutes(10)
                ? UpcomingPlaybackQueue.After(state.Playlist, current) : [];
            var upcoming = next.Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 500)
                .Take(2).ToArray();
            var source = activity?.StreamFor(key.Owner, key.Device, current);
            var quality = context.Policy?.AudioQuality == "DataSaver" ? ProviderAudioQuality.DataSaver :
                source?.Matches(context.Protocol.ToString().ToLowerInvariant(), context.BackendInstanceId) == true
                    ? source.EffectiveQuality : ProviderAudioQuality.Any;
            if (state.Current != current || state.Session != sessionId || !state.Next.SequenceEqual(upcoming) ||
                state.Quality != quality || paused)
            {
                state.Cancel();
                state.Cancellation = new();
                state.Attempted.Clear();
                state.StartedAt = now;
            }
            state.Current = current;
            state.Session = sessionId;
            state.Next = upcoming;
            state.Context = context;
            state.Quality = quality;
            state.ObservedAt = now;
            state.PositionTicks = positionTicks;
            state.Paused = paused;
        }
    }

    internal void ProcessPending()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var (key, state) in devices.ToArray())
            {
                if (!settings.Value.PrepareUpcomingSongs ||
                    state.Context != null && now - state.ObservedAt >= TimeSpan.FromMinutes(2) ||
                    state.Context == null && now - state.PlaylistAt >= TimeSpan.FromMinutes(10))
                {
                    state.Cancel();
                    devices.Remove(key);
                    continue;
                }
                var count = Math.Clamp(settings.Value.PrepareUpcomingCount, 1, 2);
                if (state.Limit != 0 && state.Limit != count)
                {
                    state.Cancel();
                    state.Cancellation = new();
                    state.Attempted.Clear();
                }
                state.Limit = count;
                if (state.Context == null || state.Paused || state.Quality == ProviderAudioQuality.DataSaver ||
                    (state.PositionTicks is { } ticks ? ticks < TimeSpan.FromSeconds(10).Ticks :
                        now - state.StartedAt < TimeSpan.FromSeconds(10))) continue;
                foreach (var item in state.Next.Take(count))
                {
                    if (running >= 2) break;
                    if (!state.Attempted.Add(item) || activity?.WasStreamOpened(item) == true) continue;
                    var original = state.Context;
                    var context = new ProtocolExecutionContext(original.Protocol, original.BackendInstanceId,
                        original.VerifiedBackendPrincipalId, original.Principal, original.CorrelationId,
                        now.AddMinutes(3), state.Cancellation.Token,
                        original.Client)
                    { Policy = original.Policy, RequestedItemId = item, IsPreparation = true, PreparationQuality = state.Quality };
                    running++;
                    var task = Task.Run(async () =>
                    {
                        try { await preparation.PrepareAsync(context, item); }
                        catch { /* Optional preparation never changes playback. */ }
                        finally { lock (gate) running--; }
                    });
                    work.Add(task);
                    _ = task.ContinueWith(completed => { lock (gate) work.Remove(completed); },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ProcessPending();
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Task[] pending;
            lock (gate)
            {
                foreach (var state in devices.Values) state.Cancel();
                devices.Clear();
                pending = work.ToArray();
            }
            await Task.WhenAll(pending);
        }
    }

    public override void Dispose()
    {
        lock (gate)
        {
            foreach (var state in devices.Values) state.Cancel();
            devices.Clear();
        }
        base.Dispose();
    }

    private DeviceState Get(DeviceKey key)
    {
        if (devices.TryGetValue(key, out var state)) return state;
        if (devices.Count >= 256)
        {
            var oldest = devices.MinBy(pair => pair.Value.Context == null ? pair.Value.PlaylistAt : pair.Value.ObservedAt);
            oldest.Value.Cancel();
            devices.Remove(oldest.Key);
        }
        return devices[key] = new();
    }

    private static DeviceKey? Key(ProtocolExecutionContext context, string? device) =>
        context.Actor?.EffectiveUserId is { } owner &&
        (device ?? context.Client.DeviceId ?? context.Client.ClientId) is { Length: > 0 and <= 200 } id
            ? new(owner, context.Protocol, context.BackendInstanceId, id) : null;

    private sealed record DeviceKey(Guid Owner, ProtocolKind Protocol, string Backend, string Device);
    private sealed class DeviceState
    {
        public string[] Playlist = [];
        public DateTimeOffset PlaylistAt;
        public string? Current;
        public string? Session;
        public string[] Next = [];
        public bool HasReportedQueue;
        public string? QueueCurrent;
        public string[] QueueNext = [];
        public ProtocolExecutionContext? Context;
        public DateTimeOffset StartedAt;
        public DateTimeOffset ObservedAt;
        public long? PositionTicks;
        public int Limit;
        public bool Paused;
        public ProviderAudioQuality Quality;
        public CancellationTokenSource Cancellation = new();
        public HashSet<string> Attempted = new(StringComparer.Ordinal);
        public void Cancel()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
        }
    }
}

public sealed class UpcomingTrackPreparation(
    IBackendLibraryAccessResolver access, ProviderStreamOpener opener) : IUpcomingTrackPreparation
{
    public async Task PrepareAsync(ProtocolExecutionContext context, string itemId)
    {
        var owner = context.RequireActor().EffectiveUserId!.Value;
        var resolved = await access.ResolveUserAsync(owner, context.CancellationToken);
        var viewer = resolved.Context;
        if (!resolved.Access.Succeeded || viewer?.Actor?.EffectiveUserId != owner || viewer.Protocol != context.Protocol ||
            viewer.BackendInstanceId != context.BackendInstanceId ||
            viewer.VerifiedBackendPrincipalId != context.VerifiedBackendPrincipalId) return;
        var execution = new ProtocolExecutionContext(viewer.Protocol, viewer.BackendInstanceId,
            viewer.VerifiedBackendPrincipalId, viewer.Principal, context.CorrelationId, context.Deadline,
            context.CancellationToken, context.Client)
        { RequestedItemId = itemId, IsPreparation = true, PreparationQuality = context.PreparationQuality };
        await opener.PrepareAsync(execution, itemId);
    }
}
