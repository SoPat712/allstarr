using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Models.Settings;
using allstarr.Services.Deezer;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Providers.Deezer;

public sealed class DeezerStreamingCapabilityAdapter : IProviderStreamingCapability
{
    private readonly HttpClient http;
    private readonly IProviderAccountSecretAccessor secrets;
    private readonly DeezerDownloadService downloads;
    private readonly string? configuredQuality;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DeezerStreamingCapabilityAdapter(
        IHttpClientFactory clients,
        IProviderAccountSecretAccessor secrets,
        DeezerDownloadService downloads,
        IOptions<DeezerSettings> settings)
        : this(clients.CreateClient(DeezerDownloadCapabilityAdapter.HttpClientName),
            secrets, downloads, settings.Value.Quality)
    { }

    public DeezerStreamingCapabilityAdapter(
        HttpClient http,
        IProviderAccountSecretAccessor secrets,
        DeezerDownloadService downloads,
        string? configuredQuality)
    {
        this.http = http;
        this.secrets = secrets;
        this.downloads = downloads;
        this.configuredQuality = configuredQuality;
    }

    public string ProviderId => DeezerDownloadCapabilityAdapter.StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Streaming;

    public async Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request)
    {
        var error = Validate(context, request.TrackId);
        if (error != null) return ProviderOutcome<ProviderStreamLease>.Failure(error);
        try
        {
            var resolved = await ResolveAsync(context, request);
            if (resolved == null)
                return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
            return ProviderOutcome<ProviderStreamLease>.Success(new(
                $"deezer-stream-{Guid.CreateVersion7():N}",
                resolved.Value.Source,
                DateTimeOffset.UtcNow.AddMinutes(1),
                supportsByteRanges: false,
                supportsSeeking: false,
                resolved.Value.Media,
                ProviderStreamRetryBehavior.RefreshLease,
                (outbound, token) => OpenAsync(
                    outbound, request.TrackId.Value, resolved.Value.Media, token)));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (KeyNotFoundException)
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
        }
        catch (InvalidDataException)
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.IncompatibleMedia));
        }
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(
                DeezerDownloadCapabilityAdapter.HttpError(exception));
        }
        catch
        {
            return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.PermanentFailure));
        }
    }

    public async Task<ProviderOutcome<ProviderStreamProbeResult>> ProbeStreamAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request)
    {
        var error = Validate(context, request.TrackId);
        if (error != null) return ProviderOutcome<ProviderStreamProbeResult>.Failure(error);
        try
        {
            var resolved = await ResolveAsync(context, request);
            return resolved == null
                ? ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration))
                : ProviderOutcome<ProviderStreamProbeResult>.Success(new(
                    true, DateTimeOffset.UtcNow, resolved.Value.Media));
        }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.Canceled));
        }
        catch (KeyNotFoundException)
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
        }
        catch (InvalidDataException)
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.IncompatibleMedia));
        }
        catch (HttpRequestException exception)
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(
                DeezerDownloadCapabilityAdapter.HttpError(exception));
        }
        catch
        {
            return ProviderOutcome<ProviderStreamProbeResult>.Failure(new(ProviderErrorKind.TransientFailure));
        }
    }

    private async Task<(Uri Source, ProviderMediaFormat Media)?> ResolveAsync(
        ProviderExecutionContext context,
        ProviderStreamLeaseRequest request)
    {
        var credential = await secrets.UseAsync(context.Account!, bytes =>
            Task.FromResult(ParseCredential(bytes)), context.CancellationToken);
        if (credential == null) return null;
        var prepared = await downloads.ResolveDownloadAsync(
            request.TrackId.Value,
            credential.Arl,
            credential.ArlFallback,
            DeezerDownloadCapabilityAdapter.Quality(request.RequestedQuality, configuredQuality),
            context.CancellationToken);
        if (!DeezerDownloadCapabilityAdapter.TryMedia(
                prepared.Format, out var media, out _) ||
            !DeezerDownloadCapabilityAdapter.TryProviderUri(
                prepared.DownloadUrl, out var source))
            throw new InvalidDataException("The Deezer stream response is incompatible.");
        return (source, media!);
    }

    private async Task<HttpResponseMessage> OpenAsync(
        HttpRequestMessage request,
        string trackId,
        ProviderMediaFormat media,
        CancellationToken cancellationToken)
    {
        var upstream = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!upstream.IsSuccessStatusCode) return upstream;
        try
        {
            if (upstream.RequestMessage?.RequestUri != request.RequestUri ||
                !DeezerDownloadCapabilityAdapter.ValidTransportType(
                    upstream.Content.Headers.ContentType?.MediaType))
                throw new InvalidDataException("The Deezer stream transport is incompatible.");
            var encrypted = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            var decrypted = new DeezerDecryptedStream(encrypted, trackId);
            var relay = new HttpResponseMessage(upstream.StatusCode)
            {
                RequestMessage = request,
                Content = new StreamContent(new ResponseOwnedStream(decrypted, upstream))
            };
            relay.Content.Headers.ContentType = new(media.MimeType);
            relay.Content.Headers.ContentLength = upstream.Content.Headers.ContentLength;
            return relay;
        }
        catch
        {
            upstream.Dispose();
            throw;
        }
    }

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
        if (!context.ProviderId.Equals(DeezerDownloadCapabilityAdapter.StableProviderId, StringComparison.Ordinal) ||
            !context.Policy.AllowsProvider(DeezerDownloadCapabilityAdapter.StableProviderId))
            return new(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested)
            return new(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow))
            return new(ProviderErrorKind.CapabilityUnavailable);
        return context.Account == null
            ? new(ProviderErrorKind.AccountNeedsConfiguration)
            : null;
    }

    private sealed record Credential(
        [property: JsonPropertyName("arl")] string? Arl,
        [property: JsonPropertyName("arlFallback")] string? ArlFallback);

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            owner.Dispose();
            GC.SuppressFinalize(this);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
