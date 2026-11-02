using allstarr.Core.Protocols;
using allstarr.Models.Domain;

namespace allstarr.Core.Music;

public sealed partial class MusicMixService
{
    internal async Task<IReadOnlyList<Song>> SeedCandidatesAsync(ProtocolExecutionContext context, Song? seed, Artist? artist,
        string? provider, string? externalId, CancellationToken token, SeedMixDiagnostics? diagnostics = null)
    {
        var result = new List<Song>();
        if (seed != null)
        {
            var resolved = seed;
            if (provider == null)
            {
                using var phase = diagnostics?.Phase("seed-provider");
                var found = await ReadRadioSourceAsync(providers.SearchAutomaticSongsAsync(context, seed.Title + " " + seed.Artist, 3), token) ?? [];
                diagnostics?.Add("seed-search-found", found.Count);
                resolved = found.FirstOrDefault(song => MusicSearchService.MetadataRecordingsMatch(seed, song)) ?? seed;
                provider = resolved.ExternalProvider;
                externalId = resolved.ExternalId;
            }
            if (provider != null && externalId != null)
            {
                diagnostics?.Add("seed-resolved", 1);
                using var phase = diagnostics?.Phase("provider-similar");
                var similar = await ReadRadioSourceAsync(providers.GetSimilarSongsAsync(context, provider, externalId, 100), token) ?? [];
                diagnostics?.Add("provider-similar", similar.Count);
                result.AddRange(similar);
            }
            if (lastFm != null)
            {
                IReadOnlyList<SimilarTrackSuggestion> suggestions;
                using (diagnostics?.Phase("similar-tracks"))
                    suggestions = await lastFm.FindAsync(context, seed.Artist, seed.Title, SuggestionLookups, token);
                diagnostics?.Add("similar-tracks", suggestions.Count);
                foreach (var suggestion in suggestions)
                {
                    using var phase = diagnostics?.Phase("suggestion-search");
                    var found = await ReadRadioSourceAsync(providers.SearchAutomaticSongsAsync(context, suggestion.Title + " " + suggestion.Artist, 3), token) ?? [];
                    diagnostics?.Add("suggestions-found", found.Count);
                    var match = found.FirstOrDefault(song => MusicSearchService.MetadataRecordingsMatch(
                        new Song { Title = suggestion.Title, Artist = suggestion.Artist }, song, allowSparse: true));
                    if (match != null) { result.Add(match); diagnostics?.Add("suggestions-resolved", 1); }
                }
            }
        }
        else if (artist != null && provider != null && externalId != null)
        {
            List<Song> tracks;
            using (diagnostics?.Phase("artist-tracks"))
            {
                tracks = await ReadRadioSourceAsync(providers.GetArtistTracksAsync(context, provider, externalId), token) ?? [];
                diagnostics?.Add("artist-tracks", tracks.Count);
                result.AddRange(tracks.Take(50));
                var relatedArtists = await ReadRadioSourceAsync(providers.GetSimilarArtistsAsync(context, provider, externalId, 10), token) ?? [];
                diagnostics?.Add("related-artists", relatedArtists.Count);
                foreach (var related in relatedArtists.Take(10))
                    if (related.ExternalProvider != null && related.ExternalId != null)
                    {
                        var relatedTracks = await ReadRadioSourceAsync(providers.GetArtistTracksAsync(context, related.ExternalProvider, related.ExternalId), token) ?? [];
                        diagnostics?.Add("related-tracks", relatedTracks.Count);
                        result.AddRange(relatedTracks.Take(20));
                    }
            }
            foreach (var track in tracks.Take(3))
                result.AddRange(await SeedCandidatesAsync(context, track, null, track.ExternalProvider ?? provider, track.ExternalId, token, diagnostics));
        }
        return result;
    }
}
