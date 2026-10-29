using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Extensions;

public static class ExtensionInvocationSecretScope
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> Current = new();

    public static string? Resolve(string key) =>
        Current.Value != null && Current.Value.TryGetValue(key, out var value) ? value : null;

    public static IDisposable Open(IReadOnlyDictionary<string, string> values)
    {
        var previous = Current.Value;
        Current.Value = values;
        return new Scope(() => Current.Value = previous);
    }

    private sealed class Scope(Action close) : IDisposable
    {
        private Action? _close = close;
        public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke();
    }
}

public sealed class ExtensionMetadataCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderMetadataCapability
{
    public ExtensionMetadataCapabilityAdapter(
        ExtensionSandbox sandbox,
        ExtensionSdkManifest manifest,
        IProviderAccountSecretAccessor? secrets = null) :
        base(sandbox, manifest, ProviderCapabilityKind.Metadata, secrets)
    { }

    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;

    public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(
        ProviderExecutionContext context, ProviderMetadataSearchRequest request) =>
        InvokeAsync(context, "searchTracks", SearchRequest(request), value => MapPage(value, MapTrack));

    public async Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
        ProviderExecutionContext context, ProviderTrackLookupRequest request)
    {
        context.RequireResourceOwner(request.Id, ProviderResourceKind.Track);
        var outcome = await InvokeAsync(context, "getTrack",
            new { id = request.Id.Value, request.ExpectedSnapshotVersion }, MapTrack);
        return outcome.IsSuccess && outcome.RequireValue().Id != request.Id
            ? Failure<ProviderTrackMetadata>(ProviderErrorKind.NotFound)
            : outcome;
    }

    public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(
        ProviderExecutionContext context, ProviderIsrcLookupRequest request) =>
        InvokeAsync(context, "lookupByIsrc", new { request.Isrc, request.Market }, MapTrack);

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(
        ProviderExecutionContext context, ProviderMetadataSearchRequest request) =>
        InvokeAsync(context, "searchAlbums", SearchRequest(request), value => MapPage(value, MapAlbum));
    public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(
        ProviderExecutionContext context, ProviderAlbumLookupRequest request)
    {
        context.RequireResourceOwner(request.Id, ProviderResourceKind.Album);
        return InvokeAsync(context, "getAlbum", new { id = request.Id.Value, request.ExpectedSnapshotVersion }, MapAlbum);
    }
    public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(
        ProviderExecutionContext context, ProviderMetadataSearchRequest request) =>
        InvokeAsync(context, "searchArtists", SearchRequest(request), value => MapPage(value, MapArtist));
    public Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(
        ProviderExecutionContext context, ProviderArtistLookupRequest request)
    {
        context.RequireResourceOwner(request.Id, ProviderResourceKind.Artist);
        return InvokeAsync(context, "getArtist", new { id = request.Id.Value, request.ExpectedSnapshotVersion }, MapArtist);
    }

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> GetArtistAlbumsAsync(
        ProviderExecutionContext context, ProviderArtistItemsRequest request)
    {
        context.RequireResourceOwner(request.Id, ProviderResourceKind.Artist);
        return InvokeAsync(context, "getArtistAlbums", ArtistItemsRequest(request), value => MapPage(value, MapAlbum));
    }

    public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> GetArtistTracksAsync(
        ProviderExecutionContext context, ProviderArtistItemsRequest request)
    {
        context.RequireResourceOwner(request.Id, ProviderResourceKind.Artist);
        return InvokeAsync(context, "getArtistTracks", ArtistItemsRequest(request), value => MapPage(value, MapTrack));
    }

    private static object SearchRequest(ProviderMetadataSearchRequest request) =>
        new { request.Query, page = new { request.Page.Limit, request.Page.Cursor }, request.Market };

    private static object ArtistItemsRequest(ProviderArtistItemsRequest request) =>
        new { id = request.Id.Value, page = new { request.Page.Limit, request.Page.Cursor }, request.ExpectedSnapshotVersion };

    private ProviderPage<T> MapPage<T>(JsonElement value, Func<JsonElement, T> map) => new(ProviderId,
        value.GetProperty("items").EnumerateArray().Select(map), OptionalText(value, "nextCursor"),
        Bool(value, "isPartial"), OptionalText(value, "snapshotVersion"));

    private ProviderTrackMetadata MapTrack(JsonElement value)
    {
        var albumId = OptionalText(value, "albumId");
        return new ProviderTrackMetadata(Id(ProviderResourceKind.Track, Text(value, "id")), Text(value, "title"),
            Artists(value), albumId == null ? null : Id(ProviderResourceKind.Album, albumId), OptionalText(value, "albumTitle"),
            Long(value, "durationMs") is { } duration && duration > 0 ? TimeSpan.FromMilliseconds(duration) : null,
            OptionalText(value, "isrc"), value.TryGetProperty("isExplicit", out var explicitValue) &&
                                        explicitValue.ValueKind is JsonValueKind.True or JsonValueKind.False ? explicitValue.GetBoolean() : null,
            Artwork(value), OptionalText(value, "snapshotVersion"),
            trackNumber: Positive(value, "trackNumber"), discNumber: Positive(value, "discNumber"), totalTracks: Positive(value, "totalTracks"),
            year: Positive(value, "year"), genre: OptionalText(value, "genre"), bpm: Positive(value, "bpm"),
            spotifyId: OptionalText(value, "spotifyId"), releaseDate: OptionalText(value, "releaseDate"),
            albumArtist: OptionalText(value, "albumArtist"), composer: OptionalText(value, "composer"),
            label: OptionalText(value, "label"), copyright: OptionalText(value, "copyright"),
            contributors: Strings(value, "contributors"),
            explicitContentLyrics: Int(value, "explicitContentLyrics"), bitrate: Positive(value, "bitrate"),
            albumArtists: Artists(value, "albumArtists"), providerIds: ProviderIds(value), totalDiscs: Positive(value, "totalDiscs"),
            upc: OptionalText(value, "upc"), musicalKey: OptionalText(value, "musicalKey"), notes: OptionalText(value, "notes"),
            media: TrackMedia(value), size: Long(value, "size"), replayGain: ReplayGain(value), genres: Strings(value, "genres"));
    }

    private ProviderAlbumMetadata MapAlbum(JsonElement value) => new(Id(ProviderResourceKind.Album, Text(value, "id")),
        Text(value, "title"), Artists(value), Positive(value, "trackCount"), Artwork(value), OptionalText(value, "snapshotVersion"),
        year: Positive(value, "year"), genre: OptionalText(value, "genre"), releaseDate: OptionalText(value, "releaseDate"),
        label: OptionalText(value, "label"), notes: OptionalText(value, "notes"), providerIds: ProviderIds(value),
        lastFmUrl: OptionalText(value, "lastFmUrl"), tracks: value.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array
            ? tracks.EnumerateArray().Select(MapTrack) : [],
        isExplicit: value.TryGetProperty("isExplicit", out var explicitValue) &&
                    explicitValue.ValueKind is JsonValueKind.True or JsonValueKind.False ? explicitValue.GetBoolean() : null,
        upc: OptionalText(value, "upc"), edition: OptionalText(value, "edition"),
        releaseType: OptionalText(value, "releaseType"), exclusionReason: OptionalText(value, "exclusionReason"));

    private ProviderArtistMetadata MapArtist(JsonElement value) => new(Id(ProviderResourceKind.Artist, Text(value, "id")),
        Text(value, "name"), Artwork(value), OptionalText(value, "snapshotVersion"),
        biography: OptionalText(value, "biography"), providerIds: ProviderIds(value), lastFmUrl: OptionalText(value, "lastFmUrl"));

    private IReadOnlyList<ProviderArtistCredit> Artists(JsonElement value, string name = "artists") =>
        value.TryGetProperty(name, out var artists) && artists.ValueKind == JsonValueKind.Array
            ? artists.EnumerateArray().Select(item => new ProviderArtistCredit(Text(item, "name"),
                ResourceValue(item, "id") is { } id ? Id(ProviderResourceKind.Artist, id) : null)).ToArray() : [];

    private static string? ResourceValue(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? item.ToString() : null;

    private static int? Positive(JsonElement value, string name) => Int(value, name) is { } number && number > 0 ? number : null;

    private static IEnumerable<string> Strings(JsonElement value, string name) =>
        value.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!) : [];

    private static IReadOnlyDictionary<string, string> ProviderIds(JsonElement value) =>
        value.TryGetProperty("providerIds", out var ids) && ids.ValueKind == JsonValueKind.Object
            ? ids.EnumerateObject().Where(item => item.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                .ToDictionary(item => item.Name, item => item.Value.ToString()) : new Dictionary<string, string>();

    private static ProviderMediaFormat? TrackMedia(JsonElement value)
    {
        if (!value.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object) return null;
        return new(OptionalText(media, "mimeType") ?? "application/octet-stream", OptionalText(media, "container") ?? "unknown",
            OptionalText(media, "codec") ?? "unknown", Positive(media, "bitrate"), Positive(media, "sampleRate"),
            Positive(media, "bitDepth"), Positive(media, "channels"));
    }

    private static ProviderReplayGain? ReplayGain(JsonElement value)
    {
        if (!value.TryGetProperty("replayGain", out var gain) || gain.ValueKind != JsonValueKind.Object) return null;
        static double? Number(JsonElement item, string name, bool peak = false) =>
            item.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number &&
            number.TryGetDouble(out var result) && double.IsFinite(result) && (!peak || result >= 0) ? result : null;
        var result = new ProviderReplayGain
        {
            TrackGain = Number(gain, "trackGain"),
            AlbumGain = Number(gain, "albumGain"),
            TrackPeak = Number(gain, "trackPeak", true),
            AlbumPeak = Number(gain, "albumPeak", true)
        };
        return result.TrackGain != null || result.AlbumGain != null || result.TrackPeak != null || result.AlbumPeak != null ? result : null;
    }

    private ProviderExternalResourceId Id(ProviderResourceKind kind, string id) => new(ProviderId, kind, id);

    private static ProviderArtworkReference? Artwork(JsonElement value) =>
        Uri.TryCreate(OptionalText(value, "artworkUrl"), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? new ProviderArtworkReference(publicUri: uri, revision: OptionalText(value, "artworkRevision")) : null;
}
