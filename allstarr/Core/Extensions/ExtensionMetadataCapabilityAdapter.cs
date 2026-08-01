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

    public Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
        ProviderExecutionContext context, ProviderTrackLookupRequest request)
    {
        context.RequireResourceOwner(request.Id, ProviderResourceKind.Track);
        return InvokeAsync(context, "getTrack", new { id = request.Id.Value, request.ExpectedSnapshotVersion }, MapTrack);
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
            Long(value, "durationMs") is { } duration ? TimeSpan.FromMilliseconds(duration) : null,
            OptionalText(value, "isrc"), value.TryGetProperty("isExplicit", out var explicitValue) &&
                                        explicitValue.ValueKind is JsonValueKind.True or JsonValueKind.False ? explicitValue.GetBoolean() : null,
            Artwork(value), OptionalText(value, "snapshotVersion"));
    }

    private ProviderAlbumMetadata MapAlbum(JsonElement value) => new(Id(ProviderResourceKind.Album, Text(value, "id")),
        Text(value, "title"), Artists(value), Int(value, "trackCount"), Artwork(value), OptionalText(value, "snapshotVersion"));

    private ProviderArtistMetadata MapArtist(JsonElement value) => new(Id(ProviderResourceKind.Artist, Text(value, "id")),
        Text(value, "name"), Artwork(value), OptionalText(value, "snapshotVersion"));

    private IReadOnlyList<ProviderArtistCredit> Artists(JsonElement value) => value.GetProperty("artists").EnumerateArray()
        .Select(item => new ProviderArtistCredit(Text(item, "name"), OptionalText(item, "id") is { } id ? Id(ProviderResourceKind.Artist, id) : null)).ToArray();

    private ProviderExternalResourceId Id(ProviderResourceKind kind, string id) => new(ProviderId, kind, id);

    private static ProviderArtworkReference? Artwork(JsonElement value) =>
        Uri.TryCreate(OptionalText(value, "artworkUrl"), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? new ProviderArtworkReference(publicUri: uri, revision: OptionalText(value, "artworkRevision")) : null;
}
