using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

public sealed class ProviderCtsDiagnosticRunner(
    IProviderRegistry providers,
    IProviderRouteAccountResolver accounts,
    ProviderCtsTrackSelector trackSelector,
    IProviderOutcomeObserver health,
    IDbContextFactory<AllstarrDbContext>? contextFactory = null,
    ProtocolExecutionOptions? executionOptions = null,
    TimeProvider? timeProvider = null,
    IProviderCheckSessions? sessions = null,
    ProviderStatusManager? statusManager = null,
    allstarr.Core.Matching.ITrackIdentityService? identities = null)
{
    private readonly ProviderDiagnosticSamples _diagnosticSamples = new();

    private const int SampleLimitBytes = 64 * 1024;
    private const int MaximumTrackAttempts = 64;
    private static readonly BoundedOperationGate Concurrency = new(2);

    public async Task<ProviderCtsDiagnosticResult> MeasureAsync(
        ProviderActorContext actor,
        string providerId,
        Guid? providerAccountId,
        ProviderAudioQuality quality,
        string correlationId,
        string? trackId = null,
        CancellationToken cancellationToken = default,
        bool householdCheck = false)
    {
        providerId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        var clock = timeProvider ?? TimeProvider.System;
        var startedAt = clock.GetTimestamp();
        var downloadBacked = providers.TryGetCapability<IProviderStreamingCapability>(
            providerId, ProviderCapabilityKind.Streaming, out var streaming) &&
            streaming is IProviderDownloadStreamingCapability;
        var timeout = downloadBacked
            ? (executionOptions ?? new ProtocolExecutionOptions()).GetDownloadDiagnosticTimeout()
            : TimeSpan.FromSeconds(30);
        using var timer = new CancellationTokenSource(timeout, clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        var progress = new MeasurementProgress();
        ProviderCtsDiagnosticResult result;
        try
        {
            result = await MeasureCoreAsync(
                actor, providerId, providerAccountId, quality, correlationId, trackId,
                deadline.Token, clock.GetUtcNow().Add(timeout), progress).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            result.Limit = new ProviderCtsDiagnosticLimit { TimeoutSeconds = (int)timeout.TotalSeconds };
            if (contextFactory != null)
            {
                progress.Stage = "recording";
                await RecordResultAsync(result, actor, correlationId, householdCheck, deadline.Token).WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
            }
            var measured = statusManager?.RecordStreamingMeasurement(result, progress.Execution?.Account?.Revision);
            var verified = statusManager?.RecordDownloadVerification(result, progress.Execution?.Account?.Revision);
            if (contextFactory != null)
                foreach (var check in new[] { measured, verified }.OfType<ProviderCheckResult>())
                    await new ProviderCheckHistory(contextFactory, clock).RecordAsync(check, correlationId, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (LocalCapacityException)
        {
            return ProviderCtsDiagnosticResult.Failure(503, providerId, providerAccountId, "admission",
                LocalCapacityException.Code, retryAfterSeconds: 5);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timer.IsCancellationRequested)
        {
            var unavailable = string.IsNullOrWhiteSpace(trackId) &&
                progress.Stage is "track-selection" or "resolve" or "lease-validation" or "first-byte" or "response-range" &&
                progress.SelectionFailureCount + progress.SourceFailureCount < ProviderDiagnosticSamples.SourceFailureThreshold;
            if (!unavailable && progress.Execution != null)
                RecordFailure(progress.Execution, "timeout", clock.GetElapsedTime(startedAt).TotalMilliseconds);
            result = ProviderCtsDiagnosticResult.Failure(
                unavailable ? StatusCodes.Status409Conflict : StatusCodes.Status504GatewayTimeout,
                providerId, providerAccountId, progress.Stage, unavailable ? "sample-unavailable" : "timeout");
            statusManager?.RecordStreamingMeasurement(result, progress.Execution?.Account?.Revision);
        }
        result.Limit = new ProviderCtsDiagnosticLimit { TimeoutSeconds = (int)timeout.TotalSeconds };
        return result;
    }

    private async Task RecordResultAsync(ProviderCtsDiagnosticResult result, ProviderActorContext actor,
        string correlationId, bool householdCheck, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory!.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = householdCheck ? null : actor.EffectiveUserId,
            Category = "provider-cts",
            Action = householdCheck ? "scheduled.measure" : "cold-connect.measure",
            Outcome = householdCheck ? "recorded" : result.Succeeded ? "succeeded" :
                ProviderRuntimeStatus.IsNeutralReason(result.ReasonCode) ? "sample-unavailable" : "failed",
            CorrelationId = correlationId,
            DetailsJson = JsonSerializer.Serialize(result),
            CreatedAt = result.MeasuredAt
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ProviderCtsDiagnosticResult> MeasureCoreAsync(
        ProviderActorContext actor,
        string providerId,
        Guid? providerAccountId,
        ProviderAudioQuality quality,
        string correlationId,
        string? trackId,
        CancellationToken cancellationToken,
        DateTimeOffset expiresAt,
        MeasurementProgress progress)
    {
        if (!providers.TryGetCapability<IProviderStreamingCapability>(
                providerId, ProviderCapabilityKind.Streaming, out var streaming) || streaming == null)
        {
            return ProviderCtsDiagnosticResult.Failure(
                StatusCodes.Status400BadRequest,
                providerId,
                providerAccountId,
                "capability",
                "The selected provider does not expose streaming diagnostics.");
        }
        using var probeLease = await Concurrency.TryEnterAsync(cancellationToken);
        if (probeLease == null)
            return ProviderCtsDiagnosticResult.Failure(429, providerId, providerAccountId,
                "concurrency", "busy", retryAfterSeconds: 5);
        var descriptor = providers.GetRequired(providerId);
        var total = Stopwatch.StartNew();
        progress.Stage = "account-resolution";
        var prepared = await new ProviderDiagnosticExecutionFactory(providers, accounts, sessions).CreateAsync(
            actor, providerId, ProviderCapabilityKind.Streaming, providerAccountId, correlationId, expiresAt, cancellationToken);
        if (prepared.Context == null)
            return ProviderCtsDiagnosticResult.Failure(prepared.StatusCode, providerId, providerAccountId,
                "account-resolution", prepared.ReasonCode!);
        var execution = prepared.Context;
        providerAccountId = execution.Account?.AccountId;
        var routeMilliseconds = total.Elapsed.TotalMilliseconds;

        progress.Execution = execution;
        var selectionMode = "manual";
        int? corpusSize = null;
        var automatic = string.IsNullOrWhiteSpace(trackId);
        var triedTracks = new HashSet<string>(StringComparer.Ordinal);
        await using var samples = SearchSampleTracksAsync(providerId, execution, progress, cancellationToken).GetAsyncEnumerator(cancellationToken);
        IReadOnlyList<ProviderRecordingAvailabilityRequest>? recordings = null;
        var recordingIndex = 0;
        IReadOnlyList<ProviderCtsTrackSelection> stored = [];
        var storedIndex = 0;
        var cachedTrack = _diagnosticSamples.LastWorking($"{providerId}:{providerAccountId}:cts")?.TrackId;
        ProviderError? lastSourceError = null;
        async Task<string?> NextSampleAsync()
        {
            progress.Stage = "track-selection";
            if (triedTracks.Count >= MaximumTrackAttempts) return null;
            if (cachedTrack != null && !triedTracks.Contains(cachedTrack))
            {
                selectionMode = "last-working-sample";
                return cachedTrack;
            }
            while (storedIndex < stored.Count)
            {
                var candidate = stored[storedIndex++];
                if (triedTracks.Contains(candidate.TrackId)) continue;
                selectionMode = "rotating-corpus";
                corpusSize = candidate.CorpusSize;
                return candidate.TrackId;
            }
            while (await samples.MoveNextAsync())
            {
                if (triedTracks.Contains(samples.Current)) continue;
                selectionMode = "search";
                corpusSize = null;
                return samples.Current;
            }
            if (streaming is not IProviderRecordingStreamingCapability { SupportsRecordingAvailability: true } fallback)
                return null;
            recordings ??= (await trackSelector.SelectRecordingSamplesAsync(cancellationToken,
                actor.Kind == ProviderActorKind.Administrator ? actor.UserId : null))
                .Concat(ProviderDiagnosticSamples.Recordings(providerId).Select(sample => new ProviderRecordingAvailabilityRequest(
                    sample.Query, sample.Title, [sample.Artist], sample.Isrc, sample.DurationSeconds * 1000, new Dictionary<string, string>())))
                .Take(ProviderDiagnosticSamples.MaximumAttempts).ToArray();
            while (recordingIndex < recordings.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var availability = await fallback.ResolveRecordingAsync(execution, recordings[recordingIndex++], quality);
                cancellationToken.ThrowIfCancellationRequested();
                if (!availability.IsSuccess)
                {
                    if (!ProviderDiagnosticSamples.FromError(availability.Error!).SampleUnavailable)
                    {
                        progress.SelectionError = availability.Error;
                        if (++progress.SelectionFailureCount >= ProviderDiagnosticSamples.SourceFailureThreshold) return null;
                    }
                    continue;
                }
                var match = availability.RequireValue();
                if (match.TrackId is not { } id || id.ProviderId != providerId || triedTracks.Contains(id.Value)) continue;
                selectionMode = "availability-corpus";
                corpusSize = recordings.Count;
                return id.Value;
            }
            return null;
        }

        async Task<bool> AdvanceSampleAsync(bool unavailable)
        {
            if (!automatic || !unavailable && ++progress.SourceFailureCount >= ProviderDiagnosticSamples.SourceFailureThreshold) return false;
            var next = await NextSampleAsync();
            if (next == null) return false;
            trackId = next;
            return true;
        }

        ProviderCtsDiagnosticResult SampleFailure(string stage, string reason, double latency, double? headers = null)
        {
            var neutral = automatic && progress.SourceFailureCount + progress.SelectionFailureCount < ProviderDiagnosticSamples.SourceFailureThreshold;
            if (!neutral) RecordFailure(execution, reason, latency);
            return ProviderCtsDiagnosticResult.Failure(neutral ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity,
                providerId, providerAccountId, stage, neutral ? "sample-unavailable" : reason, latency, headers);
        }

        progress.Stage = "track-selection";
        if (automatic)
        {
            stored = await trackSelector.SelectCandidatesAsync(providerId, providerAccountId, cancellationToken);
            trackId = await NextSampleAsync();
            if (trackId == null)
            {
                var reason = progress.SelectionFailureCount >= ProviderDiagnosticSamples.SourceFailureThreshold
                    ? progress.SelectionError!.Code : "sample-unavailable";
                return ProviderCtsDiagnosticResult.Failure(
                    StatusCodes.Status409Conflict, providerId, providerAccountId, "track-selection", reason);
            }
        }
        else trackId = trackId!.Trim();

        while (true)
        {
            try
            {
                var preparationStartedAt = total.Elapsed;
                ProviderOutcome<ProviderStreamLease> leaseOutcome;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    triedTracks.Add(trackId!);
                    progress.Stage = "resolve";
                    leaseOutcome = await streaming.GetStreamLeaseAsync(execution,
                        new ProviderStreamLeaseRequest(new(providerId, ProviderResourceKind.Track, trackId!), quality, 0));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (leaseOutcome.Error?.Code == LocalCapacityException.Code) throw new LocalCapacityException();
                    if (leaseOutcome.IsSuccess || !automatic) break;
                    var missing = ProviderDiagnosticSamples.FromError(leaseOutcome.Error!).SampleUnavailable;
                    if (!missing)
                    {
                        lastSourceError = leaseOutcome.Error;
                        if (++progress.SourceFailureCount >= ProviderDiagnosticSamples.SourceFailureThreshold) break;
                    }
                    var nextTrack = await NextSampleAsync();
                    if (nextTrack == null) break;
                    trackId = nextTrack;
                }
                var preparationMilliseconds = (total.Elapsed - preparationStartedAt).TotalMilliseconds;
                var resolveMilliseconds = preparationMilliseconds;
                if (!leaseOutcome.IsSuccess)
                {
                    var failure = lastSourceError ?? progress.SelectionError ?? leaseOutcome.Error!;
                    var unavailable = automatic && progress.SourceFailureCount + progress.SelectionFailureCount < ProviderDiagnosticSamples.SourceFailureThreshold;
                    var error = unavailable ? "sample-unavailable" : streaming is IProviderDownloadStreamingCapability ||
                        failure.Code == "service_unavailable" ? failure.Code : failure.Kind.ToString();
                    if (!unavailable) health.Observe(execution, ProviderCapabilityKind.Streaming, failure,
                        TimeSpan.FromMilliseconds(resolveMilliseconds));
                    return ProviderCtsDiagnosticResult.Failure(
                        unavailable ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity, providerId, providerAccountId,
                        "resolve", error, resolveMilliseconds);
                }

                progress.Stage = "lease-validation";
                var lease = leaseOutcome.RequireValue();
                if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    if (await AdvanceSampleAsync(false)) continue;
                    return SampleFailure("lease-validation", "expired-lease", resolveMilliseconds);
                }

                if (!OutboundRequestGuard.TryCreateSafeHttpUri(
                        lease.ProtectedSourceUri.AbsoluteUri, out var safeUri, out var unsafeReason))
                {
                    if (await AdvanceSampleAsync(false)) continue;
                    return SampleFailure("lease-validation", unsafeReason, resolveMilliseconds);
                }

                using var sampleClient = CreateSampleClient();
                using var sampleRequest = new HttpRequestMessage(HttpMethod.Get, safeUri);
                if (lease.SupportsByteRanges)
                    sampleRequest.Headers.Range = new RangeHeaderValue(0, SampleLimitBytes - 1);
                sampleRequest.Headers.CacheControl = new CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                    MaxAge = TimeSpan.Zero
                };
                sampleRequest.Headers.Pragma.ParseAdd("no-cache");
                progress.Stage = "first-byte";
                var upstreamHeadersStartedAt = total.Elapsed;
                using var response = lease.ProtectedResponseFactory != null
                    ? await lease.ProtectedResponseFactory(sampleRequest, cancellationToken)
                    : await sampleClient.SendAsync(
                        sampleRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var headersMilliseconds = total.Elapsed.TotalMilliseconds;
                var upstreamHeadersMilliseconds = (total.Elapsed - upstreamHeadersStartedAt).TotalMilliseconds;
                if (IsRedirect(response.StatusCode) || !response.IsSuccessStatusCode)
                {
                    var error = IsRedirect(response.StatusCode)
                        ? "redirect-rejected"
                        : $"http-{(int)response.StatusCode}";
                    if (!IsRedirect(response.StatusCode) && response.Headers.TryGetValues("X-Allstarr-Failure-Code", out var codes) &&
                        codes.SequenceEqual(["account_mismatch"], StringComparer.Ordinal))
                        error = "account_mismatch";
                    if (automatic && error != "account_mismatch")
                    {
                        var missing = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone;
                        if (!missing) progress.SourceFailureCount++;
                        if (missing || progress.SourceFailureCount < ProviderDiagnosticSamples.SourceFailureThreshold)
                        {
                            var next = await NextSampleAsync();
                            if (next != null) { trackId = next; continue; }
                            return ProviderCtsDiagnosticResult.Failure(StatusCodes.Status409Conflict, providerId, providerAccountId,
                                "track-selection", "sample-unavailable");
                        }
                    }
                    RecordFailure(execution, error, headersMilliseconds);
                    return ProviderCtsDiagnosticResult.Failure(
                        StatusCodes.Status422UnprocessableEntity,
                        providerId,
                        providerAccountId,
                        "first-byte",
                        error,
                        resolveMilliseconds,
                        headersMilliseconds);
                }

                if (!StreamByteRanges.Matches(response, sampleRequest.Headers.Range))
                {
                    if (await AdvanceSampleAsync(false)) continue;
                    return SampleFailure("response-range", "range-response-mismatch", resolveMilliseconds, headersMilliseconds);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[32 * 1024];
                var prefix = new byte[4096];
                var prefixLength = 0;
                var bytesRead = 0;
                double? firstByteMilliseconds = null;
                var transferStart = total.Elapsed;
                using var sampleHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (bytesRead < SampleLimitBytes)
                {
                    var count = await stream.ReadAsync(
                        buffer.AsMemory(0, Math.Min(buffer.Length, SampleLimitBytes - bytesRead)), cancellationToken);
                    if (count == 0) break;
                    var retained = Math.Min(count, prefix.Length - prefixLength);
                    buffer.AsSpan(0, retained).CopyTo(prefix.AsSpan(prefixLength));
                    prefixLength += retained;
                    bytesRead += count;
                    sampleHash.AppendData(buffer, 0, count);
                    firstByteMilliseconds ??= total.Elapsed.TotalMilliseconds;
                }

                if (bytesRead == 0 || !firstByteMilliseconds.HasValue)
                {
                    if (await AdvanceSampleAsync(false)) continue;
                    return SampleFailure("first-byte", "empty-media-response", total.Elapsed.TotalMilliseconds, headersMilliseconds);
                }

                var transferSeconds = Math.Max((total.Elapsed - transferStart).TotalSeconds, 0.001);
                var throughputKbps = bytesRead * 8d / 1000d / transferSeconds;
                var totalMilliseconds = total.Elapsed.TotalMilliseconds;
                health.Observe(execution, ProviderCapabilityKind.Streaming, null,
                    TimeSpan.FromMilliseconds(firstByteMilliseconds.Value));
                var facts = AudioMediaFacts.ReadPrefix(prefix.AsSpan(0, prefixLength),
                    response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength);
                health.ObservePlaybackStart(execution, quality,
                    TimeSpan.FromMilliseconds(firstByteMilliseconds.Value), facts?.Media ?? lease.Media);
                if (facts != null && identities is allstarr.Core.Matching.ITrackMediaFactsStore store)
                    await store.RememberMediaFactsAsync(execution, new(providerId, ProviderResourceKind.Track, trackId!), facts, cancellationToken);
                var success = ProviderCtsDiagnosticResult.Success(
                    providerId,
                    providerAccountId,
                    selectionMode,
                    corpusSize,
                    quality,
                    routeMilliseconds,
                    preparationMilliseconds,
                    resolveMilliseconds,
                    headersMilliseconds,
                    upstreamHeadersMilliseconds,
                    firstByteMilliseconds.Value,
                    totalMilliseconds,
                    bytesRead,
                    throughputKbps,
                    (int)response.StatusCode,
                    response.Content.Headers.ContentType?.MediaType,
                    response.Content.Headers.ContentLength,
                    response.StatusCode == HttpStatusCode.PartialContent ? response.Content.Headers.ContentRange?.ToString() : null,
                    response.StatusCode == HttpStatusCode.PartialContent &&
                        response.Headers.AcceptRanges.Any(value => value.Equals("bytes", StringComparison.OrdinalIgnoreCase)),
                    CacheState(response),
                    Convert.ToHexString(sampleHash.GetHashAndReset()).ToLowerInvariant(),
                    lease.SupportsByteRanges,
                    lease.SupportsSeeking,
                    facts?.Media ?? lease.Media,
                    lease.QualityDowngradeReason,
                    DateTimeOffset.UtcNow,
                    sampleRequest.Headers.Range?.ToString());
                success.SelectedTrackId = trackId;
                success.DownloadVerified = streaming is IProviderDownloadStreamingCapability;
                if (automatic) _diagnosticSamples.Remember($"{providerId}:{providerAccountId}:cts",
                    new("Last working recording", "Source", null, 200, trackId));
                return success;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                if (await AdvanceSampleAsync(false)) continue;
                return SampleFailure(progress.Stage, "timeout", total.Elapsed.TotalMilliseconds);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                if (await AdvanceSampleAsync(false)) continue;
                return SampleFailure(progress.Stage, "endpoint-unreachable", total.Elapsed.TotalMilliseconds);
            }
        }
    }

    private void RecordFailure(ProviderExecutionContext context, string failureCode, double latencyMilliseconds)
    {
        if (ProviderRuntimeStatus.IsNeutralReason(failureCode)) return;
        var kind = Enum.TryParse<ProviderErrorKind>(failureCode, out var parsed) ? parsed : ProviderErrorKind.TransientFailure;
        health.Observe(context, ProviderCapabilityKind.Streaming,
            new ProviderError(kind, kind == ProviderErrorKind.RateLimited ? TimeSpan.FromMinutes(1) : null),
            TimeSpan.FromMilliseconds(latencyMilliseconds));
    }

    private async IAsyncEnumerable<string> SearchSampleTracksAsync(
        string providerId, ProviderExecutionContext execution, MeasurementProgress progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        providers.TryGetCapability<IProviderMetadataCapability>(providerId, ProviderCapabilityKind.Metadata, out var metadata);
        providers.TryGetCapability<IProviderStreamingCapability>(providerId, ProviderCapabilityKind.Streaming, out var streaming);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var curated = _diagnosticSamples.Rotate($"{providerId}:{execution.Account?.AccountId}:cts",
            ProviderDiagnosticSamples.Recordings(providerId));
        var queries = curated.Concat(ProviderDiagnosticSamples.DiscoveryQueries(providerId)
            .Select(query => new ProviderDiagnosticSample(query, "", null, 200))).Take(ProviderDiagnosticSamples.MaximumAttempts);
        foreach (var sample in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sample.TrackId != null)
            {
                if (seen.Add(sample.TrackId)) yield return sample.TrackId;
                continue;
            }
            if (metadata == null)
            {
                if (providerId != "monochrome") yield break;
                if (streaming is not IProviderRecordingStreamingCapability { SupportsRecordingAvailability: true } recording) yield break;
                var match = sample.Isrc != null ? await recording.ResolveRecordingAsync(execution,
                    new(sample.Query, sample.Title, [sample.Artist], sample.Isrc, sample.DurationSeconds * 1000, new Dictionary<string, string>()), ProviderAudioQuality.Lossless)
                    : streaming is IProviderDiagnosticSampleSearch search ? await search.DiscoverSampleAsync(execution, sample.Title, ProviderAudioQuality.Lossless)
                    : ProviderOutcome<ProviderRecordingAvailability>.Success(new(null));
                if (!match.IsSuccess)
                {
                    if (SelectionFailed(match.Error!)) yield break;
                    continue;
                }
                if (match.RequireValue().TrackId is { } id && id.ProviderId == providerId && seen.Add(id.Value)) yield return id.Value;
                continue;
            }
            ProviderOutcome<ProviderPage<ProviderTrackMetadata>> outcome;
            try { outcome = await metadata.SearchTracksAsync(execution, new(sample.Query.Trim(), new(3))).WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { outcome = ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(new(ProviderErrorKind.TransientFailure)); }
            if (!outcome.IsSuccess)
            {
                if (SelectionFailed(outcome.Error!)) yield break;
                continue;
            }
            foreach (var item in outcome.RequireValue().Items.Take(3))
                if (item.Id.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) && seen.Add(item.Id.Value)) yield return item.Id.Value;
        }
        if (providerId == "artistgrid" && metadata != null)
            await foreach (var track in allstarr.Core.Providers.ArtistGrid.ArtistGridDiagnosticSamples.TracksAsync(metadata, execution))
                if (seen.Add(track.Id.Value)) yield return track.Id.Value;

        bool SelectionFailed(ProviderError error)
        {
            if (ProviderDiagnosticSamples.FromError(error).SampleUnavailable)
            {
                return false;
            }
            if (error.Kind == ProviderErrorKind.NotSupported) return false;
            progress.SelectionError = error;
            return ++progress.SelectionFailureCount >= ProviderDiagnosticSamples.SourceFailureThreshold;
        }
    }

    private sealed class MeasurementProgress
    {
        public ProviderError? SelectionError { get; set; }
        public int SelectionFailureCount { get; set; }
        public int SourceFailureCount { get; set; }
        public string Stage { get; set; } = "capability";
        public ProviderExecutionContext? Execution { get; set; }
    }

    private static HttpClient CreateSampleClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            MaxConnectionsPerServer = 1,
            UseCookies = false
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Allstarr-Provider-Diagnostic/1.0");
        return client;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string CacheState(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Cache", out var values)) return "unknown";
        var value = string.Join(", ", values);
        if (value.Contains("hit", StringComparison.OrdinalIgnoreCase)) return "hit";
        if (value.Contains("miss", StringComparison.OrdinalIgnoreCase)) return "miss";
        if (value.Contains("bypass", StringComparison.OrdinalIgnoreCase)) return "bypass";
        return "reported";
    }
}

public sealed class ProviderCtsDiagnosticResult
{
    public Guid MeasurementId { get; init; } = Guid.CreateVersion7();
    [JsonInclude]
    public bool DownloadVerified { get; internal set; }

    [JsonIgnore]
    public int StatusCode { get; init; }

    [JsonIgnore]
    public string? SelectedTrackId { get; internal set; }

    public bool Succeeded { get; init; }
    public string ProviderId { get; init; } = string.Empty;
    public Guid? ProviderAccountId { get; init; }
    public string? Stage { get; init; }
    public string? Error { get; init; }
    public string? ReasonCode => Succeeded ? null :
        Error is { } error && !error.Contains(' ') ? error : Stage;
    public string? SelectionMode { get; init; }
    public int? CorpusSize { get; init; }
    public string? RequestedQuality { get; init; }
    public double? RouteMilliseconds { get; init; }
    public double? PreparationMilliseconds { get; init; }
    public double? ResolveMilliseconds { get; init; }
    public double? HeadersMilliseconds { get; init; }
    public double? UpstreamHeadersMilliseconds { get; init; }
    public double? FirstByteMilliseconds { get; init; }
    public double? ClickToStreamMilliseconds { get; init; }
    public double? TotalMilliseconds { get; init; }
    public int SampleBytes { get; init; }
    public double? ThroughputKbps { get; init; }
    public int? UpstreamStatusCode { get; init; }
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    public string? ContentRange { get; init; }
    public string? RequestedRange { get; init; }
    public bool AcceptsByteRanges { get; init; }
    public string? CacheState { get; init; }
    public string? SampleSha256 { get; init; }
    public bool LeaseSupportsByteRanges { get; init; }
    public bool LeaseSupportsSeeking { get; init; }
    public string SeekRung => !Succeeded ? "unavailable" :
        LeaseSupportsByteRanges && UpstreamStatusCode == 206 &&
        ContentRangeHeaderValue.TryParse(ContentRange, out var range) && range.HasLength &&
        RangeHeaderValue.TryParse(RequestedRange, out var requested) &&
        StreamByteRanges.MatchesContentRange(range, ContentLength, requested)
            ? "byte-range" : RequestedRange == null && LeaseSupportsSeeking ? "provider-seek" : "sequential";
    public ProviderMediaFormat? Media { get; init; }
    public string? QualityDowngradeReason { get; init; }
    public string ProbeMode { get; init; } = "cold-connect";
    public int Bars { get; init; }
    public string? Quality { get; init; }
    public DateTimeOffset MeasuredAt { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public ProviderCtsDiagnosticLimit Limit { get; internal set; } = new();

    public static ProviderCtsDiagnosticResult Failure(
        int statusCode,
        string providerId,
        Guid? providerAccountId,
        string stage,
        string error,
        double? resolveMilliseconds = null,
        double? headersMilliseconds = null,
        int? retryAfterSeconds = null) => new()
        {
            StatusCode = statusCode,
            ProviderId = providerId,
            ProviderAccountId = providerAccountId,
            Stage = stage,
            Error = error,
            ResolveMilliseconds = Round(resolveMilliseconds),
            HeadersMilliseconds = Round(headersMilliseconds),
            MeasuredAt = DateTimeOffset.UtcNow,
            RetryAfterSeconds = retryAfterSeconds
        };

    public static ProviderCtsDiagnosticResult Success(
        string providerId,
        Guid? providerAccountId,
        string selectionMode,
        int? corpusSize,
        ProviderAudioQuality quality,
        double routeMilliseconds,
        double preparationMilliseconds,
        double resolveMilliseconds,
        double headersMilliseconds,
        double upstreamHeadersMilliseconds,
        double firstByteMilliseconds,
        double totalMilliseconds,
        int sampleBytes,
        double throughputKbps,
        int upstreamStatusCode,
        string? contentType,
        long? contentLength,
        string? contentRange,
        bool acceptsByteRanges,
        string cacheState,
        string sampleSha256,
        bool leaseSupportsByteRanges,
        bool leaseSupportsSeeking,
        ProviderMediaFormat media,
        string? qualityDowngradeReason,
        DateTimeOffset measuredAt,
        string? requestedRange = null)
    {
        var bars = ConnectivityQuality.Bars(firstByteMilliseconds, true, ConnectivityMetric.ClickToStream);
        return new ProviderCtsDiagnosticResult
        {
            StatusCode = StatusCodes.Status200OK,
            Succeeded = true,
            ProviderId = providerId,
            ProviderAccountId = providerAccountId,
            SelectionMode = selectionMode,
            CorpusSize = corpusSize,
            RequestedQuality = quality.ToString().ToLowerInvariant(),
            RouteMilliseconds = Round(routeMilliseconds),
            PreparationMilliseconds = Round(preparationMilliseconds),
            ResolveMilliseconds = Round(resolveMilliseconds),
            HeadersMilliseconds = Round(headersMilliseconds),
            UpstreamHeadersMilliseconds = Round(upstreamHeadersMilliseconds),
            FirstByteMilliseconds = Round(firstByteMilliseconds),
            ClickToStreamMilliseconds = Round(firstByteMilliseconds),
            TotalMilliseconds = Round(totalMilliseconds),
            SampleBytes = sampleBytes,
            ThroughputKbps = Round(throughputKbps),
            UpstreamStatusCode = upstreamStatusCode,
            ContentType = contentType,
            ContentLength = contentLength,
            ContentRange = contentRange,
            RequestedRange = requestedRange,
            AcceptsByteRanges = acceptsByteRanges,
            CacheState = cacheState,
            SampleSha256 = sampleSha256,
            LeaseSupportsByteRanges = leaseSupportsByteRanges,
            LeaseSupportsSeeking = leaseSupportsSeeking,
            Media = media,
            QualityDowngradeReason = qualityDowngradeReason,
            ProbeMode = "cold-connect",
            Bars = bars,
            Quality = ConnectivityQuality.Label(bars),
            MeasuredAt = measuredAt
        };
    }

    private static double? Round(double? value) => value.HasValue ? Math.Round(value.Value, 1) : null;
}

public sealed class ProviderCtsDiagnosticLimit
{
    public int TimeoutSeconds { get; init; } = 30;
    public int SampleBytes { get; init; } = 64 * 1024;
}
