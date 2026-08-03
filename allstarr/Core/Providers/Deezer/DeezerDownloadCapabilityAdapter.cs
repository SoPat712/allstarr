using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Providers.Spotify;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Deezer;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Providers.Deezer;

public sealed class DeezerDownloadCapabilityAdapter : IProviderDownloadCapability
{
    public const string StableProviderId = "deezer";
    public const string HttpClientName = "DeezerDownloadCapability";

    private readonly HttpClient http;
    private readonly IProviderAccountSecretAccessor secrets;
    private readonly DeezerDownloadService downloads;
    private readonly ProviderDownloadArtifactResolver artifacts;
    private readonly string? configuredQuality;
    private readonly long maximumArtifactBytes;
    private readonly ILogger logger;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DeezerDownloadCapabilityAdapter(
        IHttpClientFactory clients,
        IProviderAccountSecretAccessor secrets,
        DeezerDownloadService downloads,
        ProviderDownloadArtifactResolver artifacts,
        IOptions<DeezerSettings> settings,
        ProviderDownloadWorkspaceOptions workspaceOptions,
        ILogger<DeezerDownloadCapabilityAdapter> logger)
        : this(clients.CreateClient(HttpClientName), secrets, downloads, artifacts,
            settings.Value.Quality, workspaceOptions.MaximumArtifactBytes, logger)
    { }

    public DeezerDownloadCapabilityAdapter(
        HttpClient http,
        IProviderAccountSecretAccessor secrets,
        DeezerDownloadService downloads,
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
                    [ProviderAudioQuality.Lossy, ProviderAudioQuality.Lossless]));
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
                credential.Arl,
                credential.ArlFallback,
                Quality(request.RequestedQuality, configuredQuality),
                context.CancellationToken);
            if (!TryMedia(prepared.Format, out var media, out var extension) ||
                !TryProviderUri(prepared.DownloadUrl, out var downloadUri))
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.IncompatibleMedia));

            using var response = await RetryHelper.RetryWithBackoffAsync(async () =>
            {
                using var outbound = new HttpRequestMessage(HttpMethod.Get, downloadUri);
                outbound.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                outbound.Headers.Accept.ParseAdd("*/*");
                var result = await http.SendAsync(
                    outbound, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
                return RetryHelper.EnsureSuccessOrDispose(result);
            }, logger, cancellationToken: context.CancellationToken);
            if (response.RequestMessage?.RequestUri is not { } actual || actual != downloadUri ||
                !ValidTransportType(response.Content.Headers.ContentType?.MediaType))
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.IncompatibleMedia));
            var expectedBytes = response.Content.Headers.ContentLength;
            if (expectedBytes is <= 0 || expectedBytes > maximumArtifactBytes)
                return ProviderOutcome<ProviderDownloadedArtifact>.Failure(new(ProviderErrorKind.IncompatibleMedia));

            await using var content = await response.Content.ReadAsStreamAsync(context.CancellationToken);
            progress?.Report(new(ProviderDownloadProgressStage.Transferring, 0, expectedBytes));
            var written = await artifacts.WriteProducedAsync(new(
                request.Workspace,
                request.DurableJobId,
                StableProviderId,
                ArtifactId(request.TrackId.Value, extension),
                maximumArtifactBytes,
                (output, token) => downloads.DecryptDownloadAsync(
                    content, output, request.TrackId.Value, token))
            {
                ExpectedBytes = expectedBytes,
                Progress = (complete, total) => progress?.Report(new(
                    ProviderDownloadProgressStage.Transferring, complete, total))
            }, context.CancellationToken);
            progress?.Report(new(ProviderDownloadProgressStage.Verifying, written.SizeBytes, written.SizeBytes));
            var output = new ProviderDownloadedArtifact(
                written.ArtifactId, written.Sha256, written.SizeBytes, media!, verified: true);
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

    internal static string Quality(ProviderAudioQuality requested, string? configured)
    {
        var ceiling = configured?.ToUpperInvariant() switch
        {
            "MP3_128" or "128" => "MP3_128",
            "MP3_320" or "320" => "MP3_320",
            _ => "FLAC"
        };
        return requested switch
        {
            ProviderAudioQuality.Lossy when ceiling == "FLAC" => "MP3_320",
            ProviderAudioQuality.Lossy => ceiling,
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
            return string.IsNullOrWhiteSpace(credential?.Arl) ? null : credential;
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

    private static bool TryMedia(
        string format,
        out ProviderMediaFormat? media,
        out string extension)
    {
        switch (format.ToUpperInvariant())
        {
            case "FLAC":
                media = new("audio/flac", "flac", "flac");
                extension = ".flac";
                return true;
            case "MP3_320":
                media = new("audio/mpeg", "mp3", "mp3", bitrate: 320_000);
                extension = ".mp3";
                return true;
            case "MP3_128":
                media = new("audio/mpeg", "mp3", "mp3", bitrate: 128_000);
                extension = ".mp3";
                return true;
            default:
                media = null;
                extension = string.Empty;
                return false;
        }
    }

    private static bool ValidTransportType(string? value) =>
        value?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryProviderUri(string value, out Uri uri)
    {
        uri = null!;
        return OutboundRequestGuard.TryCreateSafeHttpUri(value, out var parsed, out _) &&
               parsed!.Scheme == Uri.UriSchemeHttps && (uri = parsed) != null;
    }

    private static string ArtifactId(string trackId, string extension) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trackId))).ToLowerInvariant() + extension;

    private static ProviderError HttpError(HttpRequestException exception) => exception.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(ProviderErrorKind.Unauthorized),
        HttpStatusCode.NotFound => new(ProviderErrorKind.NotFound),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(30)),
        >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };

    private sealed record Credential(
        [property: JsonPropertyName("arl")] string? Arl,
        [property: JsonPropertyName("arlFallback")] string? ArlFallback);
}
