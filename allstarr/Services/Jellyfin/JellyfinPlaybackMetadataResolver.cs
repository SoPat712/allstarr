using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Models.Settings;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Jellyfin;

public sealed class JellyfinPlaybackMetadataResolver : IPlaybackMetadataResolver
{
    private const int MaximumArtworkBytes = 5 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly JellyfinSettings _settings;
    private readonly IBackendLibraryAccessResolver _libraryAccess;
    private readonly IBackendLibraryPermissionSource _permissionSource;
    private readonly ILogger<JellyfinPlaybackMetadataResolver> _logger;

    public JellyfinPlaybackMetadataResolver(
        IHttpClientFactory httpClientFactory,
        IOptions<JellyfinSettings> settings,
        IBackendLibraryAccessResolver libraryAccess,
        IBackendLibraryPermissionSource permissionSource,
        ILogger<JellyfinPlaybackMetadataResolver> logger)
    {
        _httpClient = httpClientFactory.CreateClient(JellyfinProxyService.HttpClientName);
        _settings = settings.Value;
        _libraryAccess = libraryAccess;
        _permissionSource = permissionSource;
        _logger = logger;
    }

    public Task<PlaybackTrackMetadata?> ResolveAsync(string itemId, CancellationToken cancellationToken) =>
        Task.FromResult<PlaybackTrackMetadata?>(null);

    public async Task<PlaybackTrackMetadata?> ResolveAsync(
        string itemId, ProtocolExecutionContext context, CancellationToken cancellationToken)
    {
        var item = await ResolveNativeItemAsync(itemId, context, cancellationToken);
        return item.HasValue ? ParseMetadata(item.Value, itemId) : null;
    }

    public Task<PlaybackArtwork?> ResolveArtworkAsync(string itemId, CancellationToken cancellationToken) =>
        Task.FromResult<PlaybackArtwork?>(null);

    public async Task<PlaybackArtwork?> ResolveArtworkAsync(
        string itemId, ProtocolExecutionContext context, CancellationToken cancellationToken)
    {
        var item = await ResolveNativeItemAsync(itemId, context, cancellationToken);
        if (!item.HasValue) return null;
        var artworkItemId = ResolveArtworkItemId(item.Value, itemId);
        if (artworkItemId == null) return null;
        try
        {
            using var request = await _permissionSource.CreateRequestAsync(context, cancellationToken);
            if (request == null) return null;
            request.RequestUri = BuildBackendUri($"Items/{Uri.EscapeDataString(artworkItemId)}/Images/Primary?quality=90&width=96");
            request.Method = HttpMethod.Get;
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode ||
                contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
                response.Content.Headers.ContentLength > MaximumArtworkBytes)
                return null;
            await response.Content.LoadIntoBufferAsync(MaximumArtworkBytes, cancellationToken);
            return new PlaybackArtwork(await response.Content.ReadAsByteArrayAsync(cancellationToken), contentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug("Unable to resolve viewer Jellyfin artwork ({ExceptionType})", exception.GetType().Name);
            return null;
        }
    }

    private async Task<JsonElement?> ResolveNativeItemAsync(
        string itemId, ProtocolExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId) || itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) ||
            context.Protocol != ProtocolKind.Jellyfin || context.Principal == null ||
            !Uri.TryCreate(_settings.Url, UriKind.Absolute, out _))
            return null;
        try
        {
            var access = await _libraryAccess.ResolveAsync(context, cancellationToken);
            if (!access.Succeeded || access.LibraryIds.Length == 0) return null;
            using var request = await _permissionSource.CreateRequestAsync(context, cancellationToken);
            if (request == null) return null;
            request.RequestUri = BuildBackendUri(
                $"Items?UserId={Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}&Ids={Uri.EscapeDataString(itemId)}" +
                "&Recursive=true&IncludeItemTypes=Audio&Fields=Artists,AlbumArtist,Album,AlbumId,AlbumPrimaryImageTag,ImageTags,RunTimeTicks,IndexNumber,ProviderIds");
            request.Method = HttpMethod.Get;
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    string.Equals(TryGetString(item, "Id"), itemId, StringComparison.OrdinalIgnoreCase))
                    return item.Clone();
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug("Unable to resolve viewer Jellyfin metadata ({ExceptionType})", exception.GetType().Name);
            return null;
        }
    }

    public static PlaybackTrackMetadata ParseMetadata(JsonElement root, string itemId)
    {
        var title = TryGetString(root, "Name") ?? "Local Jellyfin track";
        var artist = TryGetFirstArrayString(root, "Artists") ??
                     TryGetString(root, "AlbumArtist") ??
                     "Jellyfin";
        var albumArtist = TryGetString(root, "AlbumArtist");
        var album = TryGetString(root, "Album");
        var artworkItemId = ResolveArtworkItemId(root, itemId);
        var durationSeconds = root.TryGetProperty("RunTimeTicks", out var runTimeTicks) &&
                              runTimeTicks.TryGetInt64(out var ticks) && ticks > 0
            ? (int)Math.Ceiling(ticks / (double)TimeSpan.TicksPerSecond)
            : (int?)null;
        var trackNumber = root.TryGetProperty("IndexNumber", out var indexNumber) &&
                          indexNumber.TryGetInt32(out var parsedTrackNumber) && parsedTrackNumber > 0
            ? parsedTrackNumber
            : (int?)null;
        var recordingMbid = root.TryGetProperty("ProviderIds", out var providerIds) &&
                            providerIds.ValueKind == JsonValueKind.Object
            ? TryGetString(providerIds, "MusicBrainzTrack") ?? TryGetString(providerIds, "MusicBrainzRecording")
            : null;

        return new PlaybackTrackMetadata(
            title,
            artist,
            album,
            artworkItemId != null
                ? $"/api/admin/downloads/artwork/{Uri.EscapeDataString(artworkItemId)}"
                : null,
            durationSeconds,
            albumArtist,
            recordingMbid,
            trackNumber);
    }

    private static string? ResolveArtworkItemId(JsonElement root, string itemId)
    {
        var hasPrimaryImage = root.TryGetProperty("ImageTags", out var imageTags) &&
                              imageTags.ValueKind == JsonValueKind.Object &&
                              imageTags.TryGetProperty("Primary", out var primaryTag) &&
                              primaryTag.ValueKind == JsonValueKind.String &&
                              !string.IsNullOrWhiteSpace(primaryTag.GetString());
        var albumId = TryGetString(root, "AlbumId");
        var hasAlbumImage = !string.IsNullOrWhiteSpace(albumId) &&
                            TryGetString(root, "AlbumPrimaryImageTag") is { Length: > 0 };
        return hasPrimaryImage ? itemId : hasAlbumImage ? albumId : null;
    }

    private Uri BuildBackendUri(string relative) =>
        new(new Uri(_settings.Url!.TrimEnd('/') + "/", UriKind.Absolute), relative);

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static string? TryGetFirstArrayString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return element.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

}
