using System.Net;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Providers.AppleDownload;

public sealed class AppleDownloadCapabilityAdapter : IProviderDownloadCapability
{
    public const string StableProviderId = "apple-download";
    public const string HttpClientName = "AppleDownloadCapability";

    private readonly HttpClient http;
    private readonly AppleDownloadSettings settings;
    private readonly IAppleDownloadEndpointDiscovery discovery;
    private readonly ProviderDownloadArtifactResolver artifacts;
    private readonly long maximumArtifactBytes;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AppleDownloadCapabilityAdapter(
        IHttpClientFactory clients,
        IOptions<AppleDownloadSettings> settings,
        IAppleDownloadEndpointDiscovery discovery,
        ProviderDownloadArtifactResolver artifacts,
        ProviderDownloadWorkspaceOptions workspaceOptions)
        : this(clients.CreateClient(HttpClientName), settings.Value, discovery, artifacts,
            workspaceOptions.MaximumArtifactBytes)
    {
    }

    public AppleDownloadCapabilityAdapter(
        HttpClient http,
        AppleDownloadSettings settings,
        IAppleDownloadEndpointDiscovery discovery,
        ProviderDownloadArtifactResolver artifacts,
        long maximumArtifactBytes)
    {
        this.http = http;
        this.settings = settings;
        this.discovery = discovery;
        this.artifacts = artifacts;
        this.maximumArtifactBytes = maximumArtifactBytes > 0
            ? maximumArtifactBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumArtifactBytes));
    }

    public string ProviderId => StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Download;

    public async Task<ProviderOutcome<ProviderDownloadAvailability>> CheckAvailabilityAsync(
        ProviderExecutionContext context,
        ProviderDownloadAvailabilityRequest request)
    {
        var error = Validate(context, request.TrackId);
        if (error != null) return ProviderOutcome<ProviderDownloadAvailability>.Failure(error);
        try
        {
            var snapshot = await discovery.DiscoverAsync(context.CancellationToken);
            var state = Availability(snapshot);
            var qualities = state == ProviderDownloadAvailabilityState.Available
                ? Enum.GetValues<ProviderAudioQuality>()
                : [];
            return ProviderOutcome<ProviderDownloadAvailability>.Success(new(state, qualities));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderDownloadAvailability>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch
        {
            return ProviderOutcome<ProviderDownloadAvailability>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    public async Task<ProviderOutcome<ProviderDownloadedArtifact>> DownloadAsync(
        ProviderExecutionContext context,
        ProviderDownloadRequest request,
        IProgress<ProviderDownloadProgress>? progress = null)
    {
        var error = Validate(context, request.TrackId);
        if (error != null) return ProviderOutcome<ProviderDownloadedArtifact>.Failure(error);
        if (!context.Policy.AllowManagedDownloads)
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.Forbidden));
        if (request.DurableJobId == Guid.Empty)
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.PermanentFailure));

        try
        {
            progress?.Report(new(ProviderDownloadProgressStage.Resolving, 0));
            var snapshot = await discovery.DiscoverAsync(context.CancellationToken);
            var availability = Availability(snapshot);
            if (availability != ProviderDownloadAvailabilityState.Available)
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ErrorFor(snapshot.State)));
            if (!OutboundRequestGuard.TryCreateConfiguredServiceUri(settings.BaseUrl, out var baseUri, out _))
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));

            var quality = Quality(request.RequestedQuality, settings.Quality);
            var endpoint = new Uri(baseUri!, $"api/download/{Uri.EscapeDataString(request.TrackId.Value)}?quality={Uri.EscapeDataString(quality)}");
            using var response = await http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest ||
                response.RequestMessage?.RequestUri is not { } responseUri || !SameOrigin(baseUri!, responseUri))
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.PermanentFailure));
            if (!response.IsSuccessStatusCode)
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(ErrorFor(response));

            var media = Media(response.Content.Headers.ContentType?.MediaType, quality);
            if (media == null)
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.IncompatibleMedia));
            var expectedBytes = response.Content.Headers.ContentLength;
            if (expectedBytes is <= 0 || expectedBytes > maximumArtifactBytes)
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.IncompatibleMedia));

            var artifactId = ArtifactId(request.TrackId.Value, media.Container);
            await using var content = await response.Content.ReadAsStreamAsync(context.CancellationToken);
            progress?.Report(new(ProviderDownloadProgressStage.Transferring, 0, expectedBytes));
            var written = await artifacts.WriteAsync(new(
                request.Workspace,
                request.DurableJobId,
                StableProviderId,
                artifactId,
                content,
                maximumArtifactBytes)
            {
                ExpectedBytes = expectedBytes,
                Progress = (complete, total) => progress?.Report(
                    new ProviderDownloadProgress(ProviderDownloadProgressStage.Transferring, complete, total))
            }, context.CancellationToken);
            progress?.Report(new(ProviderDownloadProgressStage.Verifying, written.SizeBytes, written.SizeBytes));
            var output = new ProviderDownloadedArtifact(
                written.ArtifactId,
                written.Sha256,
                written.SizeBytes,
                media,
                verified: true);
            progress?.Report(new(ProviderDownloadProgressStage.Completed, written.SizeBytes, written.SizeBytes));
            return ProviderOutcome<ProviderDownloadedArtifact>.Success(output);
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (InvalidDataException)
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.IncompatibleMedia));
        }
        catch (UnauthorizedAccessException)
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.Forbidden));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.TransientFailure));
        }
        catch
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.PermanentFailure));
        }
    }

    public static ProviderRegistration CreateRegistration(
        AppleDownloadCapabilityAdapter adapter,
        IProviderLyricsCapability lyrics,
        AppleDownloadStreamingCapabilityAdapter streaming,
        IProviderMetadataCapability metadata) => new(
        new ProviderDescriptor(
            StableProviderId,
            "Apple Music – GAMDL",
            "Optional operator-managed Apple audio downloads through a discovered compatible gateway.",
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "apple-download-gateway-v1",
            capabilities:
            [
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Metadata,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "1",
                    hooks:
                    [
                        "searchTracks", "getTrack", "lookupByIsrc", "searchAlbums", "getAlbum",
                        "searchArtists", "getArtist", "getArtistAlbums", "getArtistTracks"
                    ]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Streaming,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "1",
                    hooks: ["getStreamLease", "probeStream"]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Download,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "1",
                    hooks: ["checkAvailability", "download"]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Lyrics,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None,
                    compatibilityVersion: "1",
                    hooks: ["fetchLyrics"])
            ],
            permissions: new ProviderPermissionDescriptor()),
        [adapter, lyrics, streaming, metadata]);

    private static ProviderError? Validate(ProviderExecutionContext context, ProviderExternalResourceId trackId)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            context.RequireResourceOwner(trackId, ProviderResourceKind.Track);
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        {
            return new(ProviderErrorKind.Forbidden);
        }
        if (!context.ProviderId.Equals(StableProviderId, StringComparison.Ordinal) ||
            !context.Policy.AllowsProvider(StableProviderId))
            return new(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested)
            return new(ProviderErrorKind.Canceled);
        return context.IsExpired(DateTimeOffset.UtcNow)
            ? new(ProviderErrorKind.CapabilityUnavailable)
            : null;
    }

    private static ProviderDownloadAvailabilityState Availability(AppleDownloadEndpointSnapshot snapshot)
    {
        if (snapshot.State == AppleDownloadEndpointState.Available &&
            snapshot.Capability(ProviderCapabilities.Download).State == AppleDownloadCapabilityState.Available &&
            snapshot.Capability("download-audio-song").State == AppleDownloadCapabilityState.Available)
            return ProviderDownloadAvailabilityState.Available;
        return snapshot.State switch
        {
            AppleDownloadEndpointState.NeedsAuthentication => ProviderDownloadAvailabilityState.AccountRequired,
            AppleDownloadEndpointState.Incompatible => ProviderDownloadAvailabilityState.Incompatible,
            _ => ProviderDownloadAvailabilityState.Unavailable
        };
    }

    private static ProviderErrorKind ErrorFor(AppleDownloadEndpointState state) => state switch
    {
        AppleDownloadEndpointState.NeedsConfiguration => ProviderErrorKind.AccountNeedsConfiguration,
        AppleDownloadEndpointState.NeedsAuthentication => ProviderErrorKind.Unauthorized,
        AppleDownloadEndpointState.Incompatible => ProviderErrorKind.NotSupported,
        _ => ProviderErrorKind.CapabilityUnavailable
    };

    private static ProviderError ErrorFor(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new(ProviderErrorKind.Unauthorized),
        HttpStatusCode.Forbidden => new(ProviderErrorKind.Forbidden),
        HttpStatusCode.NotFound => new(ProviderErrorKind.NotFound),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, RetryAfter(response)),
        >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };

    private static TimeSpan RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero
            ? delta
            : TimeSpan.FromSeconds(30);

    public static string Quality(ProviderAudioQuality requested, string? configured)
    {
        var configuredQuality = NormalizeQuality(configured);

        var ideal = requested switch
        {
            ProviderAudioQuality.HighResolution => "alac-24-96",
            ProviderAudioQuality.Lossless => "alac-16-44",
            ProviderAudioQuality.Lossy => "aac-320",
            ProviderAudioQuality.DataSaver => "aac-96",
            _ => configuredQuality
        };
        return ApplyClientQuality(ideal, configuredQuality);
    }

    internal static string ApplyClientQuality(string ideal, string? configured)
    {
        var configuredQuality = NormalizeQuality(configured);
        var requested = NormalizeQuality(ideal);
        var ranking = new[] { "alac-24-192", "alac-24-96", "alac-24-48", "alac-16-44", "aac-320", "aac-96" };
        var configuredIndex = Array.IndexOf(ranking, configuredQuality);
        var requestedIndex = Array.IndexOf(ranking, requested);

        // Original-quality playback uses the value saved in Settings. A Jellyfin
        // bandwidth request can select a lower Apple tier without silently raising
        // the configured quality; changing Settings later takes effect normally.
        return requestedIndex < configuredIndex ? configuredQuality : requested;
    }

    private static string NormalizeQuality(string? configured) => configured?.Trim().ToLowerInvariant() switch
    {
        "alac-24-192" => "alac-24-192",
        "alac-24-96" => "alac-24-96",
        "alac-24-48" => "alac-24-48",
        "alac-16-44" => "alac-16-44",
        "aac-320" => "aac-320",
        "aac-96" => "aac-96",
        _ => "alac-16-44"
    };

    private static ProviderMediaFormat? Media(string? mimeType, string quality) => mimeType?.ToLowerInvariant() switch
    {
        "audio/flac" or "audio/x-flac" => new(mimeType.ToLowerInvariant(), "flac", "flac"),
        "audio/mp4" or "audio/x-m4a" or "audio/m4a" => new(mimeType.ToLowerInvariant(), "m4a",
            quality.StartsWith("aac-", StringComparison.OrdinalIgnoreCase) ? "aac" : "alac"),
        "audio/aac" => new(mimeType.ToLowerInvariant(), "aac", "aac"),
        _ => null
    };

    private static string ArtifactId(string trackId, string extension)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trackId))).ToLowerInvariant();
        return $"apple-{hash[..32]}.{extension}";
    }

    private static bool SameOrigin(Uri expected, Uri actual) =>
        expected.Scheme.Equals(actual.Scheme, StringComparison.OrdinalIgnoreCase) &&
        expected.Host.Equals(actual.Host, StringComparison.OrdinalIgnoreCase) &&
        expected.Port == actual.Port;
}
