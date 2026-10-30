using allstarr.Core.Playback;
using System.Net.Http.Headers;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Music;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private async Task<ProtocolProviderStream?> OpenWithStartPreferenceAsync(ProtocolExecutionContext protocol,
        ProviderRoutePlan<IProviderStreamingCapability> plan, ProviderExternalResourceId requestedTrack,
        ProviderAudioQuality quality, EffectiveProviderPolicySnapshot policy,
        DateTimeOffset startDeadline, DateTimeOffset transferDeadline, RangeHeaderValue? requestedRange, CancellationToken startDeadlineCancellation,
        CancellationToken streamCancellation,
        Func<ProtocolProviderStream, ProviderExecutionContext?, ProviderExternalResourceId?, Task<ProtocolProviderStream>> opened,
        Func<Task<ProviderRoutePlan<IProviderStreamingCapability>?>> resolveAvailability,
        HashSet<(string Provider, Guid? Account, string Track)>? attempted = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        attempted ??= [];
        var eligible = plan.Candidates.Where(candidate => IsAutomaticProvider(candidate.Provider.Id, policy))
            .Where(candidate => !attempted.Contains((candidate.Provider.Id, candidate.Context.Account?.AccountId,
                (candidate.TrackId ?? requestedTrack).Value)))
            .DistinctBy(candidate => (candidate.Provider.Id, candidate.Context.Account?.AccountId,
                (candidate.TrackId ?? requestedTrack).Value)).Take(32).ToArray();
        var mode = policy.Preferences.PlaybackStart ?? "Balanced";
        var facts = new List<PlaybackStartCandidate>();
        for (var index = 0; index < eligible.Length; index++)
        {
            var candidate = eligible[index];
            var track = candidate.TrackId ?? requestedTrack;
            if (!await AllowsCleanPlaybackAsync(protocol, track, candidate.Context)) continue;
            var measurement = outcomes?.GetPlaybackStart(candidate.Context, quality);
            var media = measurement?.Media;
            var cached = await AvailableCachedMediaAsync(protocol, candidate.Context, track, quality, protocol.CancellationToken);
            if (cached != null)
            {
                media = cached;
            }
            else if (MediaFacts is { } mediaStore)
            {
                var song = new Song { ExternalProvider = track.ProviderId, ExternalId = track.Value };
                try { await mediaStore.ApplyStoredMediaFactsAsync(protocol.RequireActor(), [song], protocol.CancellationToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger?.LogDebug("Stored playback format is unavailable ({ErrorType})", error.GetType().Name); }
                if (song.Codec != null)
                    media = new(AudioMediaFacts.Mime(song.Container ?? "unknown"), song.Container ?? "unknown", song.Codec,
                        song.Bitrate, song.SampleRate, song.BitDepth, song.Channels);
            }
            if (cached != null || outcomes?.IsPlaybackCoolingDown(candidate.Context, track, quality) != true)
                facts.Add(new(index, media, measurement?.MedianStart, cached != null));
        }
        var order = PlaybackStartPolicy.Order(mode, facts);
        var startReason = mode switch
        {
            "FastStart" => "playback-start-fast",
            "BestQuality" => "playback-start-quality",
            _ => "playback-start-balanced"
        };
        plan = plan with { Candidates = order.Select(index => eligible[index]).ToArray() };
        var localCapacity = 0;
        ProviderError? lastFailure = null;
        string? fallbackReason = null;
        var count = Math.Min(4 - attempted.Count, plan.Candidates.Count);
        for (var index = 0; index < count; index++)
        {
            protocol.CancellationToken.ThrowIfCancellationRequested();
            var current = index;
            var hedged = false;
            using var result = index + 1 < count
                ? await PlaybackStartHedge.FirstReadyAsync(
                    (token, abandonment) => StartAsync(current, token, abandonment),
                    (token, abandonment) =>
                    {
                        hedged = true;
                        return StartAsync(current + 1, token, abandonment);
                    },
                    item => item.Stream != null || item.Error?.Code == LocalCapacityException.Code || item.Error?.Kind is ProviderErrorKind.Unauthorized or ProviderErrorKind.Forbidden,
                    item => item.Dispose(), PlaybackStartPolicy.HedgeDelay(mode), clock, protocol.CancellationToken)
                : await StartAsync(current, protocol.CancellationToken, CancellationToken.None);
            if (result.Stream != null)
            {
                var stream = result.TakeStream();
                try { return await opened(stream, result.Execution, result.Track); }
                catch { stream.Response.Dispose(); throw; }
            }
            if (result.Error?.Code == LocalCapacityException.Code) throw new LocalCapacityException();
            lastFailure = result.Error ?? new(ProviderErrorKind.TransientFailure);
            var fallback = router.EvaluateFallback(plan, result.Index, lastFailure);
            if (fallback.Disposition == ProviderFallbackDisposition.Exhausted && attempted.Count < 4 &&
                await resolveAvailability() is { } refreshed)
                return await OpenWithStartPreferenceAsync(protocol, refreshed, requestedTrack, quality, policy,
                    startDeadline, transferDeadline, requestedRange, startDeadlineCancellation, streamCancellation, opened, resolveAvailability, attempted);
            if (fallback.Disposition != ProviderFallbackDisposition.Advance) ThrowRouteFailure(lastFailure);
            fallbackReason = $"fallback-{lastFailure.Code}";
            if (hedged) index++;
        }
        ThrowRouteFailure(lastFailure ?? new(ProviderErrorKind.TransientFailure));
        return null;

        async Task<PlaybackStartResult> StartAsync(int index, CancellationToken cancellation, CancellationToken abandonment)
        {
            var candidate = plan.Candidates[index];
            var track = candidate.TrackId ?? requestedTrack;
            attempted.Add((candidate.Provider.Id, candidate.Context.Account?.AccountId, track.Value));
            var scope = managedTrackCache?.ScopeFor(protocol, candidate.Context.Account?.AccountId, quality,
                candidate.Context.Account?.Revision) ?? new DownloadedSongMappingScope(candidate.Context.Account?.AccountId, quality);
            IDisposable? gate = null;
            CancellationTokenSource? abandoning = null;
            HttpResponseMessage? response = null;
            var transferred = false;
            var started = clock.GetTimestamp();
            try
            {
                if (managedTrackCache != null)
                {
                    gate = await managedTrackCache.BeginOpenAsync(track, scope, candidate.Context.Account?.Revision, false, cancellation,
                        originalDownload: protocol.IsOriginalDownload);
                    var cached = await OpenAvailableCacheAsync(protocol, candidate.Context, track, quality,
                        requestedRange?.Ranges.Single().From, false, requestedRange != null, cancellation);
                    if (cached != null)
                    {
                        if (!CanDeliverCached(protocol, cached))
                        {
                            cached.Response.Dispose();
                            return new(index, null, candidate.Context, track, ProviderError.AudioProfileUnavailable(), null);
                        }
                        transferred = true;
                        return new(index, cached with { SelectionReason = $"{startReason}:managed-cache" },
                            candidate.Context, track, null, gate);
                    }
                }
                if (outcomes?.IsPlaybackCoolingDown(candidate.Context, track, quality) == true)
                    return new(index, null, candidate.Context, track, new(ProviderErrorKind.TransientFailure), null);
                var deadline = startDeadline;
                var remaining = deadline - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero) throw new OperationCanceledException(cancellation);
                var budget = index + 1 < count && remaining > GetSourceStartTimeout() ? GetSourceStartTimeout() : remaining;
                using var timeout = new CancellationTokenSource(budget, clock);
                using var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
                abandoning = CancellationTokenSource.CreateLinkedTokenSource(abandonment, timeout.Token, startDeadlineCancellation);
                var execution = new ProviderExecutionContext(candidate.Context.Actor, candidate.Context.ProviderId,
                    candidate.Context.Account, candidate.Context.Policy, candidate.Context.OperationId,
                    candidate.Context.CorrelationId, clock.GetUtcNow().Add(budget), sourceCancellation.Token, candidate.Context.IdempotencyKey)
                {
                    PlaybackAbandonment = abandoning.Token,
                    PlaybackTransferDeadline = candidate.Implementation is IProviderDownloadStreamingCapability ? transferDeadline : null
                };
                var leaseFailed = false;
                async Task<ProviderOutcome<ProviderStreamLease>> ResolveAsync()
                {
                    var resolved = await candidate.Implementation.GetStreamLeaseAsync(execution, new(track, quality, requestedRange?.Ranges.Single().From) { PreferOriginalContainer = protocol.IsOriginalDownload });
                    leaseFailed = !resolved.IsSuccess;
                    return resolved;
                }
                async Task<HttpResponseMessage> OpenAtAsync(ProviderStreamLease lease, RangeHeaderValue? range,
                    EntityTagHeaderValue? tag, CancellationToken token)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
                    if (range != null && lease.SupportsByteRanges) request.Headers.Range = range;
                    if (tag != null) request.Headers.IfRange = new RangeConditionHeaderValue(tag);
                    var pending = lease.ProtectedResponseFactory != null ? lease.ProtectedResponseFactory(request, token) :
                        httpClientFactory.CreateClient(StreamingClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    try { return await pending.WaitAsync(token); }
                    catch
                    {
                        _ = pending.ContinueWith(completed =>
                        {
                            if (completed.IsCompletedSuccessfully) completed.Result.Dispose();
                            else _ = completed.Exception;
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        throw;
                    }
                }
                ProviderOutcome<(HttpResponseMessage Response, ProviderStreamLease Lease)> outcome;
                try
                {
                    outcome = await OpenCandidateAsync(ResolveAsync,
                        lease => OpenAtAsync(lease, requestedRange, null, sourceCancellation.Token), false, requestedRange, sourceCancellation.Token,
                        await ExpectedPlaybackDurationAsync(execution, track, requestedTrack), protocol.AudioDelivery,
                        async (_, offset, end, token) =>
                        {
                            var refreshed = await candidate.Implementation.GetStreamLeaseAsync(ResumeExecution(execution, token, opening: true), new(track, quality, offset) { PreferOriginalContainer = protocol.IsOriginalDownload });
                            if (!refreshed.IsSuccess) ThrowRouteFailure(refreshed.Error!);
                            return await OpenAtAsync(refreshed.RequireValue(), new(offset, end), null, token);
                        }, candidate.Provider.Id);
                }
                catch (OperationCanceledException) when (abandonment.IsCancellationRequested && !protocol.CancellationToken.IsCancellationRequested)
                {
                    if (Volatile.Read(ref localCapacity) == 0)
                        outcomes?.ObservePlayback(execution, track, quality, new(ProviderErrorKind.TransientFailure), clock.GetElapsedTime(started));
                    throw;
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { outcome = ProviderOutcome<(HttpResponseMessage, ProviderStreamLease)>.Failure(new(ProviderErrorKind.TransientFailure)); }
                if (timeout.IsCancellationRequested && outcome.Error?.Kind == ProviderErrorKind.Canceled)
                    outcome = ProviderOutcome<(HttpResponseMessage, ProviderStreamLease)>.Failure(new(ProviderErrorKind.TransientFailure));
                if (outcome.IsSuccess) response = outcome.RequireValue().Response;
                if (abandonment.IsCancellationRequested)
                {
                    if (!outcome.IsSuccess)
                        outcomes?.ObservePlayback(execution, track, quality, new(ProviderErrorKind.TransientFailure), clock.GetElapsedTime(started));
                    abandonment.ThrowIfCancellationRequested();
                }
                cancellation.ThrowIfCancellationRequested();
                if (outcome.Error?.Code == LocalCapacityException.Code) Interlocked.Exchange(ref localCapacity, 1);
                if (outcome.Error?.Code != "audio-profile-unavailable") outcomes?.ObservePlayback(execution, track, quality, outcome.Error, clock.GetElapsedTime(started),
                    outcome.IsSuccess ? outcome.RequireValue().Lease.Media : null);
                if (!leaseFailed && outcome.Error?.Code != "audio-profile-unavailable") outcomes?.Observe(execution, ProviderCapabilityKind.Streaming, outcome.Error, clock.GetElapsedTime(started));
                if (!outcome.IsSuccess) return new(index, null, execution, track, outcome.Error, null);
                var lease = outcome.RequireValue().Lease;
                response = outcome.RequireValue().Response;
                var failureReported = 0;
                var sourceFacts = new PlaybackFileFacts();
                void Failed()
                {
                    if (Interlocked.Exchange(ref failureReported, 1) != 0) return;
                    sourceFacts.Failed = true;
                    var failure = new ProviderError(ProviderErrorKind.TransientFailure);
                    outcomes?.ObservePlayback(execution, track, quality, failure, clock.GetElapsedTime(started));
                    outcomes?.Observe(execution, ProviderCapabilityKind.Streaming, failure, clock.GetElapsedTime(started));
                    logger?.LogInformation("Playback source {Provider} stopped after opening ({FailureCode})", candidate.Provider.Id, failure.Code);
                }
                async Task<HttpResponseMessage> ResumeAsync(long offset, long? end, EntityTagHeaderValue tag, CancellationToken token)
                {
                    var renewed = await candidate.Implementation.GetStreamLeaseAsync(ResumeExecution(execution, token), new(track, quality, offset) { PreferOriginalContainer = protocol.IsOriginalDownload });
                    if (!renewed.IsSuccess) ThrowRouteFailure(renewed.Error!);
                    return await OpenAtAsync(renewed.RequireValue(), new(offset, end), tag, token);
                }
                await ProviderPlaybackStream.AttachAsync(response, lease.SupportsByteRanges, ResumeAsync, Failed, clock, streamCancellation);
                sourceFacts.ExpectedDurationSeconds = await ExpectedPlaybackDurationAsync(execution, track, requestedTrack);
                sourceFacts.SourceFailed = Failed;
                async Task<HttpResponseMessage> FullAsync(CancellationToken token)
                {
                    var full = await OpenCandidateAsync(
                        () => candidate.Implementation.GetStreamLeaseAsync(ResumeExecution(execution, token), new(track, quality) { PreferOriginalContainer = protocol.IsOriginalDownload }),
                        selected => OpenAtAsync(selected, null, null, token), false, null, token, sourceFacts.ExpectedDurationSeconds);
                    if (!full.IsSuccess) ThrowRouteFailure(full.Error!);
                    var complete = full.RequireValue();
                    sourceFacts.Measured = PrefetchedStream.Facts(complete.Response.Content);
                    sourceFacts.Media = sourceFacts.Measured?.Media ?? complete.Lease.Media;
                    await ProviderPlaybackStream.AttachAsync(complete.Response, complete.Lease.SupportsByteRanges, ResumeAsync, Failed, clock, token);
                    return complete.Response;
                }
                var reason = string.Join(':', fallbackReason ?? startReason,
                    execution.Account?.ResolutionReason ?? "account-free", "remote");
                var stream = new ProtocolProviderStream(response, lease, candidate.Provider.Id, track.Value,
                    execution.Account?.AccountId, EffectiveQuality: quality, SelectionReason: reason,
                    ServingAccountRevision: execution.Account?.Revision)
                { FileFacts = sourceFacts, OpenOriginalResponseAsync = FullAsync };
                transferred = true;
                return new(index, stream, execution, track, null, gate, abandoning);
            }
            finally
            {
                if (!transferred)
                {
                    if (clock.GetUtcNow() >= startDeadline && !streamCancellation.IsCancellationRequested)
                        abandoning?.Cancel();
                    response?.Dispose();
                    gate?.Dispose();
                    abandoning?.Dispose();
                }
            }
        }
    }

    private sealed class PlaybackStartResult(int index, ProtocolProviderStream? stream,
        ProviderExecutionContext execution, ProviderExternalResourceId track, ProviderError? error, IDisposable? gate,
        CancellationTokenSource? abandonment = null) : IDisposable
    {
        public int Index { get; } = index;
        public ProtocolProviderStream? Stream { get; private set; } = stream;
        public ProviderExecutionContext Execution { get; } = execution;
        public ProviderExternalResourceId Track { get; } = track;
        public ProviderError? Error { get; } = error;
        public ProtocolProviderStream TakeStream()
        {
            var result = Stream!;
            Stream = null;
            return result;
        }
        public void Dispose() { Stream?.Response.Dispose(); gate?.Dispose(); abandonment?.Dispose(); }
    }
}
