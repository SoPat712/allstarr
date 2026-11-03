using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Services.Local;

namespace allstarr.Core.ClientApi;

internal static class ClientCatalogIds
{
    internal static string Resource(string provider, ProviderResourceKind kind, string id) =>
        ProtocolProviderGateway.ProtocolItemId(new(provider, kind, id));

    internal static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var parsed = LocalLibraryService.ParseExternalResource(id);
        if (!parsed.isExternal) return id;
        return Resource(parsed.provider!, parsed.type switch
        {
            "song" => ProviderResourceKind.Track,
            "album" => ProviderResourceKind.Album,
            "artist" => ProviderResourceKind.Artist,
            _ => ProviderResourceKind.Playlist
        }, parsed.externalId!);
    }
}
