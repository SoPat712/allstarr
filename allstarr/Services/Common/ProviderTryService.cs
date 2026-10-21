using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Net.Http.Headers;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Protocols;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

public sealed class ProviderTryService(IProviderRegistry providers, ProviderDiagnosticExecutionFactory executionFactory,
    ProviderTryOperations operations, ProviderStatusManager status, ProviderTryMediaStore tickets,
    ProviderCtsDiagnosticRunner cts, ProviderDownloadArtifactResolver artifacts,
    IDbContextFactory<AllstarrDbContext> database, EncryptedSecretStore secrets,
    ProtocolExecutionOptions? executionOptions = null, TimeProvider? timeProvider = null, CloudflareChallengeSolver? challengeSolver = null)
{
    private readonly BoundedOperationGate overall = new(3);
    private readonly ConcurrentDictionary<string, BoundedOperationGate> providerGates = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public static bool IsScrobbling(string provider) => provider is "lastfm" or "listenbrainz" or "multi-scrobbler" or "koito";

    public async Task<ProviderTryExecution> ExecuteAsync(ProviderActorContext actor, string provider,
        ProviderTryRequest request, string correlation, CancellationToken token)
    {
        using var overallLease = await overall.TryEnterAsync(token);
        if (overallLease == null) return new(429, null, "busy");
        using var providerLease = await providerGates.GetOrAdd(provider, _ => new(1)).TryEnterAsync(token);
        if (providerLease == null) return new(429, null, "busy");
        var timer = Stopwatch.StartNew();
        var timeout = request.Capability == "download" || request.Capability == "streaming" &&
            providers.TryGetCapability<IProviderStreamingCapability>(provider, ProviderCapabilityKind.Streaming, out var streaming) &&
            streaming is IProviderDownloadStreamingCapability
            ? (executionOptions ?? new ProtocolExecutionOptions()).GetDownloadDiagnosticTimeout() : TimeSpan.FromSeconds(30);
        if (request.Capability == "lyrics" && provider is "binilyrics" or "lyricsplus" && challengeSolver != null &&
            await challengeSolver.ConfiguredTimeoutAsync(token) is { } solveTimeout)
            timeout += solveTimeout * (provider == "binilyrics" ? 2 : 1);
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        ProviderExecutionContext? execution = null;
        long? accountRevision = null;
        ProviderTryResult result;
        try
        {
            if (request.Capability == "scrobbling")
            {
                var check = await CheckScrobblingAsync(actor, provider, request.AccountId, linked.Token);
                if (check.StatusCode != 200) return check;
                result = check.Result!;
                accountRevision = check.AccountRevision;
            }
            else
            {
                var capability = Enum.Parse<ProviderCapabilityKind>(request.Capability, true);
                var prepared = await executionFactory.CreateAsync(actor, provider, capability, request.AccountId,
                    correlation, clock.GetUtcNow().Add(timeout), linked.Token);
                if (prepared.Context == null) return new(prepared.StatusCode, null, prepared.ReasonCode);
                execution = prepared.Context;
                result = request.Capability switch
                {
                    "streaming" => await StreamAsync(actor, execution, request, correlation, linked.Token),
                    "download" => await DownloadAsync(actor, execution, request, linked.Token),
                    _ => await operations.ExecuteAsync(execution, request)
                };
            }
        }
        catch (ProviderTryBusyException) { return new(429, null, "busy"); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { result = ProviderTryResult.Failure("timeout"); }
        catch (Exception) { result = ProviderTryResult.Failure("provider_error"); }
        token.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested) result = ProviderTryResult.Failure("timeout");
        result.Provider = provider;
        result.Capability = request.Capability;
        result.Operation = request.Operation;
        result.AccountId = execution?.Account?.AccountId ?? (request.AccountId == Guid.Empty ? null : request.AccountId);
        result.ElapsedMs = Math.Round(timer.Elapsed.TotalMilliseconds, 1);
        if (!result.Succeeded && result.Message == null) result.Message = result.ReasonCode == "not_found"
            ? "No results were found." : "The provider operation failed.";
        status.RecordTryResult(provider, request.Capability, result.AccountId, execution?.Account?.Revision ?? accountRevision,
            result.Succeeded, (long)result.ElapsedMs, result.ReasonCode);
        return new(200, result, result.ReasonCode);
    }

    private async Task<ProviderTryResult> StreamAsync(ProviderActorContext actor, ProviderExecutionContext execution,
        ProviderTryRequest request, string correlation, CancellationToken token)
    {
        var quality = ProviderTryOperations.Quality(request.Quality);
        var measured = await cts.MeasureAsync(actor, execution.ProviderId, execution.Account?.AccountId, quality,
            correlation, request.TrackId, token);
        if (measured.StatusCode == StatusCodes.Status429TooManyRequests) throw new ProviderTryBusyException();
        if (!measured.Succeeded) return ProviderTryResult.Failure(measured.ReasonCode == "NotFound" ? "not_found" : measured.ReasonCode ?? "provider_error");
        var trackId = measured.SelectedTrackId;
        if (string.IsNullOrWhiteSpace(trackId)) return ProviderTryResult.Failure("not_found");
        var media = measured.Media!;
        var length = ContentRangeHeaderValue.TryParse(measured.ContentRange, out var contentRange) && contentRange.HasLength
            ? contentRange.Length : measured.UpstreamStatusCode == 200 ? measured.ContentLength : null;
        var ticket = tickets.CreateStream(actor.UserId!.Value, async (range, cancellationToken) =>
        {
            var prepared = await executionFactory.CreateAsync(actor, execution.ProviderId, ProviderCapabilityKind.Streaming,
                execution.Account?.AccountId, correlation, clock.GetUtcNow().AddMinutes(15), cancellationToken);
            if (prepared.Context == null) throw new HttpRequestException("Provider account is unavailable.");
            var capability = providers.GetRequiredCapability<IProviderStreamingCapability>(execution.ProviderId, ProviderCapabilityKind.Streaming);
            long? rangeStart = null;
            var parsedRange = StreamByteRanges.Parse(range);
            if (parsedRange?.Ranges.SingleOrDefault()?.From is { } start) rangeStart = start;
            var outcome = await capability.GetStreamLeaseAsync(prepared.Context,
                new(new(execution.ProviderId, ProviderResourceKind.Track, trackId), quality, rangeStart));
            if (!outcome.IsSuccess) throw new HttpRequestException("Provider media is unavailable.");
            var lease = outcome.RequireValue();
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(lease.ProtectedSourceUri.AbsoluteUri, out var uri, out _))
                throw new HttpRequestException("Provider media is unavailable.");
            using var outbound = new HttpRequestMessage(HttpMethod.Get, uri);
            // Like listener playback: only ranged leases get a Range header; others are read from the
            // start (e.g. Deezer's decrypting stream) and the media controller skips to the offset.
            if (lease.SupportsByteRanges) outbound.Headers.Range = parsedRange;
            if (lease.ProtectedResponseFactory != null) return await lease.ProtectedResponseFactory(outbound, cancellationToken);
            return await StreamClient.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }, measured.ContentType ?? media.MimeType, "preview." + media.Container, length);
        return new()
        {
            Succeeded = true,
            Media = Media("stream", ticket, null, length, media,
            measured.SeekRung, measured.ClickToStreamMilliseconds, null)
        };
    }

    private async Task<ProviderTryResult> DownloadAsync(ProviderActorContext actor, ProviderExecutionContext execution,
        ProviderTryRequest request, CancellationToken token)
    {
        var quality = ProviderTryOperations.Quality(request.Quality);
        var reservation = tickets.TryReserveDownload();
        if (reservation == null) return ProviderTryResult.Failure("temporary_storage_full", "Temporary media storage is full.");
        var jobId = Guid.NewGuid();
        ProviderTransientDownloadWorkspace? workspace = null;
        var transferred = false;
        var deferred = false;
        try
        {
            workspace = artifacts.CreateTransientWorkspace(new(actor.UserId, jobId, execution.ProviderId,
                execution.Account?.AccountId, "provider-try-" + jobId.ToString("N"))
            { MaximumBytes = reservation.MaximumBytes });
            var context = new ProviderExecutionContext(execution.Actor, execution.ProviderId, execution.Account, execution.Policy,
                execution.OperationId, execution.CorrelationId, execution.Deadline, execution.CancellationToken,
                "provider-try-" + jobId.ToString("N"));
            var track = new ProviderExternalResourceId(execution.ProviderId, ProviderResourceKind.Track, request.TrackId!);
            var downloading = providers.GetRequiredCapability<IProviderDownloadCapability>(execution.ProviderId, ProviderCapabilityKind.Download)
                .DownloadAsync(context, new(track, jobId, workspace.Reference, quality));
            ProviderOutcome<ProviderDownloadedArtifact> downloaded;
            try { downloaded = await downloading.WaitAsync(token); }
            catch (OperationCanceledException) when (!downloading.IsCompleted)
            {
                deferred = true;
                _ = CleanupDownloadAsync(downloading, workspace, reservation);
                throw;
            }
            if (!downloaded.IsSuccess) return ProviderTryOperations.Failure(downloaded.Error!);
            var artifact = await artifacts.ResolveTransientAsync(workspace, downloaded.RequireValue(), token);
            ProviderTrackMetadata? metadata = null;
            var descriptor = providers.GetRequired(execution.ProviderId);
            if (descriptor.Capabilities.Any(item => item.Capability == ProviderCapabilityKind.Metadata && item.Hooks.Contains("getTrack")) &&
                providers.TryGetCapability<IProviderMetadataCapability>(execution.ProviderId, ProviderCapabilityKind.Metadata, out var catalog))
            {
                var lookup = await catalog!.GetTrackAsync(context, new(track)).WaitAsync(token);
                if (lookup.IsSuccess) metadata = lookup.RequireValue();
            }
            var name = SafeFileName((metadata == null ? "Unknown artist" : string.Join(", ", metadata.Artists.Select(item => item.Name))) +
                " - " + (metadata?.Title ?? request.TrackId) + "." + artifact.Media.Container);
            token.ThrowIfCancellationRequested();
            var ticket = reservation.CreateFileTicket(actor.UserId!.Value, artifact.SourcePath, artifact.Media.MimeType, name,
                () => artifacts.DeleteTransientWorkspace(workspace));
            transferred = true;
            return new()
            {
                Succeeded = true,
                Media = Media("download", ticket, name, artifact.Length, artifact.Media,
                null, null, metadata?.Duration?.TotalSeconds)
            };
        }
        finally
        {
            if (!transferred && !deferred)
            {
                if (workspace == null) reservation.Dispose();
                else reservation.Abandon(() => artifacts.DeleteTransientWorkspace(workspace));
            }
        }
    }

    private async Task CleanupDownloadAsync(Task downloading, ProviderTransientDownloadWorkspace workspace,
        ProviderTryDownloadReservation reservation)
    {
        try { await downloading; }
        catch (Exception) { }
        reservation.Abandon(() => artifacts.DeleteTransientWorkspace(workspace));
    }

    private sealed class ProviderTryBusyException : Exception;

    private async Task<ProviderTryExecution> CheckScrobblingAsync(ProviderActorContext actor, string provider, Guid? accountId, CancellationToken token)
    {
        if (!accountId.HasValue || accountId == Guid.Empty) return new(400, null, "account_required");
        await using var db = await database.CreateDbContextAsync(token);
        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == accountId && item.ProviderId == provider && item.Enabled, token);
        if (account == null) return new(404, null, "account_unavailable");
        var values = new Dictionary<string, string>();
        if (account.SecretReferenceId is { } secretId)
        {
            using var lease = await secrets.OpenAsync(secretId, new SecretAccessContext(account.OwnerUserId,
                $"provider-account:{provider}:{account.Id:N}", AllowShared: account.OwnerUserId == null), token);
            using var document = JsonDocument.Parse(lease.Value);
            foreach (var item in document.RootElement.EnumerateObject())
                if (item.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    values[new string(item.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray())] =
                        item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString()! : item.Value.GetRawText();
        }
        var result = await status.TestManagedProviderCapabilityAsync(provider, ProviderCapabilities.Scrobbling, account.Id, values, token, account.Revision);
        return new(200, new() { Succeeded = result.Health == ProviderHealthState.Healthy, ReasonCode = result.ReasonCode }, result.ReasonCode)
        { AccountRevision = account.Revision };
    }

    private static object Media(string kind, ProviderTryMediaTicket ticket, string? fileName, long? size,
        ProviderMediaFormat format, string? seekMode, double? startMs, double? duration) => new
        {
            kind,
            url = ticket.Url,
            expiresAt = ticket.ExpiresAt,
            fileName,
            sizeBytes = size,
            contentType = format.MimeType,
            codec = format.Codec,
            sampleRateHz = format.SampleRate,
            bitDepth = format.BitDepth,
            bitrateKbps = format.Bitrate / 1000d,
            durationSeconds = duration,
            quality = DeliveredQuality(format),
            seekMode,
            startMs
        };

    private static string DeliveredQuality(ProviderMediaFormat format) => format.Codec.ToLowerInvariant() switch
    {
        "mp3" or "aac" or "opus" or "vorbis" or "mp2" or "wma" => "lossy",
        "flac" or "alac" or "pcm" or "lpcm" or "wav" or "aiff" or "ape" or "wavpack" =>
            format.BitDepth > 16 || format.SampleRate > 48000 ? "high-resolution" : "lossless",
        var codec when codec.StartsWith("pcm_", StringComparison.Ordinal) =>
            format.BitDepth > 16 || format.SampleRate > 48000 ? "high-resolution" : "lossless",
        _ => "unknown"
    };

    private static string SafeFileName(string name) => new string(name.Select(character =>
        char.IsControl(character) || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : character).ToArray())[..Math.Min(200, name.Length)];
    private static readonly HttpClient StreamClient = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = Timeout.InfiniteTimeSpan };
}

public sealed record ProviderTryExecution(int StatusCode, ProviderTryResult? Result, string? ReasonCode)
{
    public long? AccountRevision { get; init; }
}
