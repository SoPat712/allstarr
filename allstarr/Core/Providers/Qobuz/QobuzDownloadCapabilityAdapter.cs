using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Qobuz;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Providers.Qobuz;

public sealed class QobuzDownloadCapabilityAdapter : IProviderDownloadCapability
{
    public const string StableProviderId = "qobuz";
    public const string HttpClientName = "QobuzDownloadCapability";

    private readonly HttpClient http;
    private readonly IProviderAccountSecretAccessor secrets;
    private readonly QobuzDownloadService downloads;
    private readonly ProviderDownloadArtifactResolver artifacts;
    private readonly string? configuredQuality;
    private readonly long maximumArtifactBytes;
    private readonly ILogger logger;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public QobuzDownloadCapabilityAdapter(
        IHttpClientFactory clients,
        IProviderAccountSecretAccessor secrets,
        QobuzDownloadService downloads,
        ProviderDownloadArtifactResolver artifacts,
        IOptions<QobuzSettings> settings,
        ProviderDownloadWorkspaceOptions workspaceOptions,
        ILogger<QobuzDownloadCapabilityAdapter> logger)
        : this(clients.CreateClient(HttpClientName), secrets, downloads, artifacts,
            settings.Value.Quality, workspaceOptions.MaximumArtifactBytes, logger)
    { }

    public QobuzDownloadCapabilityAdapter(
        HttpClient http,
        IProviderAccountSecretAccessor secrets,
        QobuzDownloadService downloads,
        ProviderDownloadArtifactResolver artifacts,
        string? configuredQuality,
        long maximumArtifactBytes,
        ILogger? logger = null)
    {
        this.http = http;
        this.secrets = secrets;
        this.downloads = downloads;
        this.artifacts = artifacts;
        this.configuredQuality = configuredQuality;
        this.maximumArtifactBytes = maximumArtifactBytes > 0
            ? maximumArtifactBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumArtifactBytes));
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
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
            return await CredentialAsync(context) == null
                ? ProviderOutcome<ProviderDownloadAvailability>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration))
                : ProviderOutcome<ProviderDownloadAvailability>.Success(new(
                    ProviderDownloadAvailabilityState.Available,
                    [ProviderAudioQuality.DataSaver, ProviderAudioQuality.Lossy, ProviderAudioQuality.Lossless, ProviderAudioQuality.HighResolution]));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderDownloadAvailability>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (KeyNotFoundException)
        {
            return ProviderOutcome<ProviderDownloadAvailability>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
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

        try
        {
            var credential = await CredentialAsync(context);
            if (credential == null)
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
            progress?.Report(new(ProviderDownloadProgressStage.Resolving, 0));
            var prepared = await downloads.ResolveDownloadAsync(
                request.TrackId.Value,
                credential.UserAuthToken,
                Quality(request.RequestedQuality, configuredQuality),
                context.CancellationToken);
            if (prepared.IsSample || !TryMedia(prepared, out var media, out var extension) ||
                !TryProviderUri(prepared.Url, out var downloadUri))
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.IncompatibleMedia));

            using var response = await RetryHelper.RetryWithBackoffAsync(async () =>
            {
                var result = await http.GetAsync(
                    downloadUri, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
                return RetryHelper.EnsureSuccessOrDispose(result);
            }, logger, cancellationToken: context.CancellationToken);
            if (response.RequestMessage?.RequestUri is not { } actual || actual != downloadUri ||
                !ValidTransportType(response.Content.Headers.ContentType?.MediaType, media!.MimeType))
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.IncompatibleMedia));
            var expectedBytes = response.Content.Headers.ContentLength;
            if (expectedBytes is <= 0 || expectedBytes > maximumArtifactBytes)
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.IncompatibleMedia));

            await using var content = await response.Content.ReadAsStreamAsync(context.CancellationToken);
            progress?.Report(new(ProviderDownloadProgressStage.Transferring, 0, expectedBytes));
            var written = await artifacts.WriteAsync(new(
                request.Workspace,
                request.DurableJobId,
                StableProviderId,
                ArtifactId(request.TrackId.Value, extension),
                content,
                maximumArtifactBytes)
            {
                ExpectedBytes = expectedBytes,
                Progress = (complete, total) => progress?.Report(new(
                    ProviderDownloadProgressStage.Transferring, complete, total))
            }, context.CancellationToken);
            progress?.Report(new(ProviderDownloadProgressStage.Verifying, written.SizeBytes, written.SizeBytes));
            var output = new ProviderDownloadedArtifact(
                written.ArtifactId, written.Sha256, written.SizeBytes, media, verified: true);
            progress?.Report(new(ProviderDownloadProgressStage.Completed, written.SizeBytes, written.SizeBytes));
            return ProviderOutcome<ProviderDownloadedArtifact>.Success(output);
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (KeyNotFoundException)
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
        }
        catch (InvalidDataException)
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.IncompatibleMedia));
        }
        catch (UnauthorizedAccessException)
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.Forbidden));
        }
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(HttpError(exception));
        }
        catch (IOException)
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.TransientFailure));
        }
        catch
        {
            return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.PermanentFailure));
        }
    }

    public static ProviderRegistration CreateRegistration(
        IProviderDownloadCapability adapter,
        IProviderStreamingCapability streaming,
        IProviderMetadataCapability metadata,
        IProviderPlaylistCapability playlists) => new(
        new ProviderDescriptor(
            StableProviderId,
            "Qobuz",
            "Qobuz catalog reads and account-bound managed audio downloads.",
            ProviderOrigin.BuiltIn,
            sdkVersion: "1",
            compatibilityVersion: "qobuz-download-v1",
            capabilities:
            [
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Metadata,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Optional,
                    compatibilityVersion: "1",
                    hooks:
                    [
                        "searchTracks", "getTrack", "lookupByIsrc", "searchAlbums", "getAlbum",
                        "searchArtists", "getArtist", "getArtistAlbums", "getArtistTracks"
                    ],
                    allowedAccountScopes:
                    [
                        ProviderAccountScope.Global,
                        ProviderAccountScope.User,
                        ProviderAccountScope.Library
                    ]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Streaming,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    compatibilityVersion: "1",
                    hooks: ["getStreamLease", "probeStream"],
                    allowedAccountScopes:
                    [
                        ProviderAccountScope.Global,
                        ProviderAccountScope.User,
                        ProviderAccountScope.Library
                    ]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Download,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    compatibilityVersion: "1",
                    hooks: ["checkAvailability", "download"],
                    allowedAccountScopes:
                    [
                        ProviderAccountScope.Global,
                        ProviderAccountScope.User,
                        ProviderAccountScope.Library
                    ]),
                new ProviderCapabilityDescriptor(
                    ProviderCapabilityKind.Playlist,
                    ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.Required,
                    compatibilityVersion: "1",
                    hooks: ["getUserPlaylists", "searchPlaylists", "getPlaylistTracks"],
                    allowedAccountScopes:
                    [
                        ProviderAccountScope.Global,
                        ProviderAccountScope.User,
                        ProviderAccountScope.Library
                    ])
            ],
            permissions: new ProviderPermissionDescriptor(
                networkOrigins:
                [
                    new Uri("https://www.qobuz.com/"),
                    new Uri("https://play.qobuz.com/")
                ],
                cache: true)),
        [adapter, streaming, metadata, playlists]);

    internal static string Quality(ProviderAudioQuality requested, string? configured)
    {
        var ceiling = configured?.ToUpperInvariant() switch
        {
            "MP3_320" or "MP3" => "MP3_320",
            "FLAC_16" or "CD" => "FLAC_16",
            "FLAC_24_LOW" or "24_96" => "FLAC_24_LOW",
            _ => "FLAC_24_HIGH"
        };
        return requested switch
        {
            ProviderAudioQuality.DataSaver => "MP3_320",
            ProviderAudioQuality.Lossy => "MP3_320",
            ProviderAudioQuality.Lossless when ceiling != "MP3_320" => "FLAC_16",
            ProviderAudioQuality.Lossless => ceiling,
            _ => ceiling
        };
    }

    private async Task<Credential?> CredentialAsync(ProviderExecutionContext context) =>
        await secrets.UseAsync(context.Account!, bytes => Task.FromResult(ParseCredential(bytes)),
            context.CancellationToken);

    private static Credential? ParseCredential(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var credential = JsonSerializer.Deserialize<Credential>(bytes.Span);
            return string.IsNullOrWhiteSpace(credential?.UserAuthToken) ||
                   string.IsNullOrWhiteSpace(credential.UserId)
                ? null
                : credential;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProviderError? Validate(
        ProviderExecutionContext context,
        ProviderExternalResourceId trackId)
    {
        ArgumentNullException.ThrowIfNull(context);
        try { context.RequireResourceOwner(trackId, ProviderResourceKind.Track); }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        { return new(ProviderErrorKind.Forbidden); }
        if (!context.ProviderId.Equals(StableProviderId, StringComparison.Ordinal) ||
            !context.Policy.AllowsProvider(StableProviderId))
            return new(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested)
            return new(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow))
            return new(ProviderErrorKind.CapabilityUnavailable);
        return context.Account == null
            ? new(ProviderErrorKind.AccountNeedsConfiguration)
            : null;
    }

    internal static bool TryMedia(
        QobuzDownloadService.QobuzDownloadResult prepared,
        out ProviderMediaFormat? media,
        out string extension)
    {
        var sampleRate = prepared.SamplingRate > 1_000
            ? checked((int)Math.Round(prepared.SamplingRate))
            : checked((int)Math.Round(prepared.SamplingRate * 1_000));
        if (prepared.FormatId == 5 && prepared.MimeType?.Contains("flac", StringComparison.OrdinalIgnoreCase) != true)
        {
            media = new("audio/mpeg", "mp3", "mp3", bitrate: 320_000);
            extension = ".mp3";
            return true;
        }
        if (prepared.FormatId is 6 or 7 or 27 &&
            prepared.MimeType?.Contains("flac", StringComparison.OrdinalIgnoreCase) == true &&
            prepared.BitDepth > 0 && sampleRate > 0)
        {
            media = new("audio/flac", "flac", "flac",
                sampleRate: sampleRate, bitDepth: prepared.BitDepth);
            extension = ".flac";
            return true;
        }
        media = null;
        extension = string.Empty;
        return false;
    }

    internal static bool ValidTransportType(string? actual, string expected) =>
        actual?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true ||
        actual?.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool TryProviderUri(string value, out Uri uri)
    {
        uri = null!;
        return OutboundRequestGuard.TryCreateSafeHttpUri(value, out var parsed, out _) &&
               parsed!.Scheme == Uri.UriSchemeHttps && (uri = parsed) != null;
    }

    private static string ArtifactId(string trackId, string extension) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trackId))).ToLowerInvariant() + extension;

    internal static ProviderError HttpError(HttpRequestException exception) => exception.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(ProviderErrorKind.Unauthorized),
        HttpStatusCode.NotFound => new(ProviderErrorKind.NotFound),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(30)),
        >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };

    private sealed record Credential(
        [property: JsonPropertyName("userAuthToken")] string? UserAuthToken,
        [property: JsonPropertyName("userId")] string? UserId);
}
