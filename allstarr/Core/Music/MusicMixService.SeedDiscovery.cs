using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Local;

namespace allstarr.Core.Music;

public sealed partial class MusicMixService
{
    private const int RelatedSeedArtists = 4, RelatedSeedTracks = 5, SeedLookupConcurrency = 3;

    private async Task DiscoverSeedSourcesAsync(ProtocolExecutionContext context, Song? seed, Artist? artist,
        string? provider, string? externalId, CancellationToken cancellationToken, SeedMixDiagnostics? diagnostics,
        bool includeArtist, List<Song>[] sources)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
        caller.Token.ThrowIfCancellationRequested();
        var clock = timeProvider ?? TimeProvider.System;
        var duration = context.Deadline - clock.GetUtcNow();
        if (duration <= TimeSpan.Zero) return;
        if (duration > TimeSpan.FromSeconds(3)) duration = TimeSpan.FromSeconds(3);
        using var timeout = new CancellationTokenSource(duration, clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, timeout.Token);
        var token = bounded.Token;
        context = new(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId, context.Principal,
            context.CorrelationId, clock.GetUtcNow() + duration, token, context.Client)
        { Policy = context.Policy, IsReadOnly = false };
        var available = providers.GetProviderOrder(ProviderCapabilityKind.Metadata) ?? [];
        var order = context.Policy?.ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Metadata, available) ?? available;
        var providerSources = (provider == null ? order : new[] { provider }.Concat(order))
            .Where(source => context.Policy?.IsAutomatic(source) != false).Distinct(StringComparer.Ordinal).ToArray();
        var matches = providerSources.ToDictionary(source => source,
            _ => new TaskCompletionSource<Song?>(TaskCreationOptions.RunContinuationsAsynchronously));
        (string Provider, string Id)? similarSource = null;
        await Task.WhenAll(KeepAsync(ArtistsAsync), KeepAsync(SimilarAsync), KeepAsync(async () =>
        {
            if (seed != null) await SimilarSeedCandidatesAsync(context, seed, token, diagnostics, sources[3]);
        }));
        if (seed == null)
            foreach (var track in sources[0].Take(3).ToArray())
                await KeepAsync(async () =>
                {
                    await SimilarForSongAsync(track);
                    await SimilarSeedCandidatesAsync(context, track, token, diagnostics, sources[3]);
                });
        var kept = CapSeedCandidates(seed, artist, sources, null).Count;
        if (!token.IsCancellationRequested && similarSource is { } refill && kept < SeedCandidateLimit)
            await KeepAsync(async () =>
            {
                var limit = Math.Min(SeedCandidateLimit + 1, sources[1].Count + SeedCandidateLimit - kept + 1);
                using var phase = diagnostics?.Phase("provider-similar");
                var found = await ReadRadioSourceAsync(providers.GetSimilarSongsAsync(context, refill.Provider, refill.Id, limit), token) ?? [];
                diagnostics?.Add("provider-similar", found.Count);
                foreach (var song in found.Take(limit))
                    if (!sources[1].Any(other => SameRadioRecording(song, other))) sources[1].Add(song);
            });
        caller.Token.ThrowIfCancellationRequested();

        async Task KeepAsync(Func<Task> read)
        {
            try { await read(); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !caller.IsCancellationRequested) { }
        }

        async Task SimilarAsync()
        {
            try { if (seed != null) await SimilarForSongAsync(seed); }
            finally { foreach (var match in matches.Values) match.TrySetResult(null); }
        }

        async Task SimilarForSongAsync(Song seed)
        {
            var resolved = false;
            var lookup = MusicLyrics.ForLookup(seed);
            lookup.ExplicitContentLyrics = seed.ExplicitContentLyrics;
            foreach (var source in providerSources)
            {
                token.ThrowIfCancellationRequested();
                var id = source == seed.ExternalProvider ? seed.ExternalId : null;
                Song? match = string.IsNullOrWhiteSpace(id) ? null : seed;
                if (string.IsNullOrWhiteSpace(id))
                {
                    using var phase = diagnostics?.Phase("seed-provider");
                    var answer = await ReadRadioSourceAsync(providers.SearchAutomaticProviderSongsAsync(context, source, lookup.Title + " " + lookup.Artist, 3), token);
                    var found = answer?.IsSuccess == true ? answer.RequireValue() : [];
                    diagnostics?.Add("seed-search-found", found.Count);
                    match = found.FirstOrDefault(song => song.ExternalProvider == source && !string.IsNullOrWhiteSpace(song.ExternalId) &&
                        MusicSearchService.MetadataRecordingsMatch(lookup, song));
                    id = match?.ExternalId;
                }
                matches[source].TrySetResult(match);
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!resolved) { diagnostics?.Add("seed-resolved", 1); resolved = true; }
                using var similarPhase = diagnostics?.Phase("provider-similar");
                var similar = await ReadRadioSourceAsync(providers.GetSimilarSongsAsync(context, source, id, SeedSourceCandidateShare), token) ?? [];
                diagnostics?.Add("provider-similar", similar.Count);
                sources[1].AddRange(similar.Take(SeedSourceCandidateShare));
                if (similar.Count == 0) continue;
                if (similar.Count >= SeedSourceCandidateShare) similarSource = (source, id);
                break;
            }
        }

        async Task ArtistsAsync()
        {
            if (!includeArtist) return;
            var name = MusicLyrics.StripPresentationLabels(seed?.Artist ?? artist?.Name);
            var ownFound = false;
            var relatedFound = false;
            using var phase = diagnostics?.Phase("artist-tracks");
            foreach (var source in providerSources)
            {
                token.ThrowIfCancellationRequested();
                string? id = artist != null && source == provider ? externalId : null;
                if (id == null && seed?.ArtistId is { } artistId)
                {
                    var parsed = LocalLibraryService.ParseExternalResource(artistId);
                    if (parsed.isExternal && parsed.type == "artist" && parsed.provider == source) id = parsed.externalId;
                }
                if (id == null && !string.IsNullOrWhiteSpace(name))
                {
                    var found = await ReadRadioSourceAsync(providers.SearchAsync(context, name, 0, 0, 3, source), token);
                    id = found?.Artists.FirstOrDefault(item => item.ExternalProvider == source &&
                        FuzzyMatcher.NormalizeForMatching(MusicLyrics.StripPresentationLabels(item.Name)) == FuzzyMatcher.NormalizeForMatching(name))?.ExternalId;
                }
                if (id == null && seed != null && (await matches[source].Task.WaitAsync(token))?.ArtistId is { } matchedArtistId)
                {
                    var parsed = LocalLibraryService.ParseExternalResource(matchedArtistId);
                    if (parsed.isExternal && parsed.type == "artist" && parsed.provider == source) id = parsed.externalId;
                }
                if (string.IsNullOrWhiteSpace(id)) continue;
                await Task.WhenAll(KeepAsync(async () =>
                {
                    if (ownFound) return;
                    var own = await ReadRadioSourceAsync(providers.GetArtistTracksAsync(context, source, id, SeedSourceCandidateShare), token) ?? [];
                    diagnostics?.Add("artist-tracks", own.Count);
                    sources[0].AddRange(own.Take(SeedSourceCandidateShare));
                    ownFound = own.Count > 0;
                }), KeepAsync(async () =>
                {
                    if (relatedFound) return;
                    var related = await ReadRadioSourceAsync(providers.GetSimilarArtistsAsync(context, source, id, RelatedSeedArtists), token) ?? [];
                    diagnostics?.Add("related-artists", related.Count);
                    using var slots = new SemaphoreSlim(SeedLookupConcurrency);
                    var tracks = await Task.WhenAll(related.Take(RelatedSeedArtists).Select(async neighbour =>
                    {
                        List<Song> found = [];
                        await KeepAsync(async () =>
                        {
                            if (neighbour.ExternalProvider == null || neighbour.ExternalId == null || context.Policy?.IsAutomatic(neighbour.ExternalProvider) == false) return;
                            await slots.WaitAsync(token);
                            try
                            {
                                found = await ReadRadioSourceAsync(providers.GetArtistTracksAsync(context, neighbour.ExternalProvider, neighbour.ExternalId, RelatedSeedTracks), token) ?? [];
                                diagnostics?.Add("related-tracks", found.Count);
                            }
                            finally { slots.Release(); }
                        });
                        return found.Take(RelatedSeedTracks).ToArray();
                    }));
                    sources[2].AddRange(tracks.SelectMany(items => items));
                    relatedFound = tracks.Any(items => items.Length > 0);
                }));
                if (ownFound && relatedFound) break;
            }
        }
    }
}
