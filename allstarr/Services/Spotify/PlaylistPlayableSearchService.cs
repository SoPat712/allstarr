using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Spotify;

/// <summary>
/// Runs background playlist matching through the same user/account-aware provider
/// gateway used by Jellyfin search. This keeps encrypted shared and personal
/// provider accounts usable without copying credentials back into deployment config.
/// </summary>
public sealed class PlaylistPlayableSearchService(
    IProtocolProviderGateway gateway,
    TrackMatchDecisionEngine matcher,
    BackendIdentityResolver identities,
    IdentityOptions identityOptions,
    IOptions<JellyfinSettings> jellyfinSettings,
    ILogger<PlaylistPlayableSearchService> logger)
{
    private readonly SemaphoreSlim _principalLock = new(1, 1);
    private AllstarrPrincipal? _principal;

    public async Task<IReadOnlyList<Song>?> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var principal = await ResolvePrincipalAsync(cancellationToken);
        if (principal == null)
        {
            return null;
        }

        var context = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            principal.BackendInstanceId,
            principal.BackendPrincipalId,
            principal,
            $"playlist-match-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddSeconds(30),
            cancellationToken);
        return (await gateway.SearchPlayableSongsAsync(context, query, limit))
            .Where(IsPlayable)
            .Take(limit)
            .ToList();
    }

    public async Task<PlayableTrackMatch> MatchAsync(
        ProtocolExecutionContext context,
        ExternalTrackMatchSnapshot source,
        TrackMatchScope scope,
        IReadOnlyList<LocalTrackMatchCandidate> localCandidates,
        ScopedTrackMatchOverride? manualOverride,
        CancellationToken cancellationToken)
    {
        var query = FuzzyMatcher.SearchQuery(source.Title);
        var songs = (await gateway.SearchPlayableSongsAsync(context, query, 60))
            .Where(IsPlayable)
            .Where(song => !string.IsNullOrWhiteSpace(song.ExternalProvider) &&
                           !string.IsNullOrWhiteSpace(song.ExternalId))
            .Where(song => !song.ExternalProvider!.Equals(source.ProviderId, StringComparison.OrdinalIgnoreCase) ||
                           !song.ExternalId!.Equals(source.ExternalId, StringComparison.Ordinal))
            .DistinctBy(song => $"{song.ExternalProvider}:{song.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var groups = GroupEquivalent(songs, scope);
        var external = songs.ToDictionary(
            song => CandidateId(song.ExternalProvider!, song.ExternalId!),
            song => song);
        var candidates = localCandidates
            .Concat(groups.Select(group => ToCandidate(group[0], scope)))
            .ToArray();
        var decision = matcher.Decide(scope, source, candidates, manualOverride);
        return new(
            decision,
            external,
            groups.ToDictionary(
                group => CandidateId(group[0].ExternalProvider!, group[0].ExternalId!),
                group => (IReadOnlyList<Song>)group));
    }

    public async Task<PlayableTrackMatch?> ReuseAsync(
        ProtocolExecutionContext context,
        ExternalTrackMatchSnapshot source,
        TrackMatchScope scope,
        IEnumerable<ProviderTrackIdentityRecord> identities,
        CancellationToken cancellationToken)
    {
        var order = gateway.GetProviderOrder(ProviderCapabilityKind.Streaming)
            .Concat(gateway.GetProviderOrder(ProviderCapabilityKind.Download))
            .Select((provider, index) => (
                Provider: ExternalTrackPlaybackPolicy.Normalize(provider),
                Index: index))
            .GroupBy(item => item.Provider, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(item => item.Index), StringComparer.Ordinal);
        var cachedRoutes = identities
            .Where(identity => identity.VerificationMethod != "automatic-suggestion")
            .Where(identity => order.ContainsKey(
                ExternalTrackPlaybackPolicy.Normalize(identity.ProviderId)))
            .OrderBy(identity => order[ExternalTrackPlaybackPolicy.Normalize(identity.ProviderId)])
            .ThenByDescending(identity => identity.Verification == ProviderIdentityVerification.Pinned)
            .ToArray();
        foreach (var cached in cachedRoutes)
        {
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
            if (song == null) continue;

            var candidate = ToCandidate(song, scope);
            var score = matcher.ScoreCandidates(source, [candidate]).Single();
            var reasons = score.Reasons
                .Prepend("verified_provider_identity")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new(
                new TrackMatchDecision(
                    TrackMatchReviewState.Accepted,
                    candidate.LibraryTrackId,
                    candidate.BackendItemId,
                    1,
                    [score with { Confidence = 1, Reasons = reasons }],
                    reasons,
                    [],
                    scope.PolicyVersion,
                    scope.SourceSnapshotVersion),
                new Dictionary<Guid, Song> { [candidate.LibraryTrackId] = song },
                new Dictionary<Guid, IReadOnlyList<Song>> { [candidate.LibraryTrackId] = [song] });
        }
        return null;
    }

    public bool CanUseProvider(string? providerId)
    {
        var normalized = ExternalTrackPlaybackPolicy.Normalize(providerId);
        return normalized.Length > 0 &&
               gateway.GetProviderOrder(ProviderCapabilityKind.Streaming)
                   .Concat(gateway.GetProviderOrder(ProviderCapabilityKind.Download))
                   .Any(provider => ExternalTrackPlaybackPolicy.Normalize(provider) == normalized);
    }

    private bool IsPlayable(Song song)
    {
        return ExternalTrackPlaybackPolicy.CanUseForPlayback(song) &&
               CanUseProvider(song.ExternalProvider);
    }

    private static LocalTrackMatchCandidate ToCandidate(Song song, TrackMatchScope scope) => new(
        CandidateId(song.ExternalProvider!, song.ExternalId!),
        scope.TenantId,
        scope.UserId,
        scope.BackendInstanceId,
        scope.LibraryScopeId,
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

    private List<Song[]> GroupEquivalent(IEnumerable<Song> songs, TrackMatchScope scope)
    {
        var order = gateway.GetProviderOrder(ProviderCapabilityKind.Streaming)
            .Concat(gateway.GetProviderOrder(ProviderCapabilityKind.Download))
            .Select((provider, index) => (Provider: ExternalTrackPlaybackPolicy.Normalize(provider), Index: index))
            .GroupBy(item => item.Provider, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(item => item.Index), StringComparer.Ordinal);
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

    private async Task<AllstarrPrincipal?> ResolvePrincipalAsync(CancellationToken cancellationToken)
    {
        if (_principal != null)
        {
            return _principal;
        }

        var principalId = jellyfinSettings.Value.UserId;
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return null;
        }

        await _principalLock.WaitAsync(cancellationToken);
        try
        {
            if (_principal != null)
            {
                return _principal;
            }

            _principal = await identities.ResolveAsync(new BackendIdentityDescriptor(
                "jellyfin",
                principalId,
                BackendInstanceId: identityOptions.BackendInstanceId), cancellationToken);
            if (_principal == null)
            {
                logger.LogWarning(
                    "Playlist matching could not resolve the Jellyfin user; falling back to deployment-level providers");
            }
            return _principal;
        }
        finally
        {
            _principalLock.Release();
        }
    }
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
