using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.ArtistGrid;

internal static class ArtistGridDiagnosticSamples
{
    public static async IAsyncEnumerable<ProviderTrackMetadata> TracksAsync(
        IProviderMetadataCapability metadata, ProviderExecutionContext context)
    {
        var index = await metadata.SearchArtistsAsync(context, new("*", new(100)));
        if (!index.IsSuccess) yield break;
        string[] preferred = ["Kanye West", "Playboi Carti", "Taylor Swift"];
        var artists = index.RequireValue().Items.OrderBy(item =>
                Array.FindIndex(preferred, name => name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)) is var rank && rank >= 0 ? rank : preferred.Length)
            .ThenBy(item => item.Name, StringComparer.Ordinal).Take(3);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artist in artists)
        {
            var tracks = await metadata.SearchTracksAsync(context, new(artist.Name, new(3)));
            if (!tracks.IsSuccess) continue;
            foreach (var track in tracks.RequireValue().Items)
                if (seen.Add(track.Id.Value)) yield return track;
        }
    }
}
