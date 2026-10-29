using allstarr.Core.Protocols;
using allstarr.Models.Domain;

namespace allstarr.Core.Music;

public sealed partial class MusicMixService
{
    internal async Task<IReadOnlyList<Song>> SeedCandidatesAsync(ProtocolExecutionContext context, Song? seed, Artist? artist,
        string? provider, string? externalId, CancellationToken token)
    {
        var result = new List<Song>();
        if (seed != null)
        {
            var resolved = seed;
            if (provider == null)
            {
                var found = await ReadRadioSourceAsync(providers.SearchAutomaticSongsAsync(context, seed.Title + " " + seed.Artist, 3), token) ?? [];
                resolved = found.FirstOrDefault(song => MusicSearchService.MetadataRecordingsMatch(seed, song)) ?? seed;
                provider = resolved.ExternalProvider;
                externalId = resolved.ExternalId;
            }
            if (provider != null && externalId != null)
                result.AddRange(await ReadRadioSourceAsync(providers.GetSimilarSongsAsync(context, provider, externalId, 100), token) ?? []);
            if (lastFm != null)
            {
                var suggestions = await lastFm.FindAsync(context, seed.Artist, seed.Title, SuggestionLookups, token);
                foreach (var suggestion in suggestions)
                {
                    var found = await ReadRadioSourceAsync(providers.SearchAutomaticSongsAsync(context, suggestion.Title + " " + suggestion.Artist, 3), token) ?? [];
                    var match = found.FirstOrDefault(song => MusicSearchService.MetadataRecordingsMatch(
                        new Song { Title = suggestion.Title, Artist = suggestion.Artist }, song, allowSparse: true));
                    if (match != null) result.Add(match);
                }
            }
        }
        else if (artist != null && provider != null && externalId != null)
        {
            var tracks = await ReadRadioSourceAsync(providers.GetArtistTracksAsync(context, provider, externalId), token) ?? [];
            result.AddRange(tracks.Take(50));
            foreach (var related in (await ReadRadioSourceAsync(providers.GetSimilarArtistsAsync(context, provider, externalId, 10), token) ?? []).Take(10))
                if (related.ExternalProvider != null && related.ExternalId != null)
                    result.AddRange((await ReadRadioSourceAsync(providers.GetArtistTracksAsync(context, related.ExternalProvider, related.ExternalId), token) ?? []).Take(20));
            foreach (var track in tracks.Take(3))
                result.AddRange(await SeedCandidatesAsync(context, track, null, track.ExternalProvider ?? provider, track.ExternalId, token));
        }
        return result;
    }
}
