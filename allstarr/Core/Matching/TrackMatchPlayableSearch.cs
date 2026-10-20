using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Spotify;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Matching;

public sealed class TrackMatchPlayableSearch(
    IProtocolProviderGateway gateway,
    TrackMatchDecisionEngine matcher,
    ILogger<TrackMatchPlayableSearch> logger,
    IEffectiveProviderPolicyResolver? effectivePolicies = null)
{
    public async Task<PlayableTrackMatch> MatchAsync(
        ProtocolExecutionContext context,
        ExternalTrackMatchSnapshot source,
        TrackMatchScope scope,
        IReadOnlyList<LocalTrackMatchCandidate> localCandidates,
        ScopedTrackMatchOverride? manualOverride,
        CancellationToken cancellationToken)
    {
        var title = FuzzyMatcher.SearchQuery(source.Title);
        var artist = source.Artist?.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        var queries = new[]
            {
                FuzzyMatcher.SearchQuery(source.Title, source.Artist),
                Join(title, source.Album),
                title,
                artist,
                source.Album
            }
            .Where(query => !string.IsNullOrWhiteSpace(query) && query.Trim().Length >= 2)
            .Select(query => query!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var (providerOrder, effectiveMatcher) = await MatchingPolicyAsync(context, cancellationToken);
        if (!await CanAutomaticallyMatchAsync(context, source.ProviderId, cancellationToken))
            return DecideMatch(source, scope, [], [], manualOverride, providerOrder, effectiveMatcher);
        var primarySongs = queries.Length == 0 ? [] : await SearchAsync(queries[0]);
        var primaryMatch = DecideMatch(
            source, scope, primarySongs, localCandidates, manualOverride, providerOrder, effectiveMatcher);
        if (primaryMatch.Decision.State == TrackMatchReviewState.Pinned ||
            primaryMatch.Decision.State == TrackMatchReviewState.Accepted && !IsExplicitFallback(source, primaryMatch, localCandidates))
            return primaryMatch;

        var songs = primarySongs.Concat((await Task.WhenAll(queries.Skip(1).Select(SearchAsync)))
                .SelectMany(result => result))
            .DistinctBy(song => $"{song.ExternalProvider}:{song.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return DecideMatch(source, scope, songs, localCandidates, manualOverride, providerOrder, effectiveMatcher);

        async Task<Song[]> SearchAsync(string query) =>
            ((await gateway.SearchPlayableSongsAsync(context, query, 15)) ?? [])
            .Where(song => IsPlayable(song, providerOrder))
            .Where(song => !string.IsNullOrWhiteSpace(song.ExternalProvider) &&
                           !string.IsNullOrWhiteSpace(song.ExternalId))
            .Where(song => !song.ExternalProvider!.Equals(source.ProviderId, StringComparison.OrdinalIgnoreCase) ||
                           !song.ExternalId!.Equals(source.ExternalId, StringComparison.Ordinal))
            .DistinctBy(song => $"{song.ExternalProvider}:{song.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .ToArray();

        static string Join(string? left, string? right) =>
            string.Join(' ', new[] { left, right }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    public async Task<PlayableTrackMatch?> ReuseAsync(
        ProtocolExecutionContext context,
        ExternalTrackMatchSnapshot source,
        TrackMatchScope scope,
        IEnumerable<ProviderTrackIdentityRecord> identities,
        IReadOnlyList<LocalTrackMatchCandidate> localCandidates,
        ScopedTrackMatchOverride? manualOverride,
        CancellationToken cancellationToken,
        ISet<Guid>? unavailableIdentities = null)
    {
        var (providerOrder, effectiveMatcher) = await MatchingPolicyAsync(context, cancellationToken);
        if (!await CanAutomaticallyMatchAsync(context, source.ProviderId, cancellationToken)) return null;
        var order = ProviderRanks(providerOrder);
        var cachedRoutes = identities
            .Where(CatalogIdentityPolicy.IsPlaybackEligible)
            .Where(identity => order.ContainsKey(
                ExternalTrackPlaybackPolicy.Normalize(identity.ProviderId)))
            .OrderBy(identity => order[ExternalTrackPlaybackPolicy.Normalize(identity.ProviderId)])
            .ThenByDescending(identity => identity.Verification == ProviderIdentityVerification.Pinned)
            .ToArray();
        var cachedProviders = cachedRoutes
            .Select(identity => ExternalTrackPlaybackPolicy.Normalize(identity.ProviderId))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var cached in cachedRoutes)
        {
            // A provider ranked above this route has never been tried, so search instead of settling for it.
            var rank = order[ExternalTrackPlaybackPolicy.Normalize(cached.ProviderId)];
            if (order.Any(provider => provider.Value < rank && !cachedProviders.Contains(provider.Key))) break;
            Song? song;
            try
            {
                song = await gateway.GetSongAsync(context, cached.ProviderId, cached.ExternalId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogInformation(
                    "Cached {Provider} track {TrackId} is unavailable; trying the next route",
                    cached.ProviderId,
                    cached.ExternalId);
                continue;
            }
            if (song == null || !IsPlayable(song, providerOrder) || string.IsNullOrWhiteSpace(song.ExternalId))
            {
                unavailableIdentities?.Add(cached.Id);
                continue;
            }

            var match = DecideMatch(
                source, scope, [song], localCandidates, manualOverride, providerOrder, effectiveMatcher);
            if (match.Decision.State == TrackMatchReviewState.Pinned ||
                match.Decision.State == TrackMatchReviewState.Accepted && !IsExplicitFallback(source, match, localCandidates))
                return match;
            unavailableIdentities?.Add(cached.Id);
        }
        return null;
    }

    private static bool IsExplicitFallback(
        ExternalTrackMatchSnapshot source,
        PlayableTrackMatch match,
        IReadOnlyList<LocalTrackMatchCandidate> localCandidates) =>
        source.IsExplicit == false &&
        (match.SelectedExternal?.ExplicitContentLyrics == 1 ||
         localCandidates.Any(candidate => candidate.LibraryTrackId == match.Decision.SelectedLibraryTrackId &&
                                          candidate.IsExplicit == true));

    private PlayableTrackMatch DecideMatch(
        ExternalTrackMatchSnapshot source,
        TrackMatchScope scope,
        IReadOnlyList<Song> songs,
        IReadOnlyList<LocalTrackMatchCandidate> localCandidates,
        ScopedTrackMatchOverride? manualOverride,
        IReadOnlyList<string> providerOrder,
        TrackMatchDecisionEngine effectiveMatcher)
    {
        var groups = GroupEquivalent(songs, scope, providerOrder);
        return new(
            effectiveMatcher.Decide(
                scope,
                source,
                localCandidates.Concat(songs.Select(song => ToCandidate(song, scope))).ToArray(),
                manualOverride,
                providerOrder),
            songs.ToDictionary(song => CandidateId(song.ExternalProvider!, song.ExternalId!)),
            groups.SelectMany(group => group.Select(song => (
                    Id: CandidateId(song.ExternalProvider!, song.ExternalId!), Group: (IReadOnlyList<Song>)group)))
                .ToDictionary(item => item.Id, item => item.Group));
    }

    public bool CanUseProvider(string? providerId)
    {
        var normalized = ExternalTrackPlaybackPolicy.Normalize(providerId);
        return normalized.Length > 0 &&
               gateway.GetProviderOrder(ProviderCapabilityKind.Streaming)
                   .Any(provider => ExternalTrackPlaybackPolicy.Normalize(provider) == normalized);
    }

    public async Task<bool> CanUseProviderAsync(
        string? providerId,
        CancellationToken cancellationToken = default)
    {
        var normalized = ExternalTrackPlaybackPolicy.Normalize(providerId);
        return normalized.Length > 0 &&
               (await ProviderOrderAsync(cancellationToken))
               .Any(provider => ExternalTrackPlaybackPolicy.Normalize(provider) == normalized);
    }

    public async Task<bool> CanUseProviderAsync(
        ProviderActorContext actor,
        string? providerId,
        CancellationToken cancellationToken = default)
    {
        var normalized = ExternalTrackPlaybackPolicy.Normalize(providerId);
        return normalized.Length > 0 &&
               (await gateway.GetPlayableProviderOrderAsync(actor, cancellationToken))
               .Any(provider => ExternalTrackPlaybackPolicy.Normalize(provider) == normalized);
    }

    private static bool IsPlayable(Song song, IReadOnlyList<string> providerOrder)
    {
        return ExternalTrackPlaybackPolicy.CanUseForPlayback(song) &&
               providerOrder.Any(provider => ExternalTrackPlaybackPolicy.Normalize(provider) ==
                                             ExternalTrackPlaybackPolicy.Normalize(song.ExternalProvider));
    }

    private LocalTrackMatchCandidate ToCandidate(Song song, TrackMatchScope scope) => new(
        CandidateId(song.ExternalProvider!, song.ExternalId!),
        scope.UserId,
        scope.BackendInstanceId,
        null,
        song.ExternalId!,
        null,
        song.Title,
        song.Artist,
        song.Album,
        song.AlbumArtist,
        song.Duration * 1000L,
        song.Isrc,
        null,
        song.ExplicitContentLyrics switch
        {
            1 => true,
            0 or 3 => false,
            _ => null
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [song.ExternalProvider!] = song.ExternalId!
        },
        IsLocal: false);

    private static Guid CandidateId(string provider, string externalId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{provider.Trim().ToLowerInvariant()}:{externalId.Trim()}"))[..16]);

    private List<Song[]> GroupEquivalent(
        IEnumerable<Song> songs,
        TrackMatchScope scope,
        IReadOnlyList<string> providerOrder)
    {
        var order = ProviderRanks(providerOrder);
        var groups = new List<List<Song>>();
        foreach (var song in songs.OrderBy(song =>
                     order.GetValueOrDefault(
                         ExternalTrackPlaybackPolicy.Normalize(song.ExternalProvider),
                         int.MaxValue)))
        {
            var candidate = ToCandidate(song, scope);
            var group = groups.FirstOrDefault(existing =>
                TrackMatchDecisionEngine.SameRecordingIdentity(
                    ToCandidate(existing[0], scope),
                    candidate));
            if (group == null)
                groups.Add([song]);
            else
                group.Add(song);
        }
        return groups.Select(group => group.ToArray()).ToList();
    }

    private static Dictionary<string, int> ProviderRanks(IReadOnlyList<string> providerOrder) =>
        providerOrder
            .Select((provider, index) => (
                Provider: ExternalTrackPlaybackPolicy.Normalize(provider),
                Index: index))
            .GroupBy(item => item.Provider, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(item => item.Index), StringComparer.Ordinal);

    private async Task<IReadOnlyList<string>> ProviderOrderAsync(
        CancellationToken cancellationToken) => effectivePolicies == null
        ? gateway.GetProviderOrder(ProviderCapabilityKind.Streaming)
        : (await effectivePolicies.ResolveAsync(cancellationToken))
            .ApplyProviderAvailability(
                ProviderCapabilityKind.Streaming,
                gateway.GetProviderOrder(ProviderCapabilityKind.Streaming));

    private async Task<(IReadOnlyList<string> ProviderOrder, TrackMatchDecisionEngine Matcher)> MatchingPolicyAsync(
        ProtocolExecutionContext context, CancellationToken cancellationToken)
    {
        if (effectivePolicies == null)
            return (gateway.GetProviderOrder(ProviderCapabilityKind.Streaming), matcher);
        var policy = context.Actor?.EffectiveUserId is { } userId
            ? await effectivePolicies.ResolveForUserAsync(userId, cancellationToken)
            : await effectivePolicies.ResolveAsync(cancellationToken);
        return (
            policy.ApplyAutomaticProviderAvailability(
                ProviderCapabilityKind.Streaming,
                gateway.GetProviderOrder(ProviderCapabilityKind.Streaming)),
            matcher.WithLocalPriorityWindow(policy.LocalPreferenceWindow));
    }

    private async Task<bool> CanAutomaticallyMatchAsync(ProtocolExecutionContext context, string providerId,
        CancellationToken cancellationToken) => effectivePolicies == null ||
        (context.Actor?.EffectiveUserId is { } userId
            ? await effectivePolicies.ResolveForUserAsync(userId, cancellationToken)
            : await effectivePolicies.ResolveAsync(cancellationToken)).IsAutomatic(providerId);


}

public sealed record PlayableTrackMatch(
    TrackMatchDecision Decision,
    IReadOnlyDictionary<Guid, Song> ExternalCandidates,
    IReadOnlyDictionary<Guid, IReadOnlyList<Song>> EquivalentExternalCandidates)
{
    public Song? SelectedExternal =>
        Decision.SelectedLibraryTrackId is { } id
            ? ExternalCandidates.GetValueOrDefault(id)
            : null;

    public IReadOnlyList<Song> RoutableExternalCandidates =>
        (Decision.State is TrackMatchReviewState.Accepted or TrackMatchReviewState.Suggested) &&
        Decision.SelectedLibraryTrackId is { } id
            ? EquivalentExternalCandidates.GetValueOrDefault(id) ?? []
            : [];
}
