using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Music;
using allstarr.Core.ClientApi;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolLyricsResolver
{
    private sealed class AutomaticSource(string id, Func<CancellationToken, Task<PreparedLyricsRead?>>? prepare,
        Func<CancellationToken, Task<LyricsInfo?>>? local, bool automaticEligible)
    {
        internal string Id => id;
        internal bool AutomaticEligible => automaticEligible;
        internal Func<CancellationToken, Task<PreparedLyricsRead?>>? Prepare => prepare;
        internal Func<CancellationToken, Task<LyricsInfo?>>? Local => local;
        internal PreparedLyricsRead? Read { get; set; }
        internal LyricsInfo? Lyrics { get; set; }
        internal bool Known { get; set; }
        internal bool Finished { get; set; }
        internal bool Deferred { get; set; }
    }

    private sealed class AutomaticLookup(string key, DateTimeOffset deadline, CancellationTokenSource timer,
        CancellationTokenSource cancellation, ProtocolExecutionContext protocol, Song song, bool householdOnly,
        string? preferred, long? recordingDurationMs, IReadOnlyList<AutomaticSource> sources, bool cacheOnly)
    {
        internal string Key => key;
        internal DateTimeOffset Deadline => deadline;
        internal ProtocolExecutionContext Protocol => protocol;
        internal Song Song => song;
        internal bool HouseholdOnly => householdOnly;
        internal string? Preferred => preferred;
        internal long? RecordingDurationMs => recordingDurationMs;
        internal bool CacheOnly => cacheOnly;
        internal IReadOnlyList<AutomaticSource> Sources => sources;
        internal CancellationToken Token => cancellation.Token;
        internal object Gate { get; } = new();
        internal TaskCompletionSource Changed { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Observed { get; set; }
        internal void Pulse()
        {
            var changed = Changed;
            Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
        internal void Dispose() { cancellation.Dispose(); timer.Dispose(); }
    }

    private readonly object _automaticGate = new();
    private readonly Dictionary<string, AutomaticLookup> _automatic = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _automaticShutdown = CancellationTokenSource.CreateLinkedTokenSource(lifetime?.ApplicationStopping ?? default);
    private TimeProvider LyricsClock => timeProvider ?? TimeProvider.System;
    private LyricsLookupOptions LookupOptions { get; } = lookupOptions ?? new();

    private static ProtocolExecutionContext WithLyricsToken(ProtocolExecutionContext context, CancellationToken token, DateTimeOffset deadline) =>
        new(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId, context.Principal,
            context.CorrelationId, deadline, token, context.Client)
        {
            Policy = context.Policy,
            RequestedItemId = context.RequestedItemId,
            IsPreparation = context.IsPreparation,
            IsReadOnly = context.IsReadOnly
        };

    private AutomaticLookup? EnterAutomatic(string key, ProtocolExecutionContext protocol, Song song, bool householdOnly,
        string? preferred, long? recordingDurationMs, Func<ProtocolExecutionContext, DateTimeOffset, IReadOnlyList<AutomaticSource>> build)
    {
        lock (_automaticGate)
        {
            if (_automatic.TryGetValue(key, out var existing)) return existing;
            if (_automaticShutdown.IsCancellationRequested) return null;
            var cacheOnly = _automatic.Count >= 4;
            var now = LyricsClock.GetUtcNow();
            var deadline = now.Add(cacheOnly ? LookupOptions.ResponseBudget : LookupOptions.BackgroundTimeout);
            if (protocol.Deadline < deadline) deadline = protocol.Deadline;
            if (deadline <= now) return null;
            var timer = new CancellationTokenSource(deadline - now, LyricsClock);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, _automaticShutdown.Token);
            var owned = WithLyricsToken(protocol, cancellation.Token, deadline);
            var lookup = new AutomaticLookup(key, deadline, timer, cancellation, owned, song, householdOnly,
                preferred, recordingDurationMs, build(owned, deadline), cacheOnly);
            if (!cacheOnly) _automatic.Add(key, lookup);
            _ = PopulateAutomaticAsync(lookup);
            return lookup;
        }
    }

    private async Task PopulateAutomaticAsync(AutomaticLookup lookup)
    {
        using var preparation = new SemaphoreSlim(LookupOptions.Parallelism);
        using var remote = new SemaphoreSlim(LookupOptions.Parallelism);
        var bindings = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await Task.WhenAll(lookup.Sources.Select(ReadAsync));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Lyrics lookup ended before all sources completed ({ErrorType})", exception.GetType().Name);
        }
        finally
        {
            lock (lookup.Gate)
            {
                foreach (var source in lookup.Sources) source.Finished = true;
                lookup.Pulse();
            }
            if (!lookup.CacheOnly)
                lock (_automaticGate) _automatic.Remove(lookup.Key);
            lookup.Completion.TrySetResult();
            lookup.Dispose();
        }

        async Task ReadAsync(AutomaticSource source)
        {
            try
            {
                if (source.Local != null)
                {
                    var local = await source.Local(lookup.Token).WaitAsync(lookup.Token);
                    if (local != null) local.Source = source.Id;
                    await AcceptAsync(source, local, true);
                    return;
                }
                if (providers is not IProtocolLyricsExecutionGateway gateway) return;
                source.Deferred = lookup.CacheOnly;
                await preparation.WaitAsync(lookup.Token);
                try
                {
                    using var timer = new CancellationTokenSource(LookupOptions.SourceTimeout, LyricsClock);
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lookup.Token, timer.Token);
                    source.Read = source.Prepare == null ? null : await source.Prepare(attempt.Token);
                    if (source.Read == null) { source.Deferred = false; return; }
                    lock (bindings)
                        if (!bindings.Add(source.Read.Binding.ProviderId)) return;
                    var cached = await gateway.ExecuteAsync(source.Read, LyricsCompletionMode.CacheOnly, attempt.Token);
                    if (HasLyrics(cached))
                    {
                        source.Deferred = false;
                        await AcceptAsync(source, Map(cached!, source), true);
                        return;
                    }
                }
                finally { preparation.Release(); }
                if (lookup.CacheOnly) return;
                await remote.WaitAsync(lookup.Token);
                try
                {
                    using var timer = new CancellationTokenSource(LookupOptions.SourceTimeout, LyricsClock);
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lookup.Token, timer.Token);
                    var result = await gateway.ExecuteAsync(source.Read!, LyricsCompletionMode.OwnedAutomatic, attempt.Token);
                    if (HasLyrics(result) && !attempt.IsCancellationRequested)
                        await AcceptAsync(source, Map(result!, source), false);
                }
                finally { remote.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                logger.LogWarning("Lyrics source {Provider} failed ({ErrorType})", source.Id, exception.GetType().Name);
            }
            finally
            {
                lock (lookup.Gate) { source.Finished = true; lookup.Pulse(); }
            }
        }

        LyricsInfo Map(allstarr.Core.Capabilities.ProviderLyricsResult result, AutomaticSource source) =>
            LyricsVersions.Map(result, source.Id, lookup.Song.Duration,
                LyricsSearchIndex.AccountProvenance(source.Read!.Binding.AccountId, source.Read.Binding.AccountRevision));

        async Task AcceptAsync(AutomaticSource source, LyricsInfo? lyrics, bool known)
        {
            lookup.Token.ThrowIfCancellationRequested();
            lock (lookup.Gate)
            {
                source.Lyrics = lyrics;
                source.Known = known;
                if (lookup.Observed) ObserveAutomatic(lookup, BestAutomatic(lookup));
                lookup.Pulse();
            }
            if (lyrics != null && !lookup.HouseholdOnly && !lookup.Protocol.IsReadOnly)
            {
                if (lyrics.Versions == null) await IndexAsync(lookup.Protocol, lookup.Song, lyrics);
                else
                {
                    var versions = LyricsVersions.Candidates(source.Id, lyrics);
                    foreach (var version in versions)
                    {
                        version.Source = LyricsVersions.SourceId(source.Id, version.Metadata);
                        await IndexAsync(lookup.Protocol, lookup.Song, version);
                    }
                    if (searchIndex != null)
                        await searchIndex.ReconcileVersionsAsync(lookup.Protocol, lookup.Song, source.Id,
                            versions.Select(version => version.Source!).ToHashSet(StringComparer.Ordinal));
                }
            }
        }
    }

    private static bool HasLyrics(allstarr.Core.Capabilities.ProviderLyricsResult? result) =>
        result?.Availability == allstarr.Core.Capabilities.ProviderLyricsAvailabilityState.Available &&
        (!string.IsNullOrWhiteSpace(result.Content) || result.Versions?.Any(version => !string.IsNullOrWhiteSpace(version.Content)) == true);

    private static IEnumerable<ProtocolLyricsCandidate> AutomaticCandidates(AutomaticLookup lookup) =>
        lookup.Sources.SelectMany(source => source.Lyrics == null
            ? new[] { new ProtocolLyricsCandidate(source.Id, null) { State = source.Deferred ? "pending" : source.Finished ? "unavailable" : "pending" } }
            : LyricsVersions.Candidates(source.Id, source.Lyrics).Select(version =>
                new ProtocolLyricsCandidate(source.Lyrics.Versions == null ? source.Id : LyricsVersions.SourceId(source.Id, version.Metadata), version)
                {
                    AutomaticEligible = source.AutomaticEligible,
                    IsDefaultVersion = RichLyricsMapper.Map(source.Lyrics) != null && (source.Lyrics.Versions == null ||
                        LyricsVersions.SourceId(source.Id, version.Metadata) == LyricsVersions.SourceId(source.Id, source.Lyrics.Metadata))
                })).Take(20);

    private static ProtocolLyricsCandidate? BestAutomatic(AutomaticLookup lookup)
    {
        var candidates = Assess(AutomaticCandidates(lookup), lookup.Song, lookup.RecordingDurationMs);
        var ready = candidates.Where(candidate => candidate.Assessment != null || RichLyricsMapper.Map(candidate.Lyrics) != null).ToArray();
        if (lookup.Preferred is { } preferred)
        {
            var pinned = ready.FirstOrDefault(candidate => candidate.SourceId == preferred) ??
                ready.FirstOrDefault(candidate => candidate.IsDefaultVersion && LyricsVersions.ProviderId(candidate.SourceId) == LyricsVersions.ProviderId(preferred)) ??
                ready.FirstOrDefault(candidate => LyricsVersions.ProviderId(candidate.SourceId) == LyricsVersions.ProviderId(preferred));
            if (pinned != null) return pinned;
        }
        return ready.Where(candidate => candidate.Assessment != null).OrderByDescending(candidate => candidate.Assessment!.Score).FirstOrDefault();
    }

    private static ProtocolLyricsCandidate? BestVersion(string provider, LyricsInfo? lyrics, Song song, long? recordingDurationMs)
    {
        return LyricsVersions.Candidates(provider, lyrics).Select(version =>
        {
            var id = lyrics?.Versions == null ? provider : LyricsVersions.SourceId(provider, version.Metadata);
            return new ProtocolLyricsCandidate(id, version) { Assessment = LyricsQuality.Evaluate(version, song, recordingDurationMs) };
        }).Where(candidate => candidate.Assessment != null).OrderByDescending(candidate => candidate.Assessment!.Score).FirstOrDefault();
    }

    private static bool IsBestPossible(AutomaticLookup lookup, ProtocolLyricsCandidate best)
    {
        if (lookup.Preferred != null && LyricsVersions.ProviderId(best.SourceId) == LyricsVersions.ProviderId(lookup.Preferred)) return true;
        var score = best.Assessment?.Score ?? -1;
        var bestRank = lookup.Sources.ToList().FindIndex(source => source.Id == LyricsVersions.ProviderId(best.SourceId));
        return !lookup.Sources.Where((source, rank) => !source.Finished &&
            (source.Id == LyricsVersions.ProviderId(lookup.Preferred ?? "") || LyricsQuality.CanImprove(source.Id, rank, score, bestRank, lookup.Song, lookup.RecordingDurationMs))).Any();
    }

    private void ObserveAutomatic(AutomaticLookup lookup, ProtocolLyricsCandidate? best)
    {
        var context = lookup.Protocol;
        if (context.Actor?.EffectiveUserId is { } owner && !context.IsPreparation && !context.IsReadOnly && !lookup.HouseholdOnly)
            clientUpdates?.Observe(owner, string.IsNullOrWhiteSpace(context.RequestedItemId) ? lookup.Song.Id : context.RequestedItemId, best?.Lyrics);
    }

    private async Task<IReadOnlyList<ProtocolLyricsCandidate>> WaitAutomaticAsync(AutomaticLookup lookup,
        bool all, CancellationToken caller, DateTimeOffset responseDeadline)
    {
        var now = LyricsClock.GetUtcNow();
        var budget = responseDeadline - now;
        if (lookup.Deadline - now < budget) budget = lookup.Deadline - now;
        if (budget < TimeSpan.Zero) budget = TimeSpan.Zero;
        using var timer = new CancellationTokenSource(budget, LyricsClock);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, caller, _automaticShutdown.Token);
        while (true)
        {
            caller.ThrowIfCancellationRequested();
            if (providers is IProtocolLyricsExecutionGateway gateway)
            {
                AutomaticSource[] completed;
                lock (lookup.Gate) completed = lookup.Sources.Where(source => source.Lyrics != null && source.Read != null).ToArray();
                try { await Task.WhenAll(completed.Select(ValidateAsync)).WaitAsync(waiting.Token); }
                catch (OperationCanceledException) when (!caller.IsCancellationRequested) { }

                async Task ValidateAsync(AutomaticSource source)
                {
                    using var validationTimer = new CancellationTokenSource(LookupOptions.SourceTimeout, LyricsClock);
                    using var validation = CancellationTokenSource.CreateLinkedTokenSource(validationTimer.Token, caller, _automaticShutdown.Token);
                    try
                    {
                        if (!await gateway.IsCurrentAsync(source.Read!, validation.Token))
                            lock (lookup.Gate) source.Lyrics = null;
                    }
                    catch (OperationCanceledException) when (!caller.IsCancellationRequested) { }
                }
            }
            Task changed;
            lock (lookup.Gate)
            {
                var best = BestAutomatic(lookup);
                if (timer.IsCancellationRequested || _automaticShutdown.IsCancellationRequested || lookup.Sources.All(source => source.Finished) ||
                    !all && best != null && (lookup.Sources.Any(source => source.Known && source.Id == LyricsVersions.ProviderId(best.SourceId)) || IsBestPossible(lookup, best)))
                {
                    lookup.Observed = true;
                    ObserveAutomatic(lookup, best);
                    if (!all)
                    {
                        if (best?.Lyrics != null) best.Lyrics.Source = best.SourceId;
                        return best == null ? [] : [best];
                    }
                    return AutomaticCandidates(lookup).ToArray();
                }
                changed = lookup.Changed.Task;
            }
            try { await changed.WaitAsync(waiting.Token); }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested) { }
        }
    }

    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken token)
    {
        _automaticShutdown.Cancel();
        AutomaticLookup[] lookups;
        lock (_automaticGate) lookups = _automatic.Values.ToArray();
        await Task.WhenAll(lookups.Select(item => item.Completion.Task)).WaitAsync(token);
    }
}
