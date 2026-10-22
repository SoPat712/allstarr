using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.ArtistGrid;

internal static class ArtistGridDiagnosticSamples
{
    public static async IAsyncEnumerable<ProviderTrackMetadata> TracksAsync(
        IProviderMetadataCapability metadata, ProviderExecutionContext context)
    {
        var index = await metadata.SearchArtistsAsync(context, new("*", new(100)));
        if (!index.IsSuccess) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var artists = index.RequireValue().Items.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var offset = artists.Length == 0 ? 0 : Random.Shared.Next(artists.Length);
        foreach (var artist in artists.Skip(offset).Concat(artists.Take(offset)).Take(8))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var tracks = await metadata.SearchTracksAsync(context, new(artist.Name, new(3)));
            if (!tracks.IsSuccess) continue;
            foreach (var track in tracks.RequireValue().Items.Take(3))
                if (seen.Add(track.Id.Value)) yield return track;
        }
    }
}
