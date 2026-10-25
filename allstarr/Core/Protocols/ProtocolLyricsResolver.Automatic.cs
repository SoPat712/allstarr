using allstarr.Core.Providers.Lyrics;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolLyricsResolver
{
    private sealed class AutomaticSource(string id, Func<bool, CancellationToken, Task<PreparedLyricsRead?>>? prepare,
        bool needsDiscovery, bool canDiscover, Func<Task<LyricsInfo?>>? local)
    {
        internal string Id => id;
        internal Func<bool, CancellationToken, Task<PreparedLyricsRead?>>? Prepare => prepare;
        internal Func<Task<LyricsInfo?>>? Local => local;
        internal bool NeedsDiscovery { get; set; } = needsDiscovery;
        internal bool CanDiscover => canDiscover;
        internal PreparedLyricsRead? Read { get; set; }
        internal bool WasPrepared { get; set; }
        internal bool WasDiscovered { get; set; }
    }
    private sealed record AutomaticAnswer(int Rank, string Id, LyricsInfo Lyrics, PreparedLyricsRead? Read);
    private sealed class AutomaticLookup(DateTimeOffset deadline, CancellationTokenSource timer, CancellationTokenSource cancellation)
    {
        internal DateTimeOffset Deadline { get; } = deadline;
        internal CancellationToken Token => cancellation.Token;
        internal List<Task<AutomaticAnswer?>> Tasks { get; } = [];
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Cancel() => cancellation.Cancel();
        internal void Dispose() { cancellation.Dispose(); timer.Dispose(); }
    }
    private readonly object _automaticGate = new();
    private readonly HashSet<AutomaticLookup> _automatic = [];
    private readonly CancellationTokenSource _automaticShutdown = CancellationTokenSource.CreateLinkedTokenSource(lifetime?.ApplicationStopping ?? default);
    private TimeProvider LyricsClock => timeProvider ?? TimeProvider.System;
    private AutomaticLookup? EnterAutomatic(ProtocolExecutionContext protocol)
    {
        lock (_automaticGate)
        {
            if (_automatic.Count >= 4 || _automaticShutdown.IsCancellationRequested) return null;
            var now = LyricsClock.GetUtcNow();
            var deadline = protocol.Deadline < now.AddSeconds(15) ? protocol.Deadline : now.AddSeconds(15);
            if (deadline <= now) return null;
            var timer = new CancellationTokenSource(deadline - now, LyricsClock);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, _automaticShutdown.Token, protocol.CancellationToken);
            var lookup = new AutomaticLookup(deadline, timer, cancellation);
            _automatic.Add(lookup);
            return lookup;
        }
    }
    private void DrainAutomatic(AutomaticLookup lookup) => _ = DrainAsync(lookup);
    private async Task DrainAsync(AutomaticLookup lookup)
    {
        try { await Task.WhenAll(lookup.Tasks); }
        catch (Exception) { }
        finally
        {
            lock (_automaticGate) _automatic.Remove(lookup);
            lookup.Dispose();
            lookup.Completion.TrySetResult();
        }
    }
    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken token)
    {
        _automaticShutdown.Cancel();
        AutomaticLookup[] lookups;
        lock (_automaticGate) lookups = _automatic.ToArray();
        await Task.WhenAll(lookups.Select(item => item.Completion.Task)).WaitAsync(token);
    }

    private async Task<ProtocolLyricsCandidate?> RunAutomaticAsync(IProtocolLyricsExecutionGateway gateway,
        IReadOnlyList<AutomaticSource> sources, AutomaticLookup lookup, CancellationToken caller)
    {
        try { return await RunAutomaticCoreAsync(gateway, sources, lookup, caller); }
        catch (OperationCanceledException) when (lookup.Token.IsCancellationRequested && !caller.IsCancellationRequested) { return null; }
        catch { lookup.Cancel(); throw; }
    }
    private async Task<ProtocolLyricsCandidate?> RunAutomaticCoreAsync(IProtocolLyricsExecutionGateway gateway,
        IReadOnlyList<AutomaticSource> sources, AutomaticLookup lookup, CancellationToken caller)
    {
        var started = LyricsClock.GetUtcNow();
        var attempted = new HashSet<int>();
        var discarded = new HashSet<int>();
        Task<AutomaticAnswer?>? fallback = null;
        var initial = 0;
        var ranks = new Dictionary<Task<AutomaticAnswer?>, int>();
        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        for (var rank = 0; rank < sources.Count; rank++)
        {
            lookup.Token.ThrowIfCancellationRequested();
            var source = sources[rank];
            if (source.Local != null)
            {
                var lyrics = await source.Local();
                attempted.Add(rank);
                if (lyrics != null) AddLocal(rank, source.Id, lyrics);
            }
            else if (!source.NeedsDiscovery && initial < 4)
            {
                var read = await PrepareAsync(rank, false);
                if (read?.MaySpeculate == true) { _ = Start(rank); initial++; }
            }
            var answer = await ChooseAsync();
            if (answer != null) return answer;
            if (lookup.Tasks.Any(task => task.IsCompletedSuccessfully && task.Result != null)) break;
            if (initial == 4) break;
        }
        while (true)
        {
            lookup.Token.ThrowIfCancellationRequested();
            var answer = await ChooseAsync();
            if (answer != null) return answer;
            var pending = lookup.Tasks.Where(task => !task.IsCompleted).ToArray();
            var elapsed = LyricsClock.GetUtcNow() - started;
            var remaining = Enumerable.Range(0, sources.Count).Where(rank => !attempted.Contains(rank)).ToArray();
            if ((elapsed >= TimeSpan.FromSeconds(3) || pending.Length == 0) && fallback?.IsCompleted != false)
            {
                var advanced = false;
                foreach (var next in remaining)
                {
                    var source = sources[next];
                    if (source.Local != null)
                    {
                        if (pending.Length != 0) continue;
                        var lyrics = await source.Local();
                        attempted.Add(next);
                        if (lyrics != null) AddLocal(next, source.Id, lyrics);
                        advanced = true;
                        break;
                    }
                    if (pending.Length != 0 && source.NeedsDiscovery) continue;
                    var read = await PrepareAsync(next, pending.Length == 0);
                    if (read == null) { advanced = true; continue; }
                    if (pending.Length != 0 && !read.MaySpeculate) continue;
                    fallback = Start(next);
                    advanced = true;
                    break;
                }
                if (advanced) continue;
            }
            if (pending.Length == 0) return null;
            var until = elapsed < TimeSpan.FromSeconds(1.5) ? started.AddSeconds(1.5) : started.AddSeconds(3);
            var wait = until - LyricsClock.GetUtcNow();
            var wake = wait > TimeSpan.Zero ? Task.Delay(wait, LyricsClock, lookup.Token) : Task.Delay(Timeout.InfiniteTimeSpan, LyricsClock, lookup.Token);
            await Task.WhenAny(pending.Cast<Task>().Append(wake)).WaitAsync(lookup.Token);
        }
        async Task<PreparedLyricsRead?> PrepareAsync(int rank, bool discovery)
        {
            lookup.Token.ThrowIfCancellationRequested();
            var source = sources[rank];
            if (!source.WasPrepared || discovery && source.Read == null && source.CanDiscover && !source.WasDiscovered)
            {
                source.WasPrepared = true;
                source.WasDiscovered |= discovery;
                try { source.Read = source.Prepare == null ? null : await source.Prepare(discovery, lookup.Token); lookup.Token.ThrowIfCancellationRequested(); }
                catch (Exception exception) when (exception is not (OperationCanceledException or UnauthorizedAccessException))
                {
                    source.WasDiscovered = true;
                    logger.LogWarning("Lyrics source {Provider} could not be prepared; trying the next configured source", source.Id);
                }
            }
            if (source.Read == null)
            {
                if (source.CanDiscover && !source.WasDiscovered) source.NeedsDiscovery = true;
                else attempted.Add(rank);
                return null;
            }
            if (providerIds.Contains(source.Read.Binding.ProviderId)) { attempted.Add(rank); return null; }
            return source.Read;
        }
        Task<AutomaticAnswer?> Start(int rank)
        {
            attempted.Add(rank);
            providerIds.Add(sources[rank].Read!.Binding.ProviderId);
            var task = ReadAutomaticAsync(gateway, sources[rank].Id, rank, sources[rank].Read!, lookup.Token);
            lookup.Tasks.Add(task);
            ranks[task] = rank;
            return task;
        }
        void AddLocal(int rank, string id, LyricsInfo lyrics)
        {
            var task = Task.FromResult<AutomaticAnswer?>(new(rank, id, lyrics, null));
            lookup.Tasks.Add(task);
            ranks[task] = rank;
        }
        async Task<ProtocolLyricsCandidate?> ChooseAsync()
        {
            foreach (var fault in lookup.Tasks.Where(task => task.IsFaulted)) await fault;
            for (var index = lookup.Tasks.Count - 1; index >= 0; index--)
            {
                var task = lookup.Tasks[index];
                if (!task.IsCompletedSuccessfully || task.Result is { } result && !discarded.Contains(result.Rank)) continue;
                lookup.Tasks.RemoveAt(index);
                ranks.Remove(task);
            }
            var completed = lookup.Tasks.Where(task => task.IsCompletedSuccessfully && task.Result != null)
                .Select(task => task.Result!).Where(item => !discarded.Contains(item.Rank)).OrderBy(item => item.Rank).ToArray();
            foreach (var answer in completed)
            {
                var hasHigher = lookup.Tasks.Any(task => !task.IsCompleted && ranks[task] < answer.Rank);
                if (LyricsClock.GetUtcNow() - started < TimeSpan.FromSeconds(1.5) && hasHigher) return null;
                if (answer.Read != null && !await gateway.IsCurrentAsync(answer.Read, lookup.Token))
                { discarded.Add(answer.Rank); continue; }
                caller.ThrowIfCancellationRequested();
                return new(answer.Id, answer.Lyrics);
            }
            return null;
        }
    }
    private async Task<AutomaticAnswer?> ReadAutomaticAsync(IProtocolLyricsExecutionGateway gateway, string id, int rank,
        PreparedLyricsRead read, CancellationToken token)
    {
        try
        {
            var result = await gateway.ExecuteAsync(read, LyricsCompletionMode.OwnedAutomatic, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result?.Content)) return null;
            var request = read.Spec.Request;
            return new(rank, id, new LyricsInfo
            {
                TrackName = request.TrackTitle ?? string.Empty,
                ArtistName = string.Join(", ", request.ArtistNames),
                AlbumName = request.AlbumTitle ?? string.Empty,
                Duration = request.DurationSeconds ?? 0,
                Source = id,
                Revision = result.Revision,
                TimedLines = result.TimedLines,
                Attribution = result.Attribution,
                PlainLyrics = result.Format == allstarr.Core.Capabilities.ProviderLyricsFormat.PlainText ? result.Content : null,
                SyncedLyrics = result.Format == allstarr.Core.Capabilities.ProviderLyricsFormat.PlainText ? null : result.Content
            }, read);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is not (OperationCanceledException or UnauthorizedAccessException)) { logger.LogWarning("Lyrics source {Provider} failed; trying the next configured source", id); return null; }
    }
}
