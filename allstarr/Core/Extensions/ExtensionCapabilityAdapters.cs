using System.Diagnostics;
using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Providers.Spotify;
using allstarr.Services.Common;
using SkiaSharp;

namespace allstarr.Core.Extensions;

public abstract class ExtensionCapabilityAdapterBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ExtensionSandbox _sandbox;
    private readonly IProviderAccountSecretAccessor? _secrets;
    private readonly IReadOnlySet<string> _secretKeys;
    private readonly IReadOnlySet<string> _accountValueKeys;

    protected ExtensionCapabilityAdapterBase(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        ProviderCapabilityKind capability, IProviderAccountSecretAccessor? secrets)
    {
        _sandbox = sandbox;
        _secrets = secrets;
        ProviderId = manifest.Id;
        Hooks = manifest.Capabilities.Single(item => item.Kind == capability).Hooks.ToHashSet(StringComparer.Ordinal);
        _secretKeys = manifest.Permissions.Where(item => item.Kind == ExtensionPermissionKind.Secret)
            .Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
        _accountValueKeys = _secretKeys
            .Concat(manifest.Settings?.Select(item => item.Key) ?? [])
            .ToHashSet(StringComparer.Ordinal);
    }

    public string ProviderId { get; }
    protected IReadOnlySet<string> Hooks { get; }

    protected async Task<ProviderOutcome<T>> InvokeAsync<T>(ProviderExecutionContext context, string hook,
        object request, Func<JsonElement, T> map, bool requireAccount = false,
        Func<IDisposable>? openInvocationScope = null)
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
                using var invocationScope = openInvocationScope?.Invoke();
                context.CancellationToken.ThrowIfCancellationRequested();
                if (context.IsExpired(DateTimeOffset.UtcNow)) return Failure<T>(ProviderErrorKind.CapabilityUnavailable);
                var json = _sandbox.InvokeJson(hook, JsonSerializer.Serialize(request, JsonOptions));
                if (json == null) return Failure<T>(ProviderErrorKind.NotSupported);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
                return ProviderOutcome<T>.Success(map(document.RootElement));
            }

            if (_accountValueKeys.Count == 0 || context.Account == null) return Run();
            if (_secrets == null) return Failure<T>(ProviderErrorKind.AccountNeedsConfiguration);
            return await _secrets.UseAsync(context.Account, value =>
            {
                using var scope = ExtensionInvocationSecretScope.Open(ParseAccountValues(value));
                return Task.FromResult(Run());
            }, context.CancellationToken);
        }
        catch (OperationCanceledException) { return Failure<T>(ProviderErrorKind.Canceled); }
        catch { return Failure<T>(ProviderErrorKind.TransientFailure); }
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
            new ProviderStreamLease(Text(value, "leaseId"), NetworkUri(value, "sourceUri"),
                value.GetProperty("expiresAt").GetDateTimeOffset(), Bool(value, "supportsByteRanges"), Bool(value, "supportsSeeking"),
                Media(value), EnumValue<ProviderStreamRetryBehavior>(value, "retryBehavior"),
                qualityDowngradeReason: OptionalText(value, "qualityDowngradeReason")),
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
    public Task<ProviderOutcome<ProviderDownloadAvailability>> CheckAvailabilityAsync(ProviderExecutionContext context, ProviderDownloadAvailabilityRequest request)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        return InvokeAsync(context, "checkAvailability", new { trackId = request.TrackId.Value, requestedQuality = request.RequestedQuality.ToString() }, value =>
            new ProviderDownloadAvailability(EnumValue<ProviderDownloadAvailabilityState>(value, "state"),
                value.TryGetProperty("availableQualities", out var qualities) ? qualities.EnumerateArray().Select(item => Enum.Parse<ProviderAudioQuality>(item.GetString()!, true)) : [], Long(value, "estimatedBytes")),
            requireAccount: accountRequired);
    }
    public Task<ProviderOutcome<ProviderDownloadedArtifact>> DownloadAsync(ProviderExecutionContext context, ProviderDownloadRequest request, IProgress<ProviderDownloadProgress>? progress = null)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        context.RequireIdempotencyKey();
        if (artifacts == null || maximumArtifactBytes < 1)
            return Task.FromResult(Failure<ProviderDownloadedArtifact>(ProviderErrorKind.CapabilityUnavailable));
        ExtensionArtifactInvocationScope? artifactScope = null;
        return InvokeAsync(context, "download", new { trackId = request.TrackId.Value, request.DurableJobId, workspaceId = request.Workspace.WorkspaceId, requestedQuality = request.RequestedQuality.ToString(), context.IdempotencyKey }, value =>
        {
            var artifact = new ProviderDownloadedArtifact(Text(value, "artifactId"), Text(value, "sha256"), value.GetProperty("sizeBytes").GetInt64(), Media(value), Bool(value, "verified"));
            var written = artifactScope?.Result ?? throw new JsonException("The extension did not write an artifact through the host broker.");
            if (!artifact.ArtifactId.Equals(written.ArtifactId, StringComparison.Ordinal) ||
                !artifact.Sha256.Equals(written.Sha256, StringComparison.Ordinal) ||
                artifact.SizeBytes != written.SizeBytes)
                throw new JsonException("The extension artifact claim does not match the host-written artifact.");
            progress?.Report(new ProviderDownloadProgress(ProviderDownloadProgressStage.Completed, artifact.SizeBytes, artifact.SizeBytes));
            return artifact;
        }, requireAccount: accountRequired, openInvocationScope: () => artifactScope = ExtensionArtifactInvocationScope.Open(
            artifacts, request.Workspace, request.DurableJobId, ProviderId, maximumArtifactBytes,
            context.CancellationToken));
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
    public ExtensionHealthCapabilityAdapter(ExtensionSandbox sandbox, ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null) : base(sandbox, manifest, ProviderCapabilityKind.Health, secrets) { }
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Health;
    public Task<ProviderOutcome<ProviderHealthProbeResult>> ProbeAsync(ProviderExecutionContext context, ProviderHealthProbeRequest request)
    {
        var hook = request.TargetCapability switch { ProviderCapabilityKind.Metadata => "probeMetadata", ProviderCapabilityKind.Playlist => "probePlaylist", ProviderCapabilityKind.Streaming => "probeStreaming", ProviderCapabilityKind.Download => "probeDownload", ProviderCapabilityKind.Intelligence => "probeIntelligence", _ => "" };
        var started = Stopwatch.GetTimestamp();
        return InvokeAsync(context, hook, new { targetCapability = request.TargetCapability.ToString(), request.NonDestructive }, value =>
            new ProviderHealthProbeResult(EnumValue<ProviderProbeStatus>(value, "status"), value.GetProperty("observedAt").GetDateTimeOffset(),
                value.TryGetProperty("latencyMs", out var latency) && latency.TryGetDouble(out var ms) ? TimeSpan.FromMilliseconds(ms) : Stopwatch.GetElapsedTime(started), OptionalText(value, "safeCode")));
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
            var tracks = tracksValue.GetProperty("items").EnumerateArray().Select(MapTrack).ToArray();
            return new ProviderPlaylistTrackPage(summary, new ProviderPage<ProviderPlaylistTrack>(ProviderId, tracks,
                OptionalText(tracksValue, "nextCursor"), Bool(tracksValue, "isPartial"), OptionalText(tracksValue, "snapshotVersion")));
        }, true);
    }
    public async Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
        ProviderExecutionContext context,
        ProviderPlaylistArtworkRequest request)
    {
        var resource = request.Artwork.ResourceId;
        if (resource == null || resource.ProviderId != ProviderId || resource.ResourceKind != ProviderResourceKind.Playlist)
            return Failure<ProviderPlaylistArtwork>(ProviderErrorKind.Forbidden);
        var location = await InvokeAsync(
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
            true);
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
                        canonicalValue.TryGetGuid(out var canonicalId)
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
