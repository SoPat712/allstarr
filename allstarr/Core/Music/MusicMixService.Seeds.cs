using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Local;

namespace allstarr.Core.Music;

public sealed partial class MusicMixService
{
    internal async Task<IReadOnlyList<Song>> SeedCandidatesAsync(ProtocolExecutionContext context, Song? seed, Artist? artist,
        string? provider, string? externalId, CancellationToken token, SeedMixDiagnostics? diagnostics = null, bool includeArtist = true)
    {
        // Starting a mix discovers relationships even when descriptive reads use saved relationships only.
        context = context with { IsReadOnly = false };
        var providerSongs = ProviderSeedCandidatesAsync(context, seed, artist, provider, externalId, includeArtist, token, diagnostics);
        var suggestions = seed != null ? SimilarSeedCandidatesAsync(context, seed, token, diagnostics) : Task.FromResult(new List<Song>());
        await Task.WhenAll(providerSongs, suggestions);
        var (result, tracks) = await providerSongs;
        result.AddRange(await suggestions);
        if (seed == null)
            foreach (var track in tracks.Take(3))
                result.AddRange(await SeedCandidatesAsync(context, track, null, track.ExternalProvider, track.ExternalId, token, diagnostics, includeArtist: false));
        return result;
    }

    private async Task<(List<Song> Songs, List<Song> ArtistTracks)> ProviderSeedCandidatesAsync(
        ProtocolExecutionContext context, Song? seed, Artist? artist, string? provider, string? externalId,
        bool includeArtist, CancellationToken token, SeedMixDiagnostics? diagnostics)
    {
        var result = new List<Song>();
        var tracks = new List<Song>();
        var available = providers.GetProviderOrder(ProviderCapabilityKind.Metadata) ?? [];
        var order = context.Policy?.ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Metadata, available) ?? available;
        var sources = (provider == null ? order : new[] { provider }.Concat(order))
            .Where(source => context.Policy?.IsAutomatic(source) != false).Distinct(StringComparer.Ordinal).ToArray();
        var similarFound = seed == null;
        var tracksFound = !includeArtist;
        var relatedFound = !includeArtist;
        var resolved = false;
        var artistName = MusicLyrics.StripPresentationLabels(seed?.Artist ?? artist?.Name);
        var lookup = seed == null ? null : MusicLyrics.ForLookup(seed);
        if (lookup != null) lookup.ExplicitContentLyrics = seed!.ExplicitContentLyrics;
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            Song? match = null;
            if (lookup != null && !similarFound)
            {
                if (source == provider && !string.IsNullOrWhiteSpace(externalId)) match = seed;
                else
                {
                    using var phase = diagnostics?.Phase("seed-provider");
                    var answer = await ReadRadioSourceAsync(providers.SearchAutomaticProviderSongsAsync(context, source,
                        lookup.Title + " " + lookup.Artist, 3), token);
                    var found = answer?.IsSuccess == true ? answer.RequireValue() : [];
                    diagnostics?.Add("seed-search-found", found.Count);
                    match = found.FirstOrDefault(song => song.ExternalProvider == source && !string.IsNullOrWhiteSpace(song.ExternalId) &&
                        MusicSearchService.MetadataRecordingsMatch(lookup, song));
                }
                if (match != null)
                {
                    if (!resolved) { diagnostics?.Add("seed-resolved", 1); resolved = true; }
                    using var phase = diagnostics?.Phase("provider-similar");
                    var similar = await ReadRadioSourceAsync(providers.GetSimilarSongsAsync(context, source,
                        source == provider && !string.IsNullOrWhiteSpace(externalId) ? externalId : match.ExternalId!, 100), token) ?? [];
                    diagnostics?.Add("provider-similar", similar.Count);
                    result.AddRange(similar);
                    similarFound = similar.Count > 0;
                }
            }
            if (!tracksFound || !relatedFound)
            {
                using var phase = diagnostics?.Phase("artist-tracks");
                string? artistId = artist != null && source == provider ? externalId : null;
                if (artistId == null && match?.ArtistId is { } id)
                {
                    var parsed = LocalLibraryService.ParseExternalResource(id);
                    if (parsed.isExternal && parsed.type == "artist" && parsed.provider == source) artistId = parsed.externalId;
                }
                if (artistId == null && !string.IsNullOrWhiteSpace(artistName))
                {
                    var found = await ReadRadioSourceAsync(providers.SearchAsync(context, artistName, 0, 0, 3, source), token);
                    artistId = found?.Artists.FirstOrDefault(item => item.ExternalProvider == source &&
                        FuzzyMatcher.NormalizeForMatching(MusicLyrics.StripPresentationLabels(item.Name)) == FuzzyMatcher.NormalizeForMatching(artistName))?.ExternalId;
                }
                if (!string.IsNullOrWhiteSpace(artistId))
                {
                    if (!tracksFound)
                    {
                        var found = await ReadRadioSourceAsync(providers.GetArtistTracksAsync(context, source, artistId), token) ?? [];
                        diagnostics?.Add("artist-tracks", found.Count);
                        tracks.AddRange(found.Take(50));
                        result.AddRange(found.Take(50));
                        tracksFound = found.Count > 0;
                    }
                    if (!relatedFound)
                    {
                        var related = await ReadRadioSourceAsync(providers.GetSimilarArtistsAsync(context, source, artistId, 10), token) ?? [];
                        diagnostics?.Add("related-artists", related.Count);
                        foreach (var neighbour in related.Take(10))
                            if (neighbour.ExternalProvider != null && neighbour.ExternalId != null && context.Policy?.IsAutomatic(neighbour.ExternalProvider) != false)
                            {
                                var found = await ReadRadioSourceAsync(providers.GetArtistTracksAsync(context, neighbour.ExternalProvider, neighbour.ExternalId), token) ?? [];
                                diagnostics?.Add("related-tracks", found.Count);
                                result.AddRange(found.Take(20));
                                relatedFound |= found.Count > 0;
                            }
                    }
                }
            }
            if (similarFound && tracksFound && relatedFound) break;
        }
        return (result, tracks);
    }

    private async Task<List<Song>> SimilarSeedCandidatesAsync(ProtocolExecutionContext context, Song seed,
        CancellationToken token, SeedMixDiagnostics? diagnostics)
    {
        var result = new List<Song>();
        if (lastFm == null) return result;
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
        return result;
    }
}
