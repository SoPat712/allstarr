using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Providers.Spotify;
using allstarr.Services.Common;
using SkiaSharp;

namespace allstarr.Core.Extensions;

public abstract class ExtensionCapabilityAdapterBase : IProviderUrlCapability
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ExtensionSandbox _sandbox;
    private readonly IProviderAccountSecretAccessor? _secrets;
    private readonly IReadOnlySet<string> _secretKeys;
    private readonly IReadOnlySet<string> _accountValueKeys;
    private readonly ProviderCapabilityKind _capability;
    private readonly bool _accountRequired;
    private readonly bool _usesExpandedUrls;

    protected ExtensionCapabilityAdapterBase(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        ProviderCapabilityKind capability, IProviderAccountSecretAccessor? secrets)
    {
        _sandbox = sandbox;
        _secrets = secrets;
        ProviderId = manifest.Id;
        _capability = capability;
        var declaration = manifest.Capabilities.Single(item => item.Kind == capability);
        _accountRequired = declaration.AccountRequired;
        _usesExpandedUrls = manifest.Compatibility == SpotiFlacExtensionCompatibility.Marker;
        Hooks = declaration.Hooks.ToHashSet(StringComparer.Ordinal);
        var urlOwner = manifest.Capabilities.FirstOrDefault(item => item.Kind == ProviderCapabilityKind.Metadata)
            ?? manifest.Capabilities.FirstOrDefault(item => item.Kind == ProviderCapabilityKind.Playlist)
            ?? manifest.Capabilities.First();
        UrlPatterns = urlOwner.Kind == capability ? manifest.UrlPatterns ?? [] : [];
        _secretKeys = manifest.Permissions.Where(item => item.Kind == ExtensionPermissionKind.Secret)
            .Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
        _accountValueKeys = _secretKeys
            .Concat(manifest.Settings?.Select(item => item.Key) ?? [])
            .ToHashSet(StringComparer.Ordinal);
    }

    public string ProviderId { get; }
    ProviderCapabilityKind IProviderCapability.Capability => _capability;
    public IReadOnlyList<string> UrlPatterns { get; }
    protected IReadOnlySet<string> Hooks { get; }

    public async Task<ProviderOutcome<ProviderExternalResourceId>> ResolveUrlAsync(
        ProviderExecutionContext context, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 4096)
            return Failure<ProviderExternalResourceId>(ProviderErrorKind.PermanentFailure);
        var outcome = await InvokeAsync(context, "handleUrl", new { url }, value => value.Clone(), _accountRequired);
        if (!outcome.IsSuccess) return ProviderOutcome<ProviderExternalResourceId>.Failure(outcome.Error!);
        var result = outcome.RequireValue();
        if (result.ValueKind == JsonValueKind.Null)
            return Failure<ProviderExternalResourceId>(ProviderErrorKind.NotFound);
        if (result.ValueKind != JsonValueKind.Object)
            return Failure<ProviderExternalResourceId>(ProviderErrorKind.PermanentFailure);
        if (result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            return Failure<ProviderExternalResourceId>(ProviderErrorKind.NotFound);
        var kind = OptionalText(result, "type")?.ToLowerInvariant() switch
        {
            "track" => ProviderResourceKind.Track,
            "album" => ProviderResourceKind.Album,
            "artist" => ProviderResourceKind.Artist,
            "playlist" => ProviderResourceKind.Playlist,
            _ => (ProviderResourceKind?)null
        };
        if (kind == null) return Failure<ProviderExternalResourceId>(ProviderErrorKind.NotSupported);
        var id = UrlResourceId(result);
        var resourceName = kind.Value.ToString().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id) && result.TryGetProperty(resourceName, out var embedded) && embedded.ValueKind == JsonValueKind.Object)
            id = UrlResourceId(embedded);
        if (string.IsNullOrWhiteSpace(id) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var separators = uri.Scheme is "http" or "https" ? new[] { '/' } : new[] { '/', ':' };
            var parts = uri.AbsolutePath.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && (uri.Host.Equals(resourceName, StringComparison.OrdinalIgnoreCase) ||
                parts.SkipLast(1).Any(part => part.Equals(resourceName, StringComparison.OrdinalIgnoreCase) ||
                    part.Equals(resourceName + "s", StringComparison.OrdinalIgnoreCase))))
                id = Uri.UnescapeDataString(parts[^1]);
        }
        if (string.IsNullOrWhiteSpace(id) && _usesExpandedUrls && kind is ProviderResourceKind.Playlist or ProviderResourceKind.Album &&
            result.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            id = url;
        if (string.IsNullOrWhiteSpace(id)) return Failure<ProviderExternalResourceId>(ProviderErrorKind.PermanentFailure);
        try
        {
            return ProviderOutcome<ProviderExternalResourceId>.Success(new(ProviderId, kind.Value, id));
        }
        catch (ArgumentException)
        {
            return Failure<ProviderExternalResourceId>(ProviderErrorKind.PermanentFailure);
        }
    }

    private static string? UrlResourceId(JsonElement value) => value.TryGetProperty("id", out var id)
        ? id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null
        }
        : null;

    protected async Task<ProviderOutcome<T>> InvokeAsync<T>(ProviderExecutionContext context, string hook,
        object request, Func<JsonElement, T> map, bool requireAccount = false,
        Func<IDisposable>? openInvocationScope = null)
    {
        if (!Hooks.Contains(hook) && !(hook == "handleUrl" && UrlPatterns.Count > 0))
            return Failure<T>(ProviderErrorKind.NotSupported);
        if (!context.ProviderId.Equals(ProviderId, StringComparison.Ordinal) || !context.Policy.AllowsProvider(ProviderId))
            return Failure<T>(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested) return Failure<T>(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow)) return Failure<T>(ProviderErrorKind.CapabilityUnavailable);
        if ((requireAccount || _sandbox.HasSignedSession) && context.Account == null)
            return Failure<T>(ProviderErrorKind.AccountNeedsConfiguration);

        try
        {
            if (!await _sandbox.ValidateSignedSessionAccountAsync(context.Account, context.CancellationToken))
                return Failure<T>(ProviderErrorKind.Forbidden);
            ProviderOutcome<T> Run()
            {
                using var sessionScope = _sandbox.HasSignedSession && context.Account != null
                    ? _sandbox.OpenSignedSessionAccount(context.Account) : null;
                using var invocationScope = openInvocationScope?.Invoke();
                context.CancellationToken.ThrowIfCancellationRequested();
                if (context.IsExpired(DateTimeOffset.UtcNow)) return Failure<T>(ProviderErrorKind.CapabilityUnavailable);
                var cacheContext = context.Account == null ? null : JsonSerializer.Serialize(new { context.Account.AccountId, context.Account.Revision });
                var json = _sandbox.InvokeJson(hook, JsonSerializer.Serialize(request, JsonOptions), context.CancellationToken, cacheContext);
                if (json == null) return Failure<T>(ProviderErrorKind.NotSupported);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
                return ProviderOutcome<T>.Success(map(document.RootElement));
            }

            // Hooks and host calls are synchronous; keep them off the request's search fan-out.
            if (_accountValueKeys.Count == 0 || context.Account == null)
                return await Task.Run(Run, context.CancellationToken);
            if (_secrets == null) return Failure<T>(ProviderErrorKind.AccountNeedsConfiguration);
            return await _secrets.UseAsync(context.Account, async value =>
            {
                using var scope = ExtensionInvocationSecretScope.Open(ParseAccountValues(value));
                return await Task.Run(Run, context.CancellationToken);
            }, context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        { return Failure<T>(ProviderErrorKind.Canceled); }
        catch (Exception exception) when (hook == "download")
        {
            _sandbox.LogDownloadFailure(exception);
            return ProviderOutcome<T>.Failure(DownloadFailure(exception));
        }
        catch { return Failure<T>(ProviderErrorKind.TransientFailure); }
    }

    private static ProviderError DownloadFailure(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains(ExtensionDownloadPostProcessor.ToolsUnavailableMessage, StringComparison.Ordinal))
            return ProviderError.DownloadMediaToolsUnavailable();
        if (message.Contains(ExtensionDownloadPostProcessor.ConversionFailureMessage, StringComparison.Ordinal))
            return ProviderError.DownloadContainerConversionFailed();
        if (exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } ||
            Regex.IsMatch(message, @"\brate[ _-]*limit(?:ed)?(?:\b|_)|\b(?:http(?:\s+(?:status|error))?|status(?:\s+code)?)\s*[:=]?\s*429\b|^\s*429\b|\btoo many requests\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var retry = Regex.Match(message, @"\bretry_after_seconds=(\d{1,4})\b", RegexOptions.CultureInvariant);
            return new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(retry.Success
                ? Math.Clamp(int.Parse(retry.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1, 3600) : 30));
        }
        if (Regex.IsMatch(message, @"\bverification[ _-]*required\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return ProviderError.DownloadVerificationRequired();
        if (exception is TimeoutException or OperationCanceledException ||
            exception is HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } ||
            Regex.IsMatch(message, @"\b(?:http(?:\s+(?:status|error))?|status(?:\s+code)?)\s*[:=]?\s*5\d{2}\b|\bservice[ _-]+unavailable\b|\bbad[ _-]+gateway\b|\b(?:timeout|timed[ _-]+out)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return ProviderError.ServiceUnavailable();
        if (exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone } ||
            Regex.IsMatch(message, @"\btrack[ _-]+(?:not[ _-]+found|unavailable)\b|\bnot[ _-]+found\b|\bhttp\s*[:=]?\s*(?:404|410)\b|\bno[ _-]+longer[ _-]+playable\b|\b(?:track|song)\b.*\bremoved\b|\b(?:geo|region)(?:graphically)?[ _-]*(?:blocked|restricted)\b|\b(?:not[ _-]+available|unavailable|blocked|restricted)\b.*\b(?:region|country|territory)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return new(ProviderErrorKind.NotFound);
        if (Regex.IsMatch(message,
                @"\bdecryption failed\b|\b(?:preview|snippet)\b|\b(?:not|does[ _-]+not)\b.*\bfull[ _-]+playable[ _-]+stream\b|\bno\b.*\b(?:supported|playable|full)\b.*\bstreams?\b.*\b(?:available|found)\b|\bduration\b.*\b(?:mismatch|does[ _-]+not[ _-]+match)\b|\bencrypted\b.*\bstreams?\b|\bunsupported[ _-]+stream\b|\bstreams?\b.*\bnot[ _-]+supported\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return new(ProviderErrorKind.IncompatibleMedia);
        return new(ProviderErrorKind.TransientFailure);
    }

    private IReadOnlyDictionary<string, string> ParseAccountValues(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in _accountValueKeys)
            if (document.RootElement.TryGetProperty(key, out var value) &&
                value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array))
                values[key] = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        return values;
    }

    protected static ProviderOutcome<T> Failure<T>(ProviderErrorKind kind) =>
        ProviderOutcome<T>.Failure(new ProviderError(kind));

    private protected Task<ExtensionBoundedHttpPayload> FetchBytesAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        _sandbox.FetchBytesAsync(uri, maximumBytes, cancellationToken);

    protected static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw new JsonException();
    protected static string? OptionalText(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    protected static bool Bool(JsonElement value, string name, bool fallback = false) =>
        value.TryGetProperty(name, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : fallback;
    protected static long? Long(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var result) ? result : null;
    protected static int? Int(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var result) ? result : null;
    protected static double Double(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var result)
            ? result : throw new JsonException();
    protected static TEnum EnumValue<TEnum>(JsonElement value, string name) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(Text(value, name), true, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new JsonException();
    protected Uri NetworkUri(JsonElement value, string name)
    {
        var uri = new Uri(Text(value, name), UriKind.Absolute);
        return _sandbox.IsNetworkAllowed(uri)
            ? uri
            : throw new UnauthorizedAccessException("Extension stream origin is not approved.");
    }
    protected static ProviderMediaFormat Media(JsonElement value)
    {
        var media = value.GetProperty("media");
        return new ProviderMediaFormat(Text(media, "mimeType"), Text(media, "container"), Text(media, "codec"),
            Int(media, "bitrate"), Int(media, "sampleRate"), Int(media, "bitDepth"), Int(media, "channels"));
    }
}

public sealed class ExtensionStreamingCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderStreamingCapability
{
    private readonly bool accountRequired;

    public ExtensionStreamingCapabilityAdapter(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null) : base(sandbox, manifest, ProviderCapabilityKind.Streaming, secrets)
    {
        accountRequired = manifest.Capabilities.Single(item => item.Kind == ProviderCapabilityKind.Streaming).AccountRequired;
    }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Streaming;
    public Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(ProviderExecutionContext context, ProviderStreamLeaseRequest request)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        return InvokeAsync(context, "getStreamLease", new { trackId = request.TrackId.Value, requestedQuality = request.RequestedQuality.ToString(), request.RangeStart }, value =>
        {
            foreach (var marker in new[] { "decryption", "decryption_key", "encrypted", "needs_decryption", "encryption_scheme" })
                if (value.TryGetProperty(marker, out var encryption) && encryption.ValueKind is not
                    (JsonValueKind.Null or JsonValueKind.False) && encryption.ToString().Length > 0)
                    throw new InvalidDataException(ExtensionDownloadPostProcessor.FailureMessage);
            return new ProviderStreamLease(Text(value, "leaseId"), NetworkUri(value, "sourceUri"),
                value.GetProperty("expiresAt").GetDateTimeOffset(), Bool(value, "supportsByteRanges"), Bool(value, "supportsSeeking"),
                Media(value), EnumValue<ProviderStreamRetryBehavior>(value, "retryBehavior"),
                qualityDowngradeReason: OptionalText(value, "qualityDowngradeReason"));
        },
            requireAccount: accountRequired);
    }
    public Task<ProviderOutcome<ProviderStreamProbeResult>> ProbeStreamAsync(ProviderExecutionContext context, ProviderStreamLeaseRequest request)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        return InvokeAsync(context, "probeStream", new { trackId = request.TrackId.Value, requestedQuality = request.RequestedQuality.ToString() }, value =>
            new ProviderStreamProbeResult(Bool(value, "available"), value.GetProperty("observedAt").GetDateTimeOffset(),
                value.TryGetProperty("media", out _) ? Media(value) : null),
            requireAccount: accountRequired);
    }
}

public sealed class ExtensionDownloadCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderDownloadCapability
{
    private readonly ProviderDownloadArtifactResolver? artifacts;
    private readonly long maximumArtifactBytes;
    private readonly bool accountRequired;

    public ExtensionDownloadCapabilityAdapter(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null,
        ProviderDownloadArtifactResolver? artifacts = null,
        ProviderDownloadWorkspaceOptions? options = null) : base(sandbox, manifest, ProviderCapabilityKind.Download, secrets)
    {
        this.artifacts = artifacts;
        maximumArtifactBytes = options?.MaximumArtifactBytes ?? 0;
        accountRequired = manifest.Capabilities.Single(item => item.Kind == ProviderCapabilityKind.Download).AccountRequired;
    }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Download;
    public Task<ProviderOutcome<ProviderDownloadAvailability>> CheckAvailabilityAsync(ProviderExecutionContext context, ProviderDownloadAvailabilityRequest request) =>
        CheckAvailabilityAsync(context, request, prepareForStreaming: false);

    internal Task<ProviderOutcome<ProviderDownloadAvailability>> CheckAvailabilityAsync(
        ProviderExecutionContext context, ProviderDownloadAvailabilityRequest request, bool prepareForStreaming)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        object hookRequest = prepareForStreaming
            ? new { trackId = request.TrackId.Value, requestedQuality = request.RequestedQuality.ToString(), prepareForStreaming = true }
            : new { trackId = request.TrackId.Value, requestedQuality = request.RequestedQuality.ToString() };
        return InvokeAsync(context, "checkAvailability", hookRequest, value =>
            new ProviderDownloadAvailability(EnumValue<ProviderDownloadAvailabilityState>(value, "state"),
                value.TryGetProperty("availableQualities", out var qualities) ? qualities.EnumerateArray().Select(item => Enum.Parse<ProviderAudioQuality>(item.GetString()!, true)) : [], Long(value, "estimatedBytes")),
            requireAccount: accountRequired);
    }
    internal Task<ProviderOutcome<ProviderRecordingAvailability>> ResolveRecordingAsync(
        ProviderExecutionContext context, ProviderRecordingAvailabilityRequest recording, ProviderAudioQuality quality)
    {
        string Id(string provider) => recording.ProviderIds.GetValueOrDefault(provider) ?? "";
        return InvokeAsync(context, "checkAvailability", new
        {
            trackId = recording.SampleId,
            requestedQuality = quality.ToString(),
            resolveRecording = true,
            track = new
            {
                id = recording.SampleId,
                name = recording.Title,
                artists = recording.ArtistNames,
                isrc = recording.Isrc,
                duration_ms = recording.DurationMilliseconds ?? 0,
                spotify_id = Id("spotify"),
                deezer_id = Id("deezer"),
                tidal_id = Id("tidal"),
                qobuz_id = Id("qobuz")
            }
        }, value => new ProviderRecordingAvailability(
            EnumValue<ProviderDownloadAvailabilityState>(value, "state") == ProviderDownloadAvailabilityState.Available &&
            OptionalText(value, "trackId") is { Length: > 0 } id
                ? new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Track, id) : null,
            OptionalText(value, "reason")), requireAccount: accountRequired);
    }

    public Task<ProviderOutcome<ProviderDownloadedArtifact>> DownloadAsync(ProviderExecutionContext context, ProviderDownloadRequest request, IProgress<ProviderDownloadProgress>? progress = null) =>
        DownloadAsync(context, request, prepareForStreaming: false, progress);

    internal Task<ProviderOutcome<ProviderDownloadedArtifact>> DownloadAsync(
        ProviderExecutionContext context, ProviderDownloadRequest request, bool prepareForStreaming, IProgress<ProviderDownloadProgress>? progress = null,
        ExtensionProgressiveTransfer? progressive = null)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        context.RequireIdempotencyKey();
        if (artifacts == null || maximumArtifactBytes < 1)
            return Task.FromResult(Failure<ProviderDownloadedArtifact>(ProviderErrorKind.CapabilityUnavailable));
        ExtensionArtifactInvocationScope? artifactScope = null;
        return InvokeAsync(context, "download", new { trackId = request.TrackId.Value, request.DurableJobId, workspaceId = request.Workspace.WorkspaceId, requestedQuality = request.RequestedQuality.ToString(), prepareForStreaming, context.IdempotencyKey }, value =>
        {
            var artifact = new ProviderDownloadedArtifact(Text(value, "artifactId"), Text(value, "sha256"), value.GetProperty("sizeBytes").GetInt64(), Media(value), Bool(value, "verified"),
                qualityDowngradeReason: OptionalText(value, "qualityDowngradeReason"));
            var written = artifactScope?.Result ?? throw new JsonException("The extension did not write an artifact through the host broker.");
            if (!artifact.ArtifactId.Equals(written.ArtifactId, StringComparison.Ordinal) ||
                !artifact.Sha256.Equals(written.Sha256, StringComparison.Ordinal) ||
                artifact.SizeBytes != written.SizeBytes)
                throw new JsonException("The extension artifact claim does not match the host-written artifact.");
            progress?.Report(new ProviderDownloadProgress(ProviderDownloadProgressStage.Completed, artifact.SizeBytes, artifact.SizeBytes));
            return artifact;
        }, requireAccount: accountRequired, openInvocationScope: () =>
        {
            artifactScope = ExtensionArtifactInvocationScope.Open(
                artifacts, request.Workspace, request.DurableJobId, ProviderId, maximumArtifactBytes, context.CancellationToken);
            artifactScope.SetProgressive(progressive);
            return artifactScope;
        });
    }
}

public sealed partial class ExtensionDownloadStreamingCapabilityAdapter :
    IProviderDownloadStreamingCapability, IAsyncDisposable
{
    private readonly ExtensionDownloadCapabilityAdapter download;
    private readonly ProviderDownloadArtifactResolver artifacts;
    private readonly bool requiresPreparedContext;

    public ExtensionDownloadStreamingCapabilityAdapter(
        ExtensionSandbox sandbox,
        ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets,
        ProviderDownloadArtifactResolver artifacts,
        ProviderDownloadWorkspaceOptions options)
    {
        download = new ExtensionDownloadCapabilityAdapter(sandbox, manifest, secrets, artifacts, options);
        this.artifacts = artifacts;
        clock = sandbox.Clock;
        deezerSpeculation = sandbox.DeezerSpeculation;
        allowsProgressive = sandbox.AllowsProgressiveDownload && manifest.Compatibility == SpotiFlacExtensionCompatibility.Marker;
        // The compatibility shim prepares and resolves the track within its download hook.
        requiresPreparedContext = manifest.Compatibility != SpotiFlacExtensionCompatibility.Marker &&
            manifest.RequiredRuntimeFeatures?.Contains("preparedContext@1", StringComparer.Ordinal) == true;
        ProviderId = manifest.Id;
        SupportsRecordingAvailability = manifest.Compatibility == SpotiFlacExtensionCompatibility.Marker &&
            sandbox.HasSpotiFlacHook("checkAvailability") &&
            manifest.Capabilities.Any(item => item.Kind == ProviderCapabilityKind.Download && item.Hooks.Contains("checkAvailability"));
    }

    public string ProviderId { get; }
    public bool SupportsRecordingAvailability { get; }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Streaming;

    public Task<ProviderOutcome<ProviderRecordingAvailability>> ResolveRecordingAsync(
        ProviderExecutionContext context, ProviderRecordingAvailabilityRequest recording, ProviderAudioQuality quality)
    {
        if (!SupportsRecordingAvailability)
            return Task.FromResult(Failure<ProviderRecordingAvailability>(ProviderErrorKind.NotSupported));
        if (!context.Policy.AllowManagedDownloads)
            return Task.FromResult(Failure<ProviderRecordingAvailability>(ProviderErrorKind.Forbidden));
        return download.ResolveRecordingAsync(context, recording, quality);
    }

    private async Task<ProviderOutcome<ProviderStreamLease>> PrepareStreamAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request,
        DownloadSession session)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        if (!context.Policy.AllowManagedDownloads)
            return Failure<ProviderStreamLease>(ProviderErrorKind.Forbidden);

        // Availability only prepares the track here. Some extensions judge availability more strictly than their
        // download does (SoundCloud), so playback always tries the download and lets that decide.
        if (requiresPreparedContext)
        {
            var availability = await download.CheckAvailabilityAsync(context,
                new ProviderDownloadAvailabilityRequest(request.TrackId, request.RequestedQuality), prepareForStreaming: true);
            if (!availability.IsSuccess && availability.Error!.Kind is not
                (ProviderErrorKind.NotSupported or ProviderErrorKind.TransientFailure))
                return ProviderOutcome<ProviderStreamLease>.Failure(availability.Error);
        }

        var jobId = Guid.CreateVersion7();
        var idempotencyKey = $"stream-{jobId:N}";
        var workspace = artifacts.CreateTransientWorkspace(new(
            context.Actor.EffectiveUserId,
            jobId,
            ProviderId,
            context.Account?.AccountId,
            idempotencyKey));

        session.CleanupWorkspace = () => artifacts.DeleteTransientWorkspace(workspace);
        try
        {
            var downloadContext = new ProviderExecutionContext(
                context.Actor,
                context.ProviderId,
                context.Account,
                context.Policy,
                "extension-download-stream",
                context.CorrelationId,
                context.Deadline,
                context.CancellationToken,
                idempotencyKey);
            var outcome = await download.DownloadAsync(
                downloadContext,
                new ProviderDownloadRequest(request.TrackId, jobId, workspace.Reference, request.RequestedQuality),
                prepareForStreaming: true, progressive: session.Transfer);
            if (!outcome.IsSuccess)
            {
                return ProviderOutcome<ProviderStreamLease>.Failure(outcome.Error!);
            }

            var output = outcome.RequireValue();
            var artifact = await artifacts.ResolveTransientAsync(workspace, output, context.CancellationToken);
            var media = output.Media;
            Task<HttpResponseMessage> OpenAsync(HttpRequestMessage message, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var range = message.Method == HttpMethod.Head ? null : message.Headers.Range;
                long start = 0, end = artifact.Length - 1;
                HttpResponseMessage response;
                if (range != null && !StreamByteRanges.TryResolve(range, artifact.Length, out start, out end))
                {
                    var validRange = StreamByteRanges.IsSingleByteRange(range);
                    response = Response(validRange ? HttpStatusCode.RequestedRangeNotSatisfiable : HttpStatusCode.BadRequest,
                        new ByteArrayContent([]), 0, media.MimeType);
                    if (validRange) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(artifact.Length);
                }
                else if (message.Method == HttpMethod.Head)
                {
                    response = Response(HttpStatusCode.OK, new ByteArrayContent([]), artifact.Length, media.MimeType);
                }
                else
                {
                    var stream = new FileStream(artifact.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    stream.Position = start;
                    var length = end - start + 1;
                    response = Response(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent,
                        new StreamContent(new BoundedReadStream(stream, length)), length, media.MimeType);
                    if (range != null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, artifact.Length);
                }
                response.Headers.AcceptRanges.Add("bytes");
                return Task.FromResult(response);
            }

            return ProviderOutcome<ProviderStreamLease>.Success(new ProviderStreamLease(
                $"download-{jobId:N}",
                new Uri($"https://extension-download.invalid/{jobId:N}"),
                DateTimeOffset.UtcNow.AddMinutes(5),
                supportsByteRanges: true,
                supportsSeeking: true,
                media,
                ProviderStreamRetryBehavior.DoNotRetry,
                OpenAsync,
                qualityDowngradeReason: output.QualityDowngradeReason));
        }
        catch (OperationCanceledException)
        {
            return Failure<ProviderStreamLease>(ProviderErrorKind.Canceled);
        }
        catch
        {
            return Failure<ProviderStreamLease>(ProviderErrorKind.TransientFailure);
        }
    }

    public async Task<ProviderOutcome<ProviderStreamProbeResult>> ProbeStreamAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request)
    {
        var result = await download.CheckAvailabilityAsync(
            context,
            new ProviderDownloadAvailabilityRequest(request.TrackId, request.RequestedQuality), prepareForStreaming: true);
        return result.IsSuccess
            ? ProviderOutcome<ProviderStreamProbeResult>.Success(new(
                result.RequireValue().State == ProviderDownloadAvailabilityState.Available,
                DateTimeOffset.UtcNow))
            : ProviderOutcome<ProviderStreamProbeResult>.Failure(result.Error!);
    }

    private static ProviderOutcome<T> Failure<T>(ProviderErrorKind kind) =>
        ProviderOutcome<T>.Failure(new ProviderError(kind));

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        HttpContent content,
        long length,
        string mimeType)
    {
        content.Headers.ContentLength = length;
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mimeType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private sealed class BoundedReadStream(Stream inner, long length) : Stream
    {
        private int disposed;
        private readonly long contentLength = length;
        private long remaining = length;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => contentLength;
        public override long Position { get => contentLength - remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken);
            remaining -= read;
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                await inner.DisposeAsync();
            }
            GC.SuppressFinalize(this);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class ExtensionLyricsCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderLyricsCapability
{
    public ExtensionLyricsCapabilityAdapter(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null) : base(sandbox, manifest, ProviderCapabilityKind.Lyrics, secrets) { }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Lyrics;
    public Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(ProviderExecutionContext context, ProviderLyricsRequest request)
    {
        context.RequireResourceOwner(request.ProviderTrackId, ProviderResourceKind.Track);
        return InvokeAsync(context, "fetchLyrics", new
        {
            request.CanonicalRecordingId,
            providerTrackId = request.ProviderTrackId.Value,
            request.AvailabilityOnly,
            preferredFormat = request.PreferredFormat?.ToString(),
            request.TrackTitle,
            request.ArtistNames,
            request.AlbumTitle,
            request.DurationSeconds
        }, value =>
            new ProviderLyricsResult(EnumValue<ProviderLyricsAvailabilityState>(value, "availability"), Text(value, "source"),
                value.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String ? Enum.Parse<ProviderLyricsFormat>(format.GetString()!, true) : null,
                OptionalText(value, "content"), OptionalText(value, "revision")));
    }
}

public sealed class ExtensionHealthCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderHealthProbeCapability
{
    private readonly ProviderDiagnosticSamples _diagnosticSamples = new();
    public ExtensionHealthCapabilityAdapter(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null) : base(sandbox, manifest, ProviderCapabilityKind.Health, secrets) { }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Health;
    public async Task<ProviderOutcome<ProviderHealthProbeResult>> ProbeAsync(ProviderExecutionContext context, ProviderHealthProbeRequest request)
    {
        var hook = request.TargetCapability switch { ProviderCapabilityKind.Metadata => "probeMetadata", ProviderCapabilityKind.Playlist => "probePlaylist", ProviderCapabilityKind.Streaming => "probeStreaming", ProviderCapabilityKind.Download => "probeDownload", ProviderCapabilityKind.Intelligence => "probeIntelligence", _ => "" };
        var started = Stopwatch.GetTimestamp();
        string? observedSampleId = null;
        bool catalogSample = false;
        ProviderOutcome<ProviderHealthProbeResult>? direct = null;
        var failedSamples = new HashSet<string>(StringComparer.Ordinal);
        string[] observedFailures = [];
        Task<ProviderOutcome<ProviderHealthProbeResult>> Invoke(ProviderExecutionContext execution, ProviderDiagnosticSample? sample) =>
            InvokeAsync(execution, hook, new
            {
                targetCapability = request.TargetCapability.ToString(),
                request.NonDestructive,
                sampleQuery = sample?.Query.Trim(),
                sampleIsrc = sample?.Isrc
            }, value =>
            {
                observedSampleId = OptionalText(value, "sampleId");
                observedFailures = value.TryGetProperty("failureSampleIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                    ? ids.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Take(8).ToArray() : [];
                catalogSample = value.TryGetProperty("catalogSample", out var marker) && marker.ValueKind == JsonValueKind.True;
                return new ProviderHealthProbeResult(EnumValue<ProviderProbeStatus>(value, "status"), value.GetProperty("observedAt").GetDateTimeOffset(),
                    value.TryGetProperty("latencyMs", out var latency) && latency.ValueKind == JsonValueKind.Number &&
                    latency.TryGetDouble(out var ms) && double.IsFinite(ms) && ms >= 0 && ms < TimeSpan.MaxValue.TotalMilliseconds / 2
                        ? TimeSpan.FromMilliseconds(ms) : Stopwatch.GetElapsedTime(started), OptionalText(value, "safeCode"));
            });
        if (!Hooks.Contains(hook) || request.TargetCapability is not (ProviderCapabilityKind.Metadata or ProviderCapabilityKind.Playlist))
            return await Invoke(context, null);
        var result = await _diagnosticSamples.CheckAsync($"{context.ProviderId}:{context.Account?.AccountId}:{hook}",
            ProviderDiagnosticSamples.Recordings(context.ProviderId), async (sample, token) =>
            {
                var outcome = await Invoke(ProviderDiagnosticSamples.WithCancellation(context, token), sample);
                if (!outcome.IsSuccess)
                {
                    var failure = ProviderDiagnosticSamples.FromError(outcome.Error!);
                    return failure.SampleUnavailable ? failure : CountFailure(failure.ReasonCode!, ["query:" + sample.Query]);
                }
                var value = outcome.RequireValue();
                if (!catalogSample)
                {
                    direct = outcome;
                    return new(value.Status == ProviderProbeStatus.Healthy, value.SafeCode, Stop: true);
                }
                if (value.Status == ProviderProbeStatus.Healthy) return new(true);
                if (value.SafeCode == "probe-limit-exceeded") return new(false, value.SafeCode, Stop: true);
                if (value.SafeCode is "not-found" or "metadata-not-found" or "playlist-not-found" or "playlist-empty" or "sample-unavailable")
                    return ProviderSampleCheck.Missing;
                return CountFailure(value.SafeCode ?? "service_unavailable", observedFailures.Length > 0
                    ? observedFailures.Select(id => "native:" + id) : [observedSampleId == null ? "query:" + sample.Query : "native:" + observedSampleId]);
            }, DiscoverAsync, context.CancellationToken);
        if (direct != null) return direct;
        return ProviderOutcome<ProviderHealthProbeResult>.Success(new(result.Success ? ProviderProbeStatus.Healthy : ProviderProbeStatus.Unavailable,
            DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(started), result.SampleUnavailable ? "sample-unavailable" : result.ReasonCode));

        ProviderSampleCheck CountFailure(string reason, IEnumerable<string> ids)
        {
            failedSamples.UnionWith(ids);
            return failedSamples.Count >= ProviderDiagnosticSamples.SourceFailureThreshold
                ? new(false, reason, Stop: true) : ProviderSampleCheck.Missing;
        }

        async IAsyncEnumerable<ProviderDiagnosticSample> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            foreach (var query in ProviderDiagnosticSamples.DiscoveryQueries(context.ProviderId))
            {
                token.ThrowIfCancellationRequested();
                yield return new(query, "", null, 200);
            }
        }
    }

}

public sealed class ExtensionIntelligenceCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderIntelligenceCapability
{
    private readonly bool accountRequired;

    public ExtensionIntelligenceCapabilityAdapter(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null)
        : base(sandbox, manifest, ProviderCapabilityKind.Intelligence, secrets) =>
        accountRequired = manifest.Capabilities.Single(item => item.Kind == ProviderCapabilityKind.Intelligence).AccountRequired;

    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Intelligence;

    public Task<ProviderOutcome<ProviderAnalysisProgress>> StartAnalysisAsync(
        ProviderExecutionContext context, bool rebuild = false)
    {
        context.RequireIdempotencyKey();
        return InvokeAsync(context, "startAnalysis", new { rebuild, context.IdempotencyKey }, MapProgress, accountRequired);
    }

    public Task<ProviderOutcome<ProviderAnalysisProgress>> GetAnalysisProgressAsync(
        ProviderExecutionContext context, string jobId)
    {
        ProviderContractValidation.RequiredText(jobId, nameof(jobId), 300);
        return InvokeAsync(context, "getAnalysisProgress", new { jobId }, MapProgress, accountRequired);
    }

    public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceCluster>>> GetClustersAsync(
        ProviderExecutionContext context, int limit = 50)
    {
        Limit(limit, 100);
        return InvokeAsync(context, "getClusters", new { limit }, value =>
            Bounded(value.GetProperty("items"), limit, item => new ProviderIntelligenceCluster(
                    Required(item, "id", 300), Required(item, "name", 300),
                    Bounded(item.GetProperty("tracks"), 200, MapTrack, "Cluster track")),
                "Cluster"), accountRequired);
    }

    public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> RecommendAsync(
        ProviderExecutionContext context, IReadOnlyList<string> seedTrackIds, int limit)
    {
        Limit(limit, 200);
        if (seedTrackIds.Count > 100) throw new ArgumentOutOfRangeException(nameof(seedTrackIds));
        foreach (var trackId in seedTrackIds) ProviderContractValidation.RequiredText(trackId, nameof(seedTrackIds), 500);
        return InvokeAsync(context, "recommend", new { seedTrackIds, limit }, value =>
            BoundedTracks(value, limit), accountRequired);
    }

    public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> SearchAsync(
        ProviderExecutionContext context, string query, bool includeLyrics, int limit)
    {
        Limit(limit, 200);
        ProviderContractValidation.RequiredText(query, nameof(query), 500);
        return InvokeAsync(context, "search", new { query, includeLyrics, limit }, value =>
            BoundedTracks(value, limit), accountRequired);
    }

    public Task<ProviderOutcome<ProviderIntelligencePath>> FindPathAsync(
        ProviderExecutionContext context, string startTrackId, string endTrackId, int limit)
    {
        ProviderContractValidation.RequiredText(startTrackId, nameof(startTrackId), 500);
        ProviderContractValidation.RequiredText(endTrackId, nameof(endTrackId), 500);
        if (startTrackId.Equals(endTrackId, StringComparison.Ordinal))
            throw new ArgumentException("Path endpoints must be different.", nameof(endTrackId));
        if (limit is < 2 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        return InvokeAsync(context, "findPath", new { startTrackId, endTrackId, limit }, value =>
            new ProviderIntelligencePath(BoundedTracks(value, limit), NonNegativeDouble(value, "totalDistance")),
            accountRequired);
    }

    public Task<ProviderOutcome<IReadOnlyList<ProviderIntelligenceTrack>>> BlendAsync(
        ProviderExecutionContext context, IReadOnlyList<string> positiveSeedTrackIds,
        IReadOnlyList<string> negativeSeedTrackIds, int limit)
    {
        Limit(limit, 200);
        ValidateSeeds(positiveSeedTrackIds, nameof(positiveSeedTrackIds), 1, 50);
        ValidateSeeds(negativeSeedTrackIds, nameof(negativeSeedTrackIds), 0, 50);
        if (positiveSeedTrackIds.Intersect(negativeSeedTrackIds, StringComparer.Ordinal).Any())
            throw new ArgumentException("Positive and negative seeds cannot overlap.", nameof(negativeSeedTrackIds));
        return InvokeAsync(context, "blend", new { positiveSeedTrackIds, negativeSeedTrackIds, limit },
            value => BoundedTracks(value, limit), accountRequired);
    }

    public Task<ProviderOutcome<ProviderIntelligenceMapPage>> GetMapAsync(
        ProviderExecutionContext context, ProviderPageRequest page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return InvokeAsync(context, "getMap", new { page.Limit, page.Cursor }, value =>
        {
            var items = Bounded(value.GetProperty("items"), page.Limit,
                item => new ProviderIntelligenceMapPoint(
                    Required(item, "trackId", 500), Required(item, "title", 500),
                    Required(item, "artist", 500), Coordinate(item, "x"), Coordinate(item, "y"),
                    Optional(item, "album", 500), Optional(item, "clusterId", 300)), "Map point");
            return new ProviderIntelligenceMapPage(items, Required(value, "projection", 100),
                ProviderContractValidation.OptionalText(OptionalText(value, "nextCursor"), "nextCursor", 2000),
                Bool(value, "isPartial"),
                ProviderContractValidation.OptionalText(OptionalText(value, "snapshotVersion"), "snapshotVersion", 300));
        }, accountRequired);
    }

    public Task<ProviderOutcome<bool>> DisconnectAsync(ProviderExecutionContext context)
    {
        context.RequireIdempotencyKey();
        return InvokeAsync(context, "disconnect", new { context.IdempotencyKey }, value =>
            Bool(value, "disconnected"), accountRequired);
    }

    private static ProviderAnalysisProgress MapProgress(JsonElement value)
    {
        var completed = NonNegative(value, "completed");
        var total = NonNegative(value, "total");
        if (completed > total) throw new JsonException();
        return new(Required(value, "jobId", 300), EnumValue<ProviderAnalysisState>(value, "state"),
            completed, total, Optional(value, "safeCode", 300));
    }

    private static ProviderIntelligenceTrack MapTrack(JsonElement value) =>
        new(Required(value, "trackId", 500), Required(value, "title", 500), Required(value, "artist", 500),
            Math.Clamp(Double(value, "score"), 0, 1), Optional(value, "album", 500),
            Optional(value, "clusterId", 300), Optional(value, "explanation", 2000));

    private static IReadOnlyList<ProviderIntelligenceTrack> BoundedTracks(JsonElement value, int limit)
        => Bounded(value.GetProperty("items"), limit, MapTrack, "Track result");

    private static IReadOnlyList<T> Bounded<T>(JsonElement value, int limit,
        Func<JsonElement, T> map, string label)
    {
        var items = value.EnumerateArray().Take(limit + 1).Select(map).ToArray();
        return items.Length <= limit ? items : throw new JsonException($"{label} exceeds the requested limit.");
    }

    private static string Required(JsonElement value, string name, int maximumLength) =>
        ProviderContractValidation.RequiredText(Text(value, name), name, maximumLength);

    private static string? Optional(JsonElement value, string name, int maximumLength) =>
        ProviderContractValidation.OptionalText(OptionalText(value, name), name, maximumLength);

    private static int NonNegative(JsonElement value, string name)
    {
        var result = value.GetProperty(name).GetInt32();
        return result >= 0 ? result : throw new JsonException();
    }

    private static double NonNegativeDouble(JsonElement value, string name)
    {
        var result = Double(value, name);
        return double.IsFinite(result) && result >= 0 ? result : throw new JsonException();
    }

    private static double Coordinate(JsonElement value, string name)
    {
        var result = Double(value, name);
        return double.IsFinite(result) && result is >= -1 and <= 1 ? result : throw new JsonException();
    }

    private static void ValidateSeeds(IReadOnlyList<string> seeds, string name, int minimum, int maximum)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        if (seeds.Count < minimum || seeds.Count > maximum ||
            seeds.Distinct(StringComparer.Ordinal).Count() != seeds.Count)
            throw new ArgumentOutOfRangeException(name);
        foreach (var seed in seeds) ProviderContractValidation.RequiredText(seed, name, 500);
    }

    private static void Limit(int value, int maximum)
    {
        if (value is < 1 || value > maximum) throw new ArgumentOutOfRangeException(nameof(value));
    }
}

public sealed class ExtensionPlaylistCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderPlaylistCapability
{
    private const int MaximumArtworkPixels = 16_000_000;
    private readonly bool accountRequired;

    public ExtensionPlaylistCapabilityAdapter(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null) : base(sandbox, manifest, ProviderCapabilityKind.Playlist, secrets)
    {
        accountRequired = manifest.Capabilities.Single(item => item.Kind == ProviderCapabilityKind.Playlist).AccountRequired;
    }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;
    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(ProviderExecutionContext context, ProviderUserPlaylistsRequest request) =>
        InvokeAsync(context, "getUserPlaylists", request.SourceUserId == null
            ? PageRequest(request.Page)
            : new { request.Page.Limit, request.Page.Cursor, request.SourceUserId }, value => MapPlaylistPage(value), accountRequired);
    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(ProviderExecutionContext context, ProviderPlaylistSearchRequest request) =>
        InvokeAsync(context, "searchPlaylists", new { request.Query, page = PageRequest(request.Page) }, value => MapPlaylistPage(value), accountRequired);
    public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(ProviderExecutionContext context, ProviderPlaylistTracksRequest request)
    {
        context.RequireResourceOwner(request.PlaylistId, ProviderResourceKind.Playlist);
        return InvokeAsync(context, "getPlaylistTracks", new { playlistId = request.PlaylistId.Value, page = PageRequest(request.Page), request.ExpectedRevision }, value =>
        {
            var summary = MapPlaylist(value.GetProperty("playlist"));
            var tracksValue = value.GetProperty("tracks");
            var tracks = tracksValue.GetProperty("items").EnumerateArray().Select(MapTrack).ToArray();
            return new ProviderPlaylistTrackPage(summary, new ProviderPage<ProviderPlaylistTrack>(ProviderId, tracks,
                OptionalText(tracksValue, "nextCursor"), Bool(tracksValue, "isPartial"), OptionalText(tracksValue, "snapshotVersion")));
        }, accountRequired);
    }
    public async Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
        ProviderExecutionContext context,
        ProviderPlaylistArtworkRequest request)
    {
        var resource = request.Artwork.ResourceId;
        if (resource == null || resource.ProviderId != ProviderId || resource.ResourceKind != ProviderResourceKind.Playlist)
            return Failure<ProviderPlaylistArtwork>(ProviderErrorKind.Forbidden);
        ProviderOutcome<ExtensionArtworkLocation> location;
        if (!Hooks.Contains("resolveArtwork") && Hooks.Contains("getPlaylistTracks"))
        {
            var page = await GetPlaylistTracksAsync(context, new(resource, new(1)));
            if (!page.IsSuccess) return ProviderOutcome<ProviderPlaylistArtwork>.Failure(page.Error!);
            var summary = page.RequireValue().Playlist;
            if (summary.Artwork?.PublicUri is not { Scheme: "https" } uri)
                return Failure<ProviderPlaylistArtwork>(ProviderErrorKind.NotFound);
            location = ProviderOutcome<ExtensionArtworkLocation>.Success(new(uri, summary.SourceRevision));
        }
        else location = await InvokeAsync(
            context,
            "resolveArtwork",
            new { playlistId = resource.Value, revision = request.Artwork.Revision, request.MaximumBytes },
            value =>
            {
                var revision = ProviderContractValidation.RequiredText(Text(value, "revision"), "revision", 300);
                if (!Uri.TryCreate(Text(value, "artworkUrl"), UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps)
                    throw new JsonException();
                return new ExtensionArtworkLocation(uri, revision);
            },
            accountRequired);
        if (!location.IsSuccess)
            return ProviderOutcome<ProviderPlaylistArtwork>.Failure(location.Error!);
        var target = location.RequireValue();
        if (request.Artwork.Revision != null &&
            !request.Artwork.Revision.Equals(target.Revision, StringComparison.Ordinal))
            return Failure<ProviderPlaylistArtwork>(ProviderErrorKind.PermanentFailure);

        try
        {
            var payload = await FetchBytesAsync(
                target.Uri,
                request.MaximumBytes,
                context.CancellationToken);
            if (payload.Bytes.Length == 0)
                return Failure<ProviderPlaylistArtwork>(ProviderErrorKind.NotFound);
            if (payload.ContentType is not ("image/jpeg" or "image/png" or "image/webp") ||
                !IsValidArtwork(payload.Bytes, payload.ContentType))
                return Failure<ProviderPlaylistArtwork>(ProviderErrorKind.PermanentFailure);
            return ProviderOutcome<ProviderPlaylistArtwork>.Success(
                new ProviderPlaylistArtwork(payload.Bytes, payload.ContentType));
        }
        catch (OperationCanceledException)
        {
            return Failure<ProviderPlaylistArtwork>(ProviderErrorKind.Canceled);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<ProviderPlaylistArtwork>(ProviderErrorKind.Forbidden);
        }
        catch (InvalidDataException)
        {
            return Failure<ProviderPlaylistArtwork>(ProviderErrorKind.PermanentFailure);
        }
        catch (HttpRequestException exception)
        {
            return Failure<ProviderPlaylistArtwork>(exception.StatusCode == HttpStatusCode.NotFound
                ? ProviderErrorKind.NotFound
                : exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? ProviderErrorKind.Unauthorized
                    : ProviderErrorKind.TransientFailure);
        }
    }

    internal static bool IsAllowedArtworkDimensions(int width, int height) =>
        width > 0 && height > 0 && (long)width * height <= MaximumArtworkPixels;

    private static bool IsValidArtwork(byte[] bytes, string contentType)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return codec != null &&
               IsAllowedArtworkDimensions(codec.Info.Width, codec.Info.Height) &&
               (codec.EncodedFormat, contentType) is
               (SKEncodedImageFormat.Jpeg, "image/jpeg") or
               (SKEncodedImageFormat.Png, "image/png") or
               (SKEncodedImageFormat.Webp, "image/webp");
    }
    private static object PageRequest(ProviderPageRequest page) => new { page.Limit, page.Cursor };
    private ProviderPlaylistTrack MapTrack(JsonElement item)
    {
        var trackId = new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Track, Text(item, "trackId"));
        var canonical = item.TryGetProperty("canonicalRecordingId", out var canonicalValue) &&
                        canonicalValue.ValueKind == JsonValueKind.String && canonicalValue.TryGetGuid(out var canonicalId)
            ? canonicalId
            : (Guid?)null;
        var value = item.TryGetProperty("metadata", out var metadata) ? metadata : item;
        var title = OptionalText(value, "title");
        if (title == null || !value.TryGetProperty("artists", out var artistsValue) ||
            artistsValue.ValueKind != JsonValueKind.Array)
            return new(item.GetProperty("position").GetInt32(), trackId, canonical);
        var artists = artistsValue.EnumerateArray()
            .Select(artist => artist.ValueKind == JsonValueKind.String
                ? artist.GetString()
                : OptionalText(artist, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new ProviderArtistCredit(name!))
            .ToArray();
        if (artists.Length == 0) return new(item.GetProperty("position").GetInt32(), trackId, canonical);
        var albumTitle = OptionalText(value, "albumTitle");
        var albumId = OptionalText(value, "albumId");
        var duration = Long(value, "durationMs");
        return new(item.GetProperty("position").GetInt32(), trackId, canonical,
            new ProviderTrackMetadata(
                trackId,
                title,
                artists,
                albumId == null ? null : new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Album, albumId),
                albumTitle,
                duration > 0 ? TimeSpan.FromMilliseconds(duration.Value) : null,
                OptionalText(value, "isrc"),
                value.TryGetProperty("isExplicit", out var explicitValue) &&
                explicitValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? explicitValue.GetBoolean()
                    : null,
                bitrate: Int(value, "bitrate")));
    }
    private ProviderPage<ProviderPlaylistSummary> MapPlaylistPage(JsonElement value) => new(ProviderId,
        value.GetProperty("items").EnumerateArray().Select(MapPlaylist), OptionalText(value, "nextCursor"), Bool(value, "isPartial"), OptionalText(value, "snapshotVersion"));
    private ProviderPlaylistSummary MapPlaylist(JsonElement value)
    {
        var resource = new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Playlist, Text(value, "id"));
        var artworkRevision = OptionalText(value, "artworkRevision");
        ProviderArtworkReference? artwork = null;
        if (Hooks.Contains("resolveArtwork") && Bool(value, "hasArtwork", artworkRevision != null))
            artwork = new ProviderArtworkReference(resource, revision: artworkRevision ?? Text(value, "sourceRevision"));
        else if (Uri.TryCreate(OptionalText(value, "artworkUrl"), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            artwork = new ProviderArtworkReference(publicUri: uri, revision: artworkRevision);
        var owner = value.GetProperty("owner");
        return new ProviderPlaylistSummary(resource, Text(value, "name"),
            new ProviderPlaylistOwner(Text(owner, "providerUserId"), OptionalText(owner, "displayName")), Text(value, "sourceRevision"),
            OptionalText(value, "description"), artwork, Int(value, "trackCount"), OptionalText(value, "sourceETag"));
    }

    private sealed record ExtensionArtworkLocation(Uri Uri, string Revision);
}
