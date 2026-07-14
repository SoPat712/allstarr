using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Services.Common;

namespace allstarr.Core.Extensions;

public abstract class ExtensionCapabilityAdapterBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ExtensionSandbox _sandbox;
    private readonly IProviderAccountSecretAccessor? _secrets;
    private readonly IReadOnlySet<string> _secretKeys;

    protected ExtensionCapabilityAdapterBase(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        ProviderCapabilityKind capability, IProviderAccountSecretAccessor? secrets)
    {
        _sandbox = sandbox;
        _secrets = secrets;
        ProviderId = manifest.Id;
        Hooks = manifest.Capabilities.Single(item => item.Kind == capability).Hooks.ToHashSet(StringComparer.Ordinal);
        _secretKeys = manifest.Permissions.Where(item => item.Kind == ExtensionPermissionKind.Secret)
            .Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
    }

    public string ProviderId { get; }
    protected IReadOnlySet<string> Hooks { get; }

    protected async Task<ProviderOutcome<T>> InvokeAsync<T>(ProviderExecutionContext context, string hook,
        object request, Func<JsonElement, T> map, bool requireAccount = false)
    {
        if (!Hooks.Contains(hook)) return Failure<T>(ProviderErrorKind.NotSupported);
        if (!context.ProviderId.Equals(ProviderId, StringComparison.Ordinal) || !context.Policy.AllowsProvider(ProviderId))
            return Failure<T>(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested) return Failure<T>(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow)) return Failure<T>(ProviderErrorKind.CapabilityUnavailable);
        if (requireAccount && context.Account == null) return Failure<T>(ProviderErrorKind.AccountNeedsConfiguration);

        try
        {
            ProviderOutcome<T> Run()
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (context.IsExpired(DateTimeOffset.UtcNow)) return Failure<T>(ProviderErrorKind.CapabilityUnavailable);
                var json = _sandbox.InvokeJson(hook, JsonSerializer.Serialize(request, JsonOptions));
                if (json == null) return Failure<T>(ProviderErrorKind.NotSupported);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
                return ProviderOutcome<T>.Success(map(document.RootElement));
            }

            if (_secretKeys.Count == 0) return Run();
            if (context.Account == null || _secrets == null) return Failure<T>(ProviderErrorKind.AccountNeedsConfiguration);
            return await _secrets.UseAsync(context.Account, value =>
            {
                using var scope = ExtensionInvocationSecretScope.Open(ParseSecrets(value));
                return Task.FromResult(Run());
            }, context.CancellationToken);
        }
        catch (OperationCanceledException) { return Failure<T>(ProviderErrorKind.Canceled); }
        catch { return Failure<T>(ProviderErrorKind.TransientFailure); }
    }

    private IReadOnlyDictionary<string, string> ParseSecrets(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in _secretKeys)
            if (document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                values[key] = value.GetString()!;
        return values;
    }

    protected static ProviderOutcome<T> Failure<T>(ProviderErrorKind kind) =>
        ProviderOutcome<T>.Failure(new ProviderError(kind));

    protected static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw new JsonException();
    protected static string? OptionalText(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    protected static bool Bool(JsonElement value, string name, bool fallback = false) =>
        value.TryGetProperty(name, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : fallback;
    protected static long? Long(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.TryGetInt64(out var result) ? result : null;
    protected static int? Int(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.TryGetInt32(out var result) ? result : null;
    protected static TEnum EnumValue<TEnum>(JsonElement value, string name) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(Text(value, name), true, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new JsonException();
    protected static ProviderMediaFormat Media(JsonElement value)
    {
        var media = value.GetProperty("media");
        return new ProviderMediaFormat(Text(media, "mimeType"), Text(media, "container"), Text(media, "codec"),
            Int(media, "bitrate"), Int(media, "sampleRate"), Int(media, "bitDepth"), Int(media, "channels"));
    }
}

public sealed class ExtensionStreamingCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderStreamingCapability
{
    public ExtensionStreamingCapabilityAdapter(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null) : base(sandbox, manifest, ProviderCapabilityKind.Streaming, secrets) { }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Streaming;
    public Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(ProviderExecutionContext context, ProviderStreamLeaseRequest request)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        return InvokeAsync(context, "getStreamLease", new { trackId = request.TrackId.Value, requestedQuality = request.RequestedQuality.ToString(), request.RangeStart }, value =>
            new ProviderStreamLease(Text(value, "leaseId"), new Uri(Text(value, "sourceUri"), UriKind.Absolute),
                value.GetProperty("expiresAt").GetDateTimeOffset(), Bool(value, "supportsByteRanges"), Bool(value, "supportsSeeking"),
                Media(value), EnumValue<ProviderStreamRetryBehavior>(value, "retryBehavior")));
    }
    public Task<ProviderOutcome<ProviderStreamProbeResult>> ProbeStreamAsync(ProviderExecutionContext context, ProviderStreamLeaseRequest request)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        return InvokeAsync(context, "probeStream", new { trackId = request.TrackId.Value, requestedQuality = request.RequestedQuality.ToString() }, value =>
            new ProviderStreamProbeResult(Bool(value, "available"), value.GetProperty("observedAt").GetDateTimeOffset(),
                value.TryGetProperty("media", out _) ? Media(value) : null));
    }
}

public sealed class ExtensionDownloadCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderDownloadCapability
{
    public ExtensionDownloadCapabilityAdapter(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null) : base(sandbox, manifest, ProviderCapabilityKind.Download, secrets) { }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Download;
    public Task<ProviderOutcome<ProviderDownloadAvailability>> CheckAvailabilityAsync(ProviderExecutionContext context, ProviderDownloadAvailabilityRequest request)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        return InvokeAsync(context, "checkAvailability", new { trackId = request.TrackId.Value, requestedQuality = request.RequestedQuality.ToString() }, value =>
            new ProviderDownloadAvailability(EnumValue<ProviderDownloadAvailabilityState>(value, "state"),
                value.TryGetProperty("availableQualities", out var qualities) ? qualities.EnumerateArray().Select(item => Enum.Parse<ProviderAudioQuality>(item.GetString()!, true)) : [], Long(value, "estimatedBytes")));
    }
    public Task<ProviderOutcome<ProviderDownloadedArtifact>> DownloadAsync(ProviderExecutionContext context, ProviderDownloadRequest request, IProgress<ProviderDownloadProgress>? progress = null)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        context.RequireIdempotencyKey();
        return InvokeAsync(context, "download", new { trackId = request.TrackId.Value, request.DurableJobId, workspaceId = request.Workspace.WorkspaceId, requestedQuality = request.RequestedQuality.ToString(), context.IdempotencyKey }, value =>
        {
            var artifact = new ProviderDownloadedArtifact(Text(value, "artifactId"), Text(value, "sha256"), value.GetProperty("sizeBytes").GetInt64(), Media(value), Bool(value, "verified"));
            progress?.Report(new ProviderDownloadProgress(ProviderDownloadProgressStage.Completed, artifact.SizeBytes, artifact.SizeBytes));
            return artifact;
        });
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
        return InvokeAsync(context, "fetchLyrics", new { request.CanonicalRecordingId, providerTrackId = request.ProviderTrackId.Value, request.AvailabilityOnly, preferredFormat = request.PreferredFormat?.ToString() }, value =>
            new ProviderLyricsResult(EnumValue<ProviderLyricsAvailabilityState>(value, "availability"), Text(value, "source"),
                value.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String ? Enum.Parse<ProviderLyricsFormat>(format.GetString()!, true) : null,
                OptionalText(value, "content"), OptionalText(value, "revision")));
    }
}

public sealed class ExtensionHealthCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderHealthProbeCapability
{
    public ExtensionHealthCapabilityAdapter(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null) : base(sandbox, manifest, ProviderCapabilityKind.Health, secrets) { }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Health;
    public Task<ProviderOutcome<ProviderHealthProbeResult>> ProbeAsync(ProviderExecutionContext context, ProviderHealthProbeRequest request)
    {
        var hook = request.TargetCapability switch { ProviderCapabilityKind.Metadata => "probeMetadata", ProviderCapabilityKind.Playlist => "probePlaylist", ProviderCapabilityKind.Streaming => "probeStreaming", ProviderCapabilityKind.Download => "probeDownload", _ => "" };
        var started = Stopwatch.GetTimestamp();
        return InvokeAsync(context, hook, new { targetCapability = request.TargetCapability.ToString(), request.NonDestructive }, value =>
            new ProviderHealthProbeResult(EnumValue<ProviderProbeStatus>(value, "status"), value.GetProperty("observedAt").GetDateTimeOffset(),
                value.TryGetProperty("latencyMs", out var latency) && latency.TryGetDouble(out var ms) ? TimeSpan.FromMilliseconds(ms) : Stopwatch.GetElapsedTime(started), OptionalText(value, "safeCode")));
    }
}

public sealed class ExtensionPlaylistCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderPlaylistCapability
{
    public ExtensionPlaylistCapabilityAdapter(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null) : base(sandbox, manifest, ProviderCapabilityKind.Playlist, secrets) { }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;
    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(ProviderExecutionContext context, ProviderUserPlaylistsRequest request) =>
        InvokeAsync(context, "getUserPlaylists", PageRequest(request.Page), value => MapPlaylistPage(value), true);
    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(ProviderExecutionContext context, ProviderPlaylistSearchRequest request) =>
        InvokeAsync(context, "searchPlaylists", new { request.Query, page = PageRequest(request.Page) }, value => MapPlaylistPage(value), true);
    public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(ProviderExecutionContext context, ProviderPlaylistTracksRequest request)
    {
        context.RequireResourceOwner(request.PlaylistId, ProviderResourceKind.Playlist);
        return InvokeAsync(context, "getPlaylistTracks", new { playlistId = request.PlaylistId.Value, page = PageRequest(request.Page), request.ExpectedRevision }, value =>
        {
            var summary = MapPlaylist(value.GetProperty("playlist"));
            var tracksValue = value.GetProperty("tracks");
            var tracks = tracksValue.GetProperty("items").EnumerateArray().Select(item => new ProviderPlaylistTrack(
                item.GetProperty("position").GetInt32(), new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Track, Text(item, "trackId")),
                item.TryGetProperty("canonicalRecordingId", out var canonical) && canonical.TryGetGuid(out var id) ? id : null)).ToArray();
            return new ProviderPlaylistTrackPage(summary, new ProviderPage<ProviderPlaylistTrack>(ProviderId, tracks,
                OptionalText(tracksValue, "nextCursor"), Bool(tracksValue, "isPartial"), OptionalText(tracksValue, "snapshotVersion")));
        }, true);
    }
    private static object PageRequest(ProviderPageRequest page) => new { page.Limit, page.Cursor };
    private ProviderPage<ProviderPlaylistSummary> MapPlaylistPage(JsonElement value) => new(ProviderId,
        value.GetProperty("items").EnumerateArray().Select(MapPlaylist), OptionalText(value, "nextCursor"), Bool(value, "isPartial"), OptionalText(value, "snapshotVersion"));
    private ProviderPlaylistSummary MapPlaylist(JsonElement value)
    {
        ProviderArtworkReference? artwork = null;
        if (Uri.TryCreate(OptionalText(value, "artworkUrl"), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) artwork = new ProviderArtworkReference(publicUri: uri);
        var owner = value.GetProperty("owner");
        return new ProviderPlaylistSummary(new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Playlist, Text(value, "id")), Text(value, "name"),
            new ProviderPlaylistOwner(Text(owner, "providerUserId"), OptionalText(owner, "displayName")), Text(value, "sourceRevision"),
            OptionalText(value, "description"), artwork, Int(value, "trackCount"), OptionalText(value, "sourceETag"));
    }
}
