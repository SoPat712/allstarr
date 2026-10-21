using System.Net;
using System.Diagnostics;
using allstarr.Core.Health;
using allstarr.Core.Music;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Core.Downloads;
using allstarr.Core.ManagedFiles;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Settings;
using allstarr.Models.Subsonic;
using allstarr.Services;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Protocols;

/// <summary>
/// The provider boundary used by protocol adapters for synthesized resources.
/// Native backend resources never enter this gateway and remain transparent relays.
/// </summary>
public interface IProtocolProviderGateway
{
    IReadOnlyList<string> GetProviderOrder(ProviderCapabilityKind capability);

    Task<IReadOnlyList<string>> GetPlayableProviderOrderAsync(
        ProviderActorContext actor,
        CancellationToken cancellationToken = default);

    Task<SearchResult> SearchAsync(
        ProtocolExecutionContext protocol,
        string query,
        int songLimit,
        int albumLimit,
        int artistLimit,
        string? providerId = null);

    Task<IReadOnlyList<Song>> SearchPlayableSongsAsync(
        ProtocolExecutionContext protocol,
        string query,
        int limit);

    Task<IReadOnlyList<Song>> SearchAutomaticSongsAsync(
        ProtocolExecutionContext protocol,
        string query,
        int limit);

    Task<Song?> GetSongAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

    Task<Song?> GetStoredSongAsync(ProtocolExecutionContext protocol, string providerId, string externalId) =>
        Task.FromResult<Song?>(null);

    Task<Album?> GetStoredAlbumAsync(ProtocolExecutionContext protocol, string providerId, string externalId) =>
        Task.FromResult<Album?>(null);

    Task<Artist?> GetStoredArtistAsync(ProtocolExecutionContext protocol, string providerId, string externalId) =>
        Task.FromResult<Artist?>(null);

    Task RememberTrackAsync(ProtocolExecutionContext protocol, string itemId);

    Task<Song?> GetPublicSongAsync(string providerId, string externalId, CancellationToken cancellationToken = default);

    Task<Album?> GetAlbumAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

    Task<Artist?> GetArtistAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

    Task<Uri?> GetPublicArtworkUriAsync(
        string providerId,
        ProviderResourceKind resourceKind,
        string externalId,
        CancellationToken cancellationToken = default);

    Task<List<Album>> GetArtistAlbumsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId);

    Task<List<Song>> GetArtistTracksAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId);

    Task<List<Song>> GetSimilarSongsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        int limit) => Task.FromResult(new List<Song>());

    Task<List<Artist>> GetSimilarArtistsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        int limit) => Task.FromResult(new List<Artist>());

    Task<List<ExternalPlaylist>> SearchPlaylistsAsync(
        ProtocolExecutionContext protocol,
        string query,
        int limit);

    Task<ExternalPlaylist?> GetPlaylistAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId);

    Task<ProviderPlaylistArtwork?> ResolvePlaylistArtworkAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        int maximumBytes) => Task.FromResult<ProviderPlaylistArtwork?>(null);

    Task<List<Song>> GetPlaylistTracksAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId);

    Task<ProtocolProviderStream?> OpenStreamAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        ProviderAudioQuality quality,
        string? rangeHeader,
        bool headOnly = false);

    Task<ProviderLyricsResult?> GetRecordingLyricsAsync(
        ProtocolExecutionContext protocol, string providerId, Guid recordingId,
        ProviderLyricsFormat? preferredFormat = null, string? trackTitle = null,
        IReadOnlyList<string>? artistNames = null, string? albumTitle = null, int? durationSeconds = null,
        bool householdOnly = false, string? isrc = null);

    Task<ProviderLyricsResult?> GetHouseholdLyricsAsync(
        ProtocolExecutionContext protocol, string providerId, string externalId,
        ProviderLyricsFormat? preferredFormat = null, string? trackTitle = null,
        IReadOnlyList<string>? artistNames = null, string? albumTitle = null, int? durationSeconds = null, string? isrc = null);

    Task<ProviderLyricsResult?> GetLyricsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        ProviderLyricsFormat? preferredFormat = null,
        string? trackTitle = null,
        IReadOnlyList<string>? artistNames = null,
        string? albumTitle = null,
        int? durationSeconds = null,
        string? isrc = null);
}

public sealed class ProtocolProviderAccountAccessException()
    : UnauthorizedAccessException("This song plays from another listener's Apple Music account. Ask an administrator to share it.");

public sealed class ProtocolProviderUnavailableException(ProviderError error)
    : HttpRequestException(error.SafeMessage, null, HttpStatusCode.ServiceUnavailable);

public sealed record ProtocolProviderStream(
    HttpResponseMessage Response,
    ProviderStreamLease Lease,
    string ServingProviderId,
    string? ServingExternalId = null,
    Guid? ServingAccountId = null,
    bool IsCached = false,
    ProviderAudioQuality EffectiveQuality = ProviderAudioQuality.Any,
    string SelectionReason = "provider-priority",
    long? ServingAccountRevision = null)
{
    internal object ObservationToken { get; } = new();
    internal PlaybackFileFacts FileFacts { get; init; } = new();
    public Guid? ServingManagedFileId { get; init; }
}

internal sealed class PlaybackFileFacts
{
    private long size = -1;
    public long? Size
    {
        get { var value = Interlocked.Read(ref size); return value < 0 ? null : value; }
        set => Interlocked.Exchange(ref size, value ?? -1);
    }
    public ProviderMediaFormat? Media { get; set; }
}

public sealed partial class ProtocolProviderGateway(
    IProviderRouter router,
    IProviderRegistry registry,
    IHttpClientFactory httpClientFactory,
    IConfiguration? configuration = null,
    IApplicationCache? applicationCache = null,
    ILogger<ProtocolProviderGateway>? logger = null,
    ManagedTrackCacheService? managedTrackCache = null,
    PlaybackDeliveryActivityStore? playbackActivity = null,
    IEffectiveProviderPolicyResolver? effectivePolicies = null,
    ITrackIdentityService? identities = null,
    IProviderOutcomeObserver? outcomes = null,
    RecordingKeptFiles? keptFiles = null,
    TimeProvider? timeProvider = null,
    IOptions<CacheSettings>? searchSettings = null) : IProtocolProviderGateway
{
    private const string StreamingClientName = "ProtocolProviderStreaming";
    private const int ProviderSearchConcurrency = 4;
    private const int RelationshipSearchLimit = 10;
    private static readonly TimeSpan ExactRouteMissTtl = TimeSpan.FromMinutes(2);

    public async Task<Uri?> GetPublicArtworkUriAsync(
        string providerId,
        ProviderResourceKind resourceKind,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        providerId = NormalizeProvider(providerId);
        if (resourceKind == ProviderResourceKind.Track &&
            await GetStoredSongAsync(PublicMetadataContext(cancellationToken), providerId, externalId) is { CoverArtUrl: { } cover } &&
            Uri.TryCreate(cover, UriKind.Absolute, out var storedArtwork))
            return storedArtwork;
        if (providerId != "allstarr" && (!IsPublicMetadataProvider(providerId) ||
            !ResolveProviderOrder(ProviderCapabilityKind.Metadata).Contains(providerId))) return null;
        if (resourceKind is not (ProviderResourceKind.Track or ProviderResourceKind.Album or
            ProviderResourceKind.Artist or ProviderResourceKind.Playlist)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var candidate = await PlanPublicMetadataAsync(providerId, "public-artwork", timeout.Token,
            resourceKind == ProviderResourceKind.Track ? externalId : null);
        if (candidate == null) return null;
        var id = candidate.TrackId ?? new ProviderExternalResourceId(providerId, resourceKind, externalId);
        ProviderArtworkReference? artwork = null;
        switch (resourceKind)
        {
            case ProviderResourceKind.Track:
                var track = await candidate.Implementation.GetTrackAsync(candidate.Context, new(id));
                if (track.IsSuccess) artwork = track.RequireValue().Artwork;
                break;
            case ProviderResourceKind.Album:
                var album = await candidate.Implementation.GetAlbumAsync(candidate.Context, new(id));
                if (album.IsSuccess) artwork = album.RequireValue().Artwork;
                break;
            case ProviderResourceKind.Artist:
                var artist = await candidate.Implementation.GetArtistAsync(candidate.Context, new(id));
                if (artist.IsSuccess) artwork = artist.RequireValue().Artwork;
                break;
            case ProviderResourceKind.Playlist:
                var playlist = await candidate.Implementation.GetPlaylistArtworkAsync(candidate.Context, id);
                if (playlist.IsSuccess) artwork = playlist.RequireValue();
                break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return artwork?.PublicUri;
    }

    public async Task<Song?> GetPublicSongAsync(string providerId, string externalId,
        CancellationToken cancellationToken = default)
    {
        providerId = NormalizeProvider(providerId);
        if (await GetStoredSongAsync(PublicMetadataContext(cancellationToken), providerId, externalId) is { } stored)
            return stored;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var candidate = await PlanPublicMetadataAsync(providerId, "public-playback-metadata", timeout.Token, externalId);
        if (candidate == null) return null;
        var outcome = await candidate.Implementation.GetTrackAsync(candidate.Context,
            new(candidate.TrackId ?? new(providerId, ProviderResourceKind.Track, externalId)));
        cancellationToken.ThrowIfCancellationRequested();
        if (outcome.IsSuccess)
        {
            var song = Map(outcome.RequireValue());
            await ApplyKnownPublicIdsAsync(candidate.Context, [song]);
            return song;
        }
        if (outcome.Error!.Kind is ProviderErrorKind.NotFound or ProviderErrorKind.NotSupported or
            ProviderErrorKind.CapabilityUnavailable) return null;
        ThrowRouteFailure(outcome.Error);
        return null;
    }

    private async Task<ProviderRouteCandidate<IProviderMetadataCapability>?> PlanPublicMetadataAsync(
        string providerId, string operation, CancellationToken cancellationToken, string? trackId = null)
    {
        if (providerId == "allstarr")
        {
            if (!Guid.TryParse(trackId, out var recordingId)) return null;
            var known = await router.PlanAsync<IProviderMetadataCapability>(Request(
                new(ProviderActorKind.PublicRead, null), ProviderCapabilityKind.Metadata, operation,
                ResolveProviderOrder(ProviderCapabilityKind.Metadata),
                new("allstarr", ProviderResourceKind.Track, recordingId.ToString("D")),
                deadline: DateTimeOffset.UtcNow.AddSeconds(30), cancellationToken: cancellationToken));
            return known.Candidates.FirstOrDefault();
        }
        if (!IsPublicMetadataProvider(providerId) ||
            !ResolveProviderOrder(ProviderCapabilityKind.Metadata).Contains(providerId)) return null;
        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            new(ProviderActorKind.PublicRead, null), ProviderCapabilityKind.Metadata,
            operation, [providerId], null, deadline: DateTimeOffset.UtcNow.AddSeconds(30),
            cancellationToken: cancellationToken));
        return plan.Candidates.SingleOrDefault();
    }

    public IReadOnlyList<string> GetProviderOrder(ProviderCapabilityKind capability) =>
        ResolveProviderOrder(capability);

    public async Task<IReadOnlyList<string>> GetPlayableProviderOrderAsync(
        ProviderActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.EffectiveUserId is not { } userId || userId == Guid.Empty) return [];
        var effectivePolicy = effectivePolicies == null
            ? null
            : await effectivePolicies.ResolveForUserAsync(
                userId, cancellationToken);
        var configuredOrder = ResolveProviderOrder(ProviderCapabilityKind.Streaming);
        if (effectivePolicy != null)
            configuredOrder = effectivePolicy.ApplyProviderAvailability(
                ProviderCapabilityKind.Streaming, configuredOrder);
        return await ResolvePlayableProviderOrderAsync(
            actor,
            configuredOrder,
            correlationId: "provider-playable-order",
            deadline: DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken: cancellationToken);
    }

    private Task<EffectiveProviderPolicySnapshot?> ResolveEffectivePolicyAsync(ProtocolExecutionContext protocol)
    {
        var userId = protocol.Actor?.EffectiveUserId;
        if (protocol.Policy is { } policy && policy.UserId == userId)
            return Task.FromResult<EffectiveProviderPolicySnapshot?>(policy);
        return ResolveAsync();

        async Task<EffectiveProviderPolicySnapshot?> ResolveAsync() => effectivePolicies == null
            ? null
            : userId is { } resolvedUserId
                ? await effectivePolicies.ResolveForUserAsync(resolvedUserId, protocol.CancellationToken)
                : await effectivePolicies.ResolveAsync(protocol.CancellationToken);
    }

    public async Task<SearchResult> SearchAsync(
        ProtocolExecutionContext protocol,
        string query,
        int songLimit,
        int albumLimit,
        int artistLimit,
        string? providerId = null)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var requestedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? null
            : NormalizeProvider(providerId);
        var actor = protocol.Actor ?? new ProviderActorContext(ProviderActorKind.PublicRead, null);
        var effectivePolicy = await ResolveEffectivePolicyAsync(protocol);
        var playableOrder = songLimit > 0
            ? await ResolvePlayableProviderOrderAsync(
                protocol,
                actor,
                ResolveProviderOrder(ProviderCapabilityKind.Streaming, effectivePolicy))
            : [];
        var playableProviders = playableOrder.ToHashSet(StringComparer.Ordinal);
        var fetchLimit = Math.Clamp(Math.Max(songLimit, Math.Max(albumLimit, artistLimit)), 1, 200);
        var providerOrder = ResolveProviderOrder(ProviderCapabilityKind.Metadata, effectivePolicy)
            .Where(item => requestedProviderId == null || item == requestedProviderId)
            .ToArray();
        if (providerOrder.Length == 0) return new SearchResult();
        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            protocol,
            actor,
            ProviderCapabilityKind.Metadata,
            "protocol-metadata-search",
            providerIds: providerOrder,
            sourceTrackId: null,
            purpose: ProviderRoutePurpose.ManualSelection));

        var routed = new SearchResult();
        var trackLookups = new List<(Song Song, TrackIdentityLookup Lookup)>();
        var settings = SearchSettings;
        var clock = timeProvider ?? TimeProvider.System;
        var searchStarted = clock.GetTimestamp();
        var searchBudget = settings.SearchBudget;
        var searchTasks = plan.Candidates.Select(async candidate =>
        {
            var snapshot = await SearchProviderWithinWindowAsync(
                candidate, query, fetchLimit, songLimit > 0, albumLimit > 0, artistLimit > 0, settings);
            return (Snapshot: snapshot, CompletedAfter: clock.GetElapsedTime(searchStarted));
        }).ToArray();
        try
        {
            var remaining = searchBudget - clock.GetElapsedTime(searchStarted);
            await Task.WhenAll(searchTasks).WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                clock, protocol.CancellationToken);
        }
        catch (TimeoutException)
        {
            logger?.LogDebug("Provider search response budget expired with {PendingCount} sources still pending",
                searchTasks.Count(task => !task.IsCompleted));
        }

        for (var index = 0; index < searchTasks.Length; index++)
        {
            if (!searchTasks[index].IsCompletedSuccessfully) continue;
            var outcome = searchTasks[index].Result;
            if (outcome.CompletedAfter > searchBudget) continue;
            var snapshot = outcome.Snapshot;
            if (snapshot == null) continue;
            var result = snapshot.Result;
            var candidate = plan.Candidates[index];
            if (playableProviders.Contains(NormalizeProvider(candidate.Provider.Id)))
            {
                for (var songIndex = 0; songIndex < result.Songs.Count; songIndex++)
                {
                    var song = result.Songs[songIndex];
                    routed.Songs.Add(song);
                    trackLookups.Add((song, new TrackIdentityLookup(candidate.Context, snapshot.TrackIds[songIndex])));
                }
            }
            routed.Albums.AddRange(result.Albums);
            routed.Artists.AddRange(result.Artists);
        }

        var songs = await CollapseVerifiedSearchTracksAsync(
            routed.Songs.Where(song => effectivePolicy?.Includes(song) != false).ToArray(),
            trackLookups.Where(item => effectivePolicy?.Includes(item.Song) != false).ToArray(),
            playableOrder, effectivePolicy, protocol);
        var knownDurationTitles = songs.Where(song => song.Duration is > 0)
            .Select(song => SearchRelevanceRanker.RecordingTitle(song.Title, song.Artist, song.AlbumArtist))
            .ToHashSet(StringComparer.Ordinal);
        return new SearchResult
        {
            Songs = MergeSearch(songs, songLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id),
                item => item.ExternalProvider, item => SearchRelevanceRanker.ScoreSong(query, item,
                    knownDurationTitles.Contains(SearchRelevanceRanker.RecordingTitle(item.Title, item.Artist, item.AlbumArtist))), providerOrder),
            Albums = MergeSearch(routed.Albums, albumLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id),
                item => item.ExternalProvider, item => SearchRelevanceRanker.Score(query, item.Title, item.Artist), providerOrder),
            Artists = MergeSearch(routed.Artists, artistLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id),
                item => item.ExternalProvider, item => SearchRelevanceRanker.Score(query, item.Name), providerOrder)
        };
    }

    private async Task<IReadOnlyList<Song>> CollapseVerifiedSearchTracksAsync(
        IReadOnlyList<Song> songs,
        IReadOnlyList<(Song Song, TrackIdentityLookup Lookup)> trackLookups,
        IReadOnlyList<string> providerOrder,
        EffectiveProviderPolicySnapshot? policy,
        ProtocolExecutionContext protocol)
    {
        var cancellationToken = protocol.CancellationToken;
        var automaticSongs = songs.Where(song => IsAutomaticProvider(NormalizeProvider(song.ExternalProvider), policy)).ToArray();
        trackLookups = trackLookups.Where(item => IsAutomaticProvider(item.Lookup.ExternalId.ProviderId, policy)).ToArray();
        var verified = Array.Empty<(Song Song, TrackIdentityResolution? Identity)>();
        if (identities != null)
        {
            try
            {
                var resolved = await identities.ResolveManyAsync(
                    trackLookups.Select(item => item.Lookup).ToArray(), cancellationToken);
                verified = trackLookups.Select((entry, index) => (entry.Song, Identity: resolved[index]))
                    .Where(item => item.Identity?.Verification == ProviderIdentityVerification.Verified &&
                                   item.Identity.VerificationMethod is not (
                                       "automatic-suggestion" or
                                       ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod or
                                       ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod))
                    .ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                logger?.LogDebug("Verified search identity lookup failed; grouping only by ISRC");
            }
        }

        // One row per proven recording: an accepted identity link proves it, and so does the same ISRC
        // from two different providers in this response.
        var groupBySong = new Dictionary<Song, string>(ReferenceEqualityComparer.Instance);
        var groupByIsrc = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (song, identity) in verified)
        {
            if (!string.IsNullOrWhiteSpace(identity!.PublicId))
            {
                song.Id = identity.PublicId;
                song.CanonicalRecordingId = identity.CanonicalRecordingId;
            }
            var group = $"recording:{identity.CanonicalRecordingId:N}";
            groupBySong[song] = group;
            if (NormalizeIsrc(song.Isrc) is { } isrc) groupByIsrc.TryAdd(isrc, group);
        }
        foreach (var song in automaticSongs)
        {
            if (groupBySong.ContainsKey(song) || NormalizeIsrc(song.Isrc) is not { } isrc) continue;
            if (!groupByIsrc.TryGetValue(isrc, out var group))
            {
                group = $"isrc:{isrc}";
                groupByIsrc[isrc] = group;
            }
            groupBySong[song] = group;
        }

        var hidden = new HashSet<Song>(ReferenceEqualityComparer.Instance);
        var evidence = new Dictionary<Song, IReadOnlyList<Song>>(ReferenceEqualityComparer.Instance);
        foreach (var song in songs) song.SearchRoutes = SearchRoutes([song]);
        foreach (var group in automaticSongs.Where(groupBySong.ContainsKey).GroupBy(song => (
                     Identity: groupBySong[song], Version: SearchVersionKey(song),
                     Mixed: FuzzyMatcher.IsContinuousMixExcerpt(song.Title, song.Album))))
            foreach (var members in PartitionSearchTracks(group, (left, right) => SearchDurationsCompatible(left, right)))
            {
                var linked = group.Key.Identity.StartsWith("recording:", StringComparison.Ordinal);
                var best = linked && members.Any(song => !string.IsNullOrWhiteSpace(song.AlbumId))
                    ? ChooseDisplaySource(protocol, members, providerOrder) :
                    members.OrderBy(song => ProviderPriority(providerOrder, song.ExternalProvider)).First();
                if (linked)
                {
                    var known = verified.FirstOrDefault(item => groupBySong[item.Song] == group.Key.Identity).Identity;
                    if (!string.IsNullOrWhiteSpace(known?.PublicId))
                    {
                        best.Id = known.PublicId;
                        best.CanonicalRecordingId = known.CanonicalRecordingId;
                    }
                }
                // An ISRC alone only merges across providers; same-provider duplicates (album and single,
                // for example) stay separate rows unless an accepted link already proves they're one recording.
                if (!linked && members.Select(song => NormalizeProvider(song.ExternalProvider)).Distinct().Count() < 2)
                    continue;
                best.SearchRoutes = SearchRoutes(members);
                evidence[best] = members;
                foreach (var song in members)
                {
                    if (ReferenceEquals(song, best)) continue;
                    if (linked || NormalizeProvider(song.ExternalProvider) != NormalizeProvider(best.ExternalProvider))
                        hidden.Add(song);
                }
            }
        return CollapseMetadataSearchTracks(songs.Where(song => !hidden.Contains(song)).ToArray(),
            providerOrder, policy, protocol, evidence);
    }

    private static string? NormalizeIsrc(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc)) return null;
        var normalized = new string(isrc.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return normalized.Length == 12 ? normalized : null;
    }

    private static int ProviderPriority(IReadOnlyList<string> order, string? provider)
    {
        for (var index = 0; index < order.Count; index++)
        {
            if (NormalizeProvider(order[index]) == NormalizeProvider(provider)) return index;
        }
        return int.MaxValue;
    }

    private static async Task<ProviderOutcome<ProviderPage<T>>?> TrySearchAsync<T>(
        Func<Task<ProviderOutcome<ProviderPage<T>>>> search,
        CancellationToken cancellationToken,
        bool enabled = true)
    {
        if (!enabled) return null;
        try
        {
            return await search();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<Song>> SearchPlayableSongsAsync(
        ProtocolExecutionContext protocol, string query, int limit) =>
        SearchSongsAsync(protocol, query, limit, ProviderRoutePurpose.ManualSelection);

    public Task<IReadOnlyList<Song>> SearchAutomaticSongsAsync(
        ProtocolExecutionContext protocol, string query, int limit) =>
        SearchSongsAsync(protocol, query, limit, ProviderRoutePurpose.Automatic);

    private async Task<IReadOnlyList<Song>> SearchSongsAsync(
        ProtocolExecutionContext protocol, string query, int limit, ProviderRoutePurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        limit = Math.Clamp(limit, 1, 200);
        var configuredProviderOrder = ResolveProviderOrder(ProviderCapabilityKind.Streaming)
            .Select(NormalizeProvider)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (configuredProviderOrder.Length == 0) return [];

        var actor = protocol.Actor ?? new ProviderActorContext(ProviderActorKind.PublicRead, null);
        var effectivePolicy = await ResolveEffectivePolicyAsync(protocol);
        if (effectivePolicy != null)
        {
            configuredProviderOrder = ResolveProviderOrder(
                    ProviderCapabilityKind.Streaming,
                    effectivePolicy)
                .Select(NormalizeProvider)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        var providerOrder = await ResolvePlayableProviderOrderAsync(
            protocol, actor, configuredProviderOrder);
        if (purpose == ProviderRoutePurpose.Automatic)
            providerOrder = providerOrder.Where(item => IsAutomaticProvider(item, effectivePolicy)).ToArray();
        if (providerOrder.Count == 0) return [];
        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            protocol,
            actor,
            ProviderCapabilityKind.Metadata,
            "protocol-playable-track-search",
            providerIds: providerOrder,
            sourceTrackId: null,
            purpose: purpose));
        var snapshots = await SearchPlayableProvidersWithinBudgetAsync(
            plan.Candidates, query, limit, protocol.CancellationToken);
        var songs = snapshots.SelectMany(snapshot => snapshot.Result.Songs)
            .Where(song => effectivePolicy?.Includes(song) != false)
            .Where(song => providerOrder.Contains(
                NormalizeProvider(song.ExternalProvider), StringComparer.Ordinal));
        // Matching needs every provider route so equivalent recordings remain available as fallbacks.
        return Merge(
            songs,
            limit,
            item => Key(item.ExternalProvider, item.ExternalId, item.Id),
            item => item.ExternalProvider,
            providerOrder);
    }

    private async Task<IReadOnlyList<string>> ResolvePlayableProviderOrderAsync(
        ProtocolExecutionContext protocol,
        ProviderActorContext actor,
        IReadOnlyList<string> configuredProviderOrder) =>
        await ResolvePlayableProviderOrderAsync(
            actor,
            configuredProviderOrder,
            protocol.CorrelationId,
            protocol.Deadline,
            protocol.CancellationToken);

    private async Task<IReadOnlyList<string>> ResolvePlayableProviderOrderAsync(
        ProviderActorContext actor,
        IReadOnlyList<string> configuredProviderOrder,
        string correlationId,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        if (configuredProviderOrder.Count == 0) return [];
        if (actor.Kind == ProviderActorKind.PublicRead)
            return configuredProviderOrder.Where(IsPublicStreamingProvider).ToArray();
        var streaming = await router.PlanAsync<IProviderStreamingCapability>(Request(
            actor,
            ProviderCapabilityKind.Streaming,
            "protocol-playable-provider-check",
            configuredProviderOrder,
            sourceTrackId: null,
            correlationId: correlationId,
            deadline: deadline,
            cancellationToken: cancellationToken,
            purpose: ProviderRoutePurpose.ManualSelection));
        var allowed = streaming.Candidates.Select(item => item.Provider.Id)
            .Select(NormalizeProvider)
            .ToHashSet(StringComparer.Ordinal);
        return configuredProviderOrder.Where(allowed.Contains).ToArray();
    }

    public async Task<Song?> GetSongAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        var routedProviderId = NormalizeProvider(providerId);
        var policy = await ResolveEffectivePolicyAsync(protocol);
        if (routedProviderId != "allstarr" && policy?.IsEnabled(routedProviderId) == false)
            throw new UnauthorizedAccessException("That source is disabled for this listener.");
        var stored = await GetStoredSongAsync(protocol, routedProviderId, externalId);
        if (stored != null && (NormalizeProvider(stored.ExternalProvider) == "allstarr" ||
            string.IsNullOrWhiteSpace(stored.Album) || !string.IsNullOrWhiteSpace(stored.AlbumId)))
            return stored;
        if ((routedProviderId == "allstarr" || IsAutomaticProvider(routedProviderId, policy)) &&
            identities != null && await identities.ResolveRecordingAsync(
                protocol.Actor ?? new ProviderActorContext(ProviderActorKind.PublicRead, null),
                $"ext-{routedProviderId}-song-{externalId}", protocol.CancellationToken) != null)
            return await GetKnownSongAsync(protocol, routedProviderId, externalId) ?? stored;
        if (stored != null) return stored;
        if (routedProviderId == "allstarr") return await GetKnownSongAsync(protocol, routedProviderId, externalId);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, routedProviderId, ProviderCapabilityKind.Metadata, "protocol-metadata-get-track");
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(routedProviderId, ProviderResourceKind.Track, externalId);
            if (await ReadCachedSongAsync(routed.Candidate.Context.Actor, routedProviderId, externalId,
                    routed.Candidate.Context.Account) is { } cached)
            {
                await ApplyKnownPublicIdsAsync(routed.Candidate.Context, [cached], protocol);
                return (await ResolveEffectivePolicyAsync(protocol))?.Includes(cached) == false ? null : cached;
            }
            var outcome = await routed.Candidate.Implementation.GetTrackAsync(
                routed.Candidate.Context,
                new ProviderTrackLookupRequest(id));
            if (outcome.IsSuccess)
            {
                var song = await EnrichRelationshipsAsync(routed.Candidate, outcome.RequireValue());
                await CacheSongsAsync(routed.Candidate.Context, [song], true);
                await ApplyKnownPublicIdsAsync(routed.Candidate.Context, [song], protocol, policy);
                return policy?.Includes(song) == false ? null : song;
            }
            if (outcome.Error!.Kind == ProviderErrorKind.NotFound)
                return await GetKnownSongAsync(protocol, routedProviderId, externalId);
            ThrowRouteFailure(outcome.Error);
        }

        return await GetKnownSongAsync(protocol, routedProviderId, externalId);
    }

    public async Task<Album?> GetAlbumAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        var routedProviderId = NormalizeProvider(providerId);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, routedProviderId, ProviderCapabilityKind.Metadata, "protocol-metadata-get-album");
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(routedProviderId, ProviderResourceKind.Album, externalId);
            if (await ReadCachedMetadataAsync<Album>(routed.Candidate.Context.Actor, routedProviderId,
                    ProviderResourceKind.Album, externalId, true, routed.Candidate.Context.Account) is { } cached)
            {
                var policy = await ResolveEffectivePolicyAsync(protocol);
                cached.Songs = cached.Songs.Where(song => policy?.Includes(song) != false).ToList();
                await ApplyKnownPublicIdsAsync(routed.Candidate.Context, cached.Songs, protocol);
                return cached;
            }
            var outcome = await routed.Candidate.Implementation.GetAlbumAsync(
                routed.Candidate.Context,
                new ProviderAlbumLookupRequest(id));
            if (outcome.IsSuccess)
            {
                var album = Map(outcome.RequireValue());
                await CacheResourcesAsync(routed.Candidate.Context, [album], [], true);
                var policy = await ResolveEffectivePolicyAsync(protocol);
                album.Songs = album.Songs.Where(song => policy?.Includes(song) != false).ToList();
                await ApplyKnownPublicIdsAsync(routed.Candidate.Context, album.Songs, protocol);
                return album;
            }
            if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return null;
            ThrowRouteFailure(outcome.Error);
        }

        return null;
    }

    public async Task<Artist?> GetArtistAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        var routedProviderId = NormalizeProvider(providerId);
        if (await ReadStoredMetadataAsync<Artist>(protocol, routedProviderId, ProviderResourceKind.Artist, externalId, true) is { } stored) return stored;
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, routedProviderId, ProviderCapabilityKind.Metadata, "protocol-metadata-get-artist");
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(routedProviderId, ProviderResourceKind.Artist, externalId);
            if (await ReadCachedMetadataAsync<Artist>(routed.Candidate.Context.Actor, routedProviderId,
                    ProviderResourceKind.Artist, externalId, true, routed.Candidate.Context.Account) is { } cached)
                return cached;
            var outcome = await routed.Candidate.Implementation.GetArtistAsync(
                routed.Candidate.Context,
                new ProviderArtistLookupRequest(id));
            if (outcome.IsSuccess)
            {
                var artist = Map(outcome.RequireValue());
                await CacheResourcesAsync(routed.Candidate.Context, [], [artist], true);
                return artist;
            }
            if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return null;
            ThrowRouteFailure(outcome.Error);
        }

        return null;
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var routedProviderId = NormalizeProvider(providerId);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, routedProviderId, ProviderCapabilityKind.Metadata, "protocol-metadata-get-artist-albums");
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(routedProviderId, ProviderResourceKind.Artist, externalId);
            var albums = new List<Album>();
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                var outcome = await routed.Candidate.Implementation.GetArtistAlbumsAsync(
                    routed.Candidate.Context,
                    new ProviderArtistItemsRequest(id, new ProviderPageRequest(200, cursor)));
                if (!outcome.IsSuccess)
                {
                    if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return [];
                    if (outcome.Error.Kind is ProviderErrorKind.NotSupported or ProviderErrorKind.CapabilityUnavailable)
                        return [];
                    ThrowRouteFailure(outcome.Error);
                }
                var page = outcome.RequireValue();
                albums.AddRange(page.Items.Select(Map));
                cursor = page.NextCursor;
                if (cursor != null && (seenCursors.Count >= 1_000 || !seenCursors.Add(cursor)))
                    throw new HttpRequestException("The provider repeated an artist-album page cursor.");
            } while (cursor != null);
            await ApplyKnownPublicIdsAsync(routed.Candidate.Context, albums.SelectMany(album => album.Songs).ToArray(), protocol);
            await CacheResourcesAsync(routed.Candidate.Context, albums, []);
            return albums;
        }

        return [];
    }

    public async Task<List<Song>> GetSimilarSongsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var routedProviderId = NormalizeProvider(providerId);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, routedProviderId, ProviderCapabilityKind.Metadata, "protocol-metadata-similar-tracks");
        if (routed.Candidate == null) return [];
        var outcome = await routed.Candidate.Implementation.GetSimilarTracksAsync(
            routed.Candidate.Context,
            new ProviderSimilarTracksRequest(
                new ProviderExternalResourceId(routedProviderId, ProviderResourceKind.Track, externalId),
                Math.Clamp(limit, 1, 200)));
        if (!outcome.IsSuccess) return [];
        var tracks = outcome.RequireValue().Items.Select(Map).ToList();
        var policy = await ResolveEffectivePolicyAsync(protocol);
        await ApplyKnownPublicIdsAsync(routed.Candidate.Context, tracks, protocol);
        return tracks.Where(song => policy?.Includes(song) != false).ToList();
    }

    public async Task<List<Artist>> GetSimilarArtistsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var routedProviderId = NormalizeProvider(providerId);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, routedProviderId, ProviderCapabilityKind.Metadata, "protocol-metadata-similar-artists");
        if (routed.Candidate == null) return [];
        var outcome = await routed.Candidate.Implementation.GetSimilarArtistsAsync(
            routed.Candidate.Context,
            new ProviderSimilarArtistsRequest(
                new ProviderExternalResourceId(routedProviderId, ProviderResourceKind.Artist, externalId),
                Math.Clamp(limit, 1, 200)));
        return outcome.IsSuccess ? outcome.RequireValue().Items.Select(Map).ToList() : [];
    }

    public async Task<List<Song>> GetArtistTracksAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var routedProviderId = NormalizeProvider(providerId);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, routedProviderId, ProviderCapabilityKind.Metadata, "protocol-metadata-get-artist-tracks");
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(routedProviderId, ProviderResourceKind.Artist, externalId);
            var tracks = new List<Song>();
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                var outcome = await routed.Candidate.Implementation.GetArtistTracksAsync(
                    routed.Candidate.Context,
                    new ProviderArtistItemsRequest(id, new ProviderPageRequest(200, cursor)));
                if (!outcome.IsSuccess)
                {
                    if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return [];
                    if (outcome.Error.Kind is ProviderErrorKind.NotSupported or ProviderErrorKind.CapabilityUnavailable)
                        return [];
                    ThrowRouteFailure(outcome.Error);
                }
                var page = outcome.RequireValue();
                tracks.AddRange(page.Items.Select(Map));
                cursor = page.NextCursor;
                if (cursor != null && (seenCursors.Count >= 1_000 || !seenCursors.Add(cursor)))
                    throw new HttpRequestException("The provider repeated an artist-track page cursor.");
            } while (cursor != null);
            var policy = await ResolveEffectivePolicyAsync(protocol);
            await ApplyKnownPublicIdsAsync(routed.Candidate.Context, tracks, protocol);
            return tracks.Where(song => policy?.Includes(song) != false).ToList();
        }

        return [];
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(
        ProtocolExecutionContext protocol,
        string query,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        limit = Math.Clamp(limit, 1, 200);
        if (protocol.Actor is null) return [];

        var actor = protocol.RequireActor();
        var effectivePolicy = await ResolveEffectivePolicyAsync(protocol);
        var providerOrder = ResolveProviderOrder(ProviderCapabilityKind.Playlist, effectivePolicy);
        var plan = await router.PlanAsync<IProviderPlaylistCapability>(Request(
            protocol,
            actor,
            ProviderCapabilityKind.Playlist,
            "protocol-playlist-search",
            providerIds: providerOrder,
            sourceTrackId: null));
        using var playlistSearchGate = new SemaphoreSlim(ProviderSearchConcurrency);
        var playlistTasks = plan.Candidates.Select(async candidate =>
        {
            await playlistSearchGate.WaitAsync(protocol.CancellationToken);
            try
            {
                return await candidate.Implementation.SearchPlaylistsAsync(
                    candidate.Context,
                    new ProviderPlaylistSearchRequest(query, new ProviderPageRequest(limit)));
            }
            finally
            {
                playlistSearchGate.Release();
            }
        }).ToList();

        var playlistOutcomes = await Task.WhenAll(playlistTasks);
        var routed = new List<ExternalPlaylist>();

        foreach (var outcome in playlistOutcomes)
        {
            if (outcome.IsSuccess)
            {
                routed.AddRange(outcome.RequireValue().Items.Select(Map));
            }
        }

        return routed.Take(limit).ToList();
    }

    public async Task<ExternalPlaylist?> GetPlaylistAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        var (_, summary) = await ReadPlaylistSummaryAsync(
            protocol, providerId, externalId, "protocol-playlist-get");
        return summary == null ? null : Map(summary);
    }

    public async Task<ProviderPlaylistArtwork?> ResolvePlaylistArtworkAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        int maximumBytes)
    {
        var (candidate, summary) = await ReadPlaylistSummaryAsync(
            protocol, providerId, externalId, "protocol-playlist-artwork");
        if (candidate == null || summary?.Artwork?.ResourceId == null) return null;

        var outcome = await candidate.Implementation.ResolveArtworkAsync(
            candidate.Context,
            new ProviderPlaylistArtworkRequest(summary.Artwork, maximumBytes));
        if (outcome.IsSuccess) return outcome.RequireValue();
        if (outcome.Error!.Kind is ProviderErrorKind.NotFound or
            ProviderErrorKind.NotSupported or ProviderErrorKind.CapabilityUnavailable)
            return null;
        ThrowRouteFailure(outcome.Error);
        return null;
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (protocol.Actor is null)
            throw new UnauthorizedAccessException("A resolved user is required for provider playlists.");

        providerId = NormalizeProvider(providerId);
        var routed = await PlanExactAsync<IProviderPlaylistCapability>(
            protocol, providerId, ProviderCapabilityKind.Playlist, "protocol-playlist-get-tracks");
        if (routed.Candidate != null)
        {
            var playlistId = new ProviderExternalResourceId(providerId, ProviderResourceKind.Playlist, externalId);
            var tracks = new List<Song>();
            string? cursor = null;
            do
            {
                var outcome = await routed.Candidate.Implementation.GetPlaylistTracksAsync(
                    routed.Candidate.Context,
                    new ProviderPlaylistTracksRequest(playlistId, new ProviderPageRequest(200, cursor)));
                if (!outcome.IsSuccess)
                {
                    if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return [];
                    ThrowRouteFailure(outcome.Error);
                }
                var page = outcome.RequireValue().Tracks;
                tracks.AddRange(page.Items.Where(item => item.Metadata != null).Select(item => Map(item.Metadata!)));
                cursor = page.NextCursor;
            } while (cursor != null);
            var policy = await ResolveEffectivePolicyAsync(protocol);
            await ApplyKnownPublicIdsAsync(routed.Candidate.Context, tracks, protocol);
            return tracks.Where(song => policy?.Includes(song) != false).ToList();
        }

        return [];
    }

    private async Task<(ProviderRouteCandidate<IProviderPlaylistCapability>? Candidate,
        ProviderPlaylistSummary? Summary)> ReadPlaylistSummaryAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        string operationId)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (protocol.Actor is null)
            throw new UnauthorizedAccessException("A resolved user is required for provider playlists.");

        providerId = NormalizeProvider(providerId);
        var routed = await PlanExactAsync<IProviderPlaylistCapability>(
            protocol, providerId, ProviderCapabilityKind.Playlist, operationId);
        if (routed.Candidate == null)
        {

            return (null, null);
        }

        var playlistId = new ProviderExternalResourceId(
            providerId, ProviderResourceKind.Playlist, externalId);
        var outcome = await routed.Candidate.Implementation.GetPlaylistTracksAsync(
            routed.Candidate.Context,
            new ProviderPlaylistTracksRequest(playlistId, new ProviderPageRequest(1)));
        if (outcome.IsSuccess) return (routed.Candidate, outcome.RequireValue().Playlist);
        if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return (routed.Candidate, null);
        ThrowRouteFailure(outcome.Error);
        return (routed.Candidate, null);
    }

    public async Task<ProtocolProviderStream?> OpenStreamAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        ProviderAudioQuality quality,
        string? rangeHeader,
        bool headOnly = false)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (protocol.Actor is null) return null;

        protocol.CancellationToken.ThrowIfCancellationRequested();
        var clock = timeProvider ?? TimeProvider.System;
        var streamCancellation = protocol.CancellationToken;
        var streamOpeningStartedAt = clock.GetUtcNow();
        var protocolDeadline = protocol.Deadline;
        if (protocolDeadline <= streamOpeningStartedAt)
            throw new OperationCanceledException(protocol.CancellationToken);
        using var openingTimeout = new CancellationTokenSource(protocolDeadline - streamOpeningStartedAt, clock);
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(protocol.CancellationToken, openingTimeout.Token);
        protocol = new ProtocolExecutionContext(protocol.Protocol, protocol.BackendInstanceId,
            protocol.VerifiedBackendPrincipalId, protocol.Principal, protocol.CorrelationId,
            protocolDeadline, opening.Token, protocol.Client)
        {
            Policy = protocol.Policy,
            RequestedItemId = protocol.RequestedItemId,
            IsPreparation = protocol.IsPreparation,
            PreparationQuality = protocol.PreparationQuality
        };
        providerId = NormalizeProvider(providerId);
        var actor = protocol.RequireActor();
        var effectivePolicy = await ResolveEffectivePolicyAsync(protocol);
        if (providerId != "allstarr" && effectivePolicy?.IsEnabled(providerId) == false)
            throw new UnauthorizedAccessException("That source is disabled for this listener.");
        if (quality == ProviderAudioQuality.Any && effectivePolicy != null)
        {
            quality = AudioQualityPolicy.RequestedQuality(effectivePolicy.AudioQuality);
        }
        if (protocol.IsPreparation && (managedTrackCache?.CanPrepare(protocol) != true ||
            quality == ProviderAudioQuality.DataSaver || effectivePolicy?.AudioQuality == "DataSaver")) return null;
        var parsedRange = headOnly ? null : StreamByteRanges.Parse(rangeHeader);
        var rangeStart = parsedRange?.Ranges.Single().From;
        var trackId = new ProviderExternalResourceId(
            providerId, ProviderResourceKind.Track, externalId);
        var itemId = protocol.RequestedItemId ?? ProtocolItemId(trackId);
        if (protocol.IsPreparation && playbackActivity?.WasStreamOpened(itemId) == true) return null;
        var previous = playbackActivity?.StreamFor(protocol, itemId, quality);
        var resuming = parsedRange != null && rangeStart != 0;
        var hasPlaybackIdentity = !string.IsNullOrWhiteSpace(protocol.Client.DeviceId);
        if (resuming && hasPlaybackIdentity && playbackActivity != null && previous == null)
            throw new HttpRequestException("Restart playback before seeking this external track.", null,
                HttpStatusCode.Conflict);
        // Legacy provider requests without a device retain their direct provider route across ranges.
        if ((hasPlaybackIdentity || providerId == "allstarr") &&
            (providerId == "allstarr" || IsAutomaticProvider(providerId, effectivePolicy)) &&
            (!resuming || previous?.ManagedFileId != null))
        {
            var kept = await OpenHouseholdKeptAsync(protocol, trackId, quality, previous?.ManagedFileId,
                resuming, headOnly);
            if (kept != null)
            {
                if (protocol.IsPreparation) { kept.Response.Dispose(); return null; }
                return await OpenedAsync(kept, null, null);
            }
            if (resuming && previous?.ManagedFileId != null)
                throw new HttpRequestException("The previous kept playback source is no longer available. Restart playback.",
                    null, HttpStatusCode.Conflict);
        }
        var exactRouteMissKey = CacheKeyBuilder.BuildPlaybackRouteNegativeKey(
            actor.EffectiveUserId,
            providerId,
            externalId,
            quality.ToString());
        if ((identities == null || registry.FindByCapability(ProviderCapabilityKind.Streaming).Count == 0) &&
            applicationCache?.IsEnabled == true && await applicationCache.ExistsAsync(exactRouteMissKey))
        {
            logger?.LogDebug(
                "Skipping repeated playback translation miss for chosen provider {ChosenProvider}",
                providerId);
            return null;
        }

        var availabilityOrder = StoredStreamingOrder(effectivePolicy);
        var providerOrder = ResolveProviderOrder(ProviderCapabilityKind.Streaming, effectivePolicy);
        if (effectivePolicy?.IsEnabled(providerId) != false)
        {
            providerOrder = providerOrder.Append(providerId).Distinct(StringComparer.Ordinal).ToArray();
        }
        if (!hasPlaybackIdentity && providerId != "allstarr")
        {
            if (!providerOrder.Contains(providerId, StringComparer.Ordinal)) return null;
            providerOrder = [providerId];
        }
        if (resuming)
        {
            if (!hasPlaybackIdentity && providerId == "allstarr")
                throw new HttpRequestException("Restart playback before seeking this recording.", null,
                    HttpStatusCode.Conflict);
            // Byte offsets cannot be transferred between two encodings of the same recording.
            var resumeProvider = previous?.ProviderId ?? providerId;
            if (!providerOrder.Contains(resumeProvider, StringComparer.Ordinal)) return null;
            providerOrder = [resumeProvider];
        }
        var streamRequest = Request(
            protocol,
            actor,
            ProviderCapabilityKind.Streaming,
            "protocol-stream-open",
            providerOrder,
            resuming && previous != null
                ? new ProviderExternalResourceId(previous.ProviderId, ProviderResourceKind.Track, previous.ExternalId)
                : trackId,
            quality,
            allowFallback: true,
            allowManagedDownloads: true);
        var plan = await router.PlanAsync<IProviderStreamingCapability>(streamRequest);
        var availabilityAttempted = false;
        async Task<bool> ResolveAvailabilityAsync()
        {
            if (availabilityAttempted || resuming || !hasPlaybackIdentity && providerId != "allstarr" ||
                providerId != "allstarr" && !IsAutomaticProvider(providerId, effectivePolicy)) return false;
            availabilityAttempted = true;
            var linked = await TryResolvePlaybackAvailabilityAsync(plan, availabilityOrder, trackId,
                exactRouteMissKey, effectivePolicy, quality);
            if (linked) plan = await router.PlanAsync<IProviderStreamingCapability>(streamRequest);
            return linked;
        }
        if (plan.Candidates.Count == 0) await ResolveAvailabilityAsync();
        if (plan.Candidates.Count == 0)
        {
            if (plan.Decision.Candidates.Any(item =>
                    item.ProviderId.Equals(providerId, StringComparison.Ordinal) &&
                    item.ReasonCode is not ("capability-unavailable" or "verified-identity-required")))
            {
                ThrowRejectedRoute(plan.Decision.Candidates.First(item => item.ProviderId == providerId), protocol.IsPreparation);
            }

            var isTranslationMiss = plan.Decision.Candidates.Any(item =>
                    item.ReasonCode == "verified-identity-required") &&
                plan.Decision.Candidates.All(item =>
                    item.ReasonCode is "capability-unavailable" or "verified-identity-required");
            if (isTranslationMiss && identities == null && applicationCache?.IsEnabled == true)
            {
                await applicationCache.SetStringAsync(
                    exactRouteMissKey,
                    "1",
                    ExactRouteMissTtl);
            }
            return null;
        }

        string? fallbackReason = null;
        var attemptedRoutes = new HashSet<(string Provider, Guid? Account, string Track)>();
        for (var candidateIndex = 0; candidateIndex < plan.Candidates.Count; candidateIndex++)
        {
            protocol.CancellationToken.ThrowIfCancellationRequested();
            var candidate = plan.Candidates[candidateIndex];
            if (protocol.IsPreparation && !ProviderStreamOpener.PreparationProviders.Contains(candidate.Provider.Id)) return null;
            var servingTrack = candidate.TrackId ?? trackId;
            if (!attemptedRoutes.Add((candidate.Provider.Id, candidate.Context.Account?.AccountId, servingTrack.Value))) continue;
            if (resuming && previous != null &&
                (servingTrack.Value != previous.ExternalId || candidate.Context.Account?.AccountId != previous.AccountId ||
                    candidate.Context.Account?.Revision != previous.AccountRevision))
                throw new HttpRequestException("The previous playback source is no longer available. Restart playback.", null,
                    HttpStatusCode.Conflict);
            var routeReason = string.Join(':',
                fallbackReason ?? "provider-priority",
                candidate.Context.Account?.ResolutionReason ?? "account-free");
            var cacheScope = new DownloadedSongMappingScope(
                    candidate.Context.Account?.AccountId,
                    quality);
            var coordinate = !headOnly && managedTrackCache != null &&
                ProviderStreamOpener.PreparationProviders.Contains(candidate.Provider.Id);
            using var cacheOpening = coordinate ? await managedTrackCache!.BeginOpenAsync(
                servingTrack, cacheScope, candidate.Context.Account?.Revision, protocol.IsPreparation, protocol.CancellationToken) : null;
            if (coordinate && cacheOpening == null) return null;
            var cached = managedTrackCache == null ? null : await managedTrackCache.TryOpenAsync(
                servingTrack, cacheScope, protocol.CancellationToken, candidate.Context.Account?.Revision, protocol.IsPreparation);
            if (cached != null)
            {
                if (protocol.IsPreparation) { cached.Response.Dispose(); return null; }
                return await OpenedAsync(cached with { SelectionReason = $"{routeReason}:managed-cache" }, candidate.Context, servingTrack);
            }
            var candidateDeadline = protocolDeadline;
            if (candidate.Implementation is IProviderDownloadStreamingCapability)
            {
                opening.Token.ThrowIfCancellationRequested();
                candidateDeadline = streamOpeningStartedAt.Add(GetDownloadStreamTimeout());
            }
            var candidateRemaining = candidateDeadline - clock.GetUtcNow();
            if (candidateRemaining > TimeSpan.Zero)
                openingTimeout.CancelAfter(candidateRemaining);
            else
                openingTimeout.Cancel();
            opening.Token.ThrowIfCancellationRequested();
            var candidateExecution = new ProviderExecutionContext(
                candidate.Context.Actor,
                candidate.Context.ProviderId,
                candidate.Context.Account,
                candidate.Context.Policy,
                candidate.Context.OperationId,
                candidate.Context.CorrelationId,
                candidateDeadline,
                protocol.CancellationToken,
                candidate.Context.IdempotencyKey);
            var leaseRequest = new ProviderStreamLeaseRequest(
                servingTrack,
                quality,
                rangeStart);

            var leaseFailed = false;
            async Task<ProviderOutcome<ProviderStreamLease>> ResolveLeaseAsync()
            {
                var resolved = await candidate.Implementation.GetStreamLeaseAsync(candidateExecution, leaseRequest);
                leaseFailed = !resolved.IsSuccess;
                return resolved;
            }

            async Task<HttpResponseMessage> OpenLeaseAsync(ProviderStreamLease lease)
            {
                using var request = new HttpRequestMessage(
                    headOnly ? HttpMethod.Head : HttpMethod.Get,
                    lease.ProtectedSourceUri);
                if (parsedRange != null && lease.SupportsByteRanges) request.Headers.Range = parsedRange;
                return lease.ProtectedResponseFactory != null
                    ? await lease.ProtectedResponseFactory(request, protocol.CancellationToken)
                    : await httpClientFactory.CreateClient(StreamingClientName).SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        protocol.CancellationToken);
            }

            var started = Stopwatch.GetTimestamp();
            var outcome = await OpenCandidateAsync(ResolveLeaseAsync, OpenLeaseAsync,
                headOnly, parsedRange, resuming && previous != null, protocol.CancellationToken);
            if (!leaseFailed && (!headOnly || !outcome.IsSuccess))
                outcomes?.Observe(candidate.Context, ProviderCapabilityKind.Streaming,
                    outcome.Error, Stopwatch.GetElapsedTime(started));
            if (outcome.IsSuccess)
            {
                var (response, lease) = outcome.RequireValue();
                return await OpenedAsync(new ProtocolProviderStream(response, lease, candidate.Provider.Id,
                    servingTrack.Value, candidate.Context.Account?.AccountId,
                    EffectiveQuality: quality,
                    SelectionReason: $"{routeReason}:remote",
                    ServingAccountRevision: candidate.Context.Account?.Revision), candidateExecution, servingTrack);
            }
            var fallback = resuming ? null : router.EvaluateFallback(plan, candidateIndex, outcome.Error!);
            if (fallback?.Disposition == ProviderFallbackDisposition.Advance)
            {
                if (!protocol.IsPreparation)
                    logger?.LogInformation("Playback advanced from {Provider} after {FailureCode}",
                        candidate.Provider.Id, outcome.Error!.Code);
                fallbackReason = fallback.ReasonCode.StartsWith("fallback-", StringComparison.Ordinal)
                    ? fallback.ReasonCode
                    : $"fallback-{fallback.ReasonCode}";
                continue;
            }
            if (fallback?.Disposition == ProviderFallbackDisposition.Exhausted && await ResolveAvailabilityAsync())
            {
                candidateIndex = -1;
                fallbackReason = $"fallback-{outcome.Error!.Code}";
                continue;
            }
            ThrowRouteFailure(outcome.Error!);
        }

        return null;

        async Task<ProtocolProviderStream> OpenedAsync(ProtocolProviderStream stream,
            ProviderExecutionContext? execution, ProviderExternalResourceId? servingTrack)
        {
            if (!headOnly && managedTrackCache != null && !stream.IsCached &&
                ProviderStreamOpener.PreparationProviders.Contains(stream.ServingProviderId))
            {
                Task<Song?>? metadata = null;
                Task<Song?> Metadata() => metadata ??= GetSongAsync(protocol, stream.ServingProviderId, stream.ServingExternalId!);
                try
                {
                    StreamDurationGuidance.Attach(stream, Metadata);
                    await managedTrackCache.WrapAsync(stream, protocol, stream.ServingProviderId, stream.ServingExternalId!,
                        false, Metadata, streamCancellation);
                }
                catch { stream.Response.Dispose(); throw; }
            }
            if (!headOnly && !protocol.IsPreparation && execution != null && servingTrack != null)
                await RememberPlayedTrackAsync(execution, servingTrack, itemId);
            if (!headOnly && stream.ServingProviderId == "monochrome" && !stream.Lease.SupportsByteRanges &&
                execution != null && identities != null)
            {
                try
                {
                    var known = await identities.GetRecordingAvailabilityAsync(execution, itemId, execution.CancellationToken);
                    if (known?.DurationMilliseconds is > 0 and < 86400000)
                    {
                        stream.Response.Headers.Remove("X-Allstarr-Duration-Seconds");
                        stream.Response.Headers.Add("X-Allstarr-Duration-Seconds",
                            (known.DurationMilliseconds.Value / 1000d).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger?.LogDebug("Stored playback duration is unavailable ({ErrorType})", error.GetType().Name);
                }
            }
            stream.Response.Headers.Remove("X-Allstarr-Provider");
            stream.Response.Headers.Add("X-Allstarr-Provider", stream.ServingProviderId);
            stream.Response.Headers.Remove("X-Allstarr-Route-Reason");
            stream.Response.Headers.Add("X-Allstarr-Route-Reason", stream.SelectionReason);
            if (!headOnly && !protocol.IsPreparation) playbackActivity?.StreamOpened(protocol, itemId, quality, stream);
            if (!protocol.IsPreparation)
                logger?.LogInformation("Opened external playback from {Provider} (cached: {Cached})",
                    stream.ServingProviderId, stream.IsCached);
            return stream;
        }
    }

    private TimeSpan GetDownloadStreamTimeout()
    {
        var options = new ProtocolExecutionOptions();
        configuration?.GetSection(ProtocolExecutionOptions.SectionName).Bind(options);
        return options.GetDownloadStreamTimeout();
    }

    private static async Task<ProviderOutcome<(HttpResponseMessage Response, ProviderStreamLease Lease)>> OpenCandidateAsync(
        Func<Task<ProviderOutcome<ProviderStreamLease>>> resolve,
        Func<ProviderStreamLease, Task<HttpResponseMessage>> open,
        bool headOnly,
        RangeHeaderValue? requestedRange,
        bool resumePublishedSource,
        CancellationToken cancellationToken)
    {
        ProviderStreamLease? lease = null;
        ProviderError error = new(ProviderErrorKind.TransientFailure);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage? response = null;
            var transferred = false;
            try
            {
                if (lease == null || lease.RetryBehavior == ProviderStreamRetryBehavior.RefreshLease)
                {
                    var result = await resolve();
                    if (!result.IsSuccess) return ProviderOutcome<(HttpResponseMessage, ProviderStreamLease)>.Failure(result.Error!);
                    lease = result.RequireValue();
                }
                response = await open(lease);
                if (response.IsSuccessStatusCode)
                {
                    if (!headOnly && !StreamByteRanges.Matches(response, requestedRange))
                    {
                        error = new(ProviderErrorKind.IncompatibleMedia);
                        break;
                    }
                    if (response.StatusCode == HttpStatusCode.OK && (!lease.SupportsByteRanges || requestedRange != null))
                    {
                        response.Headers.AcceptRanges.Clear();
                        response.Content.Headers.ContentRange = null;
                    }
                    response.Content.Headers.ContentType ??= new MediaTypeHeaderValue(lease.Media.MimeType);
                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    // A validated range of the published source can return headers before its bytes arrive.
                    if ((mediaType == null || mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                        mediaType is "application/octet-stream" or "video/mp4") &&
                        (headOnly || resumePublishedSource && response.StatusCode == HttpStatusCode.PartialContent ||
                         await PrefetchedStream.PrepareAsync(response, cancellationToken)))
                    {
                        transferred = true;
                        return ProviderOutcome<(HttpResponseMessage, ProviderStreamLease)>.Success((response, lease));
                    }
                    error = new(ProviderErrorKind.IncompatibleMedia);
                    break;
                }
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    var range = response.Content.Headers.ContentRange;
                    error = requestedRange != null && range is { HasRange: false, Length: { } length } &&
                        range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) &&
                        !StreamByteRanges.TryResolve(requestedRange, length, out _, out _)
                            ? ProviderError.StreamRangeUnavailable() : new(ProviderErrorKind.IncompatibleMedia);
                    break;
                }
                var retryAfter = response.Headers.RetryAfter;
                error = response.StatusCode == HttpStatusCode.TooManyRequests ? new(ProviderErrorKind.RateLimited,
                    retryAfter?.Delta ?? (retryAfter?.Date is { } retryDate
                        ? TimeSpan.FromSeconds(Math.Max(0, (retryDate - DateTimeOffset.UtcNow).TotalSeconds)) : TimeSpan.FromSeconds(30))) :
                    StreamError(response.StatusCode);
                if (!ShouldRetry(lease, response.StatusCode)) break;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                error = exception is HttpRequestException { StatusCode: { } status }
                    ? StreamError(status) : new(ProviderErrorKind.TransientFailure);
                if (lease?.RetryBehavior is null or ProviderStreamRetryBehavior.DoNotRetry ||
                    exception is HttpRequestException { StatusCode: { } failedStatus } && !ShouldRetry(lease, failedStatus)) break;
            }
            finally
            {
                if (!transferred) response?.Dispose();
            }
        }
        return ProviderOutcome<(HttpResponseMessage, ProviderStreamLease)>.Failure(error);
    }

    private static ProviderError StreamError(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound or HttpStatusCode.Gone => new(ProviderErrorKind.NotFound),
        HttpStatusCode.Unauthorized => new(ProviderErrorKind.Unauthorized),
        HttpStatusCode.Forbidden => new(ProviderErrorKind.Forbidden),
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(30)),
        HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ => new(ProviderErrorKind.PermanentFailure)
    };

    private static bool ShouldRetry(ProviderStreamLease lease, HttpStatusCode statusCode) =>
        lease.RetryBehavior != ProviderStreamRetryBehavior.DoNotRetry &&
        (statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
         statusCode >= HttpStatusCode.InternalServerError ||
         lease.RetryBehavior == ProviderStreamRetryBehavior.RefreshLease &&
         statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

    public Task<ProviderLyricsResult?> GetRecordingLyricsAsync(
        ProtocolExecutionContext protocol, string providerId, Guid recordingId,
        ProviderLyricsFormat? preferredFormat = null, string? trackTitle = null,
        IReadOnlyList<string>? artistNames = null, string? albumTitle = null, int? durationSeconds = null,
        bool householdOnly = false, string? isrc = null)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (recordingId == Guid.Empty) throw new ArgumentException("A recording ID is required.", nameof(recordingId));
        protocol.CancellationToken.ThrowIfCancellationRequested();
        return GetLyricsCoreAsync(protocol, providerId, recordingId.ToString("D"), preferredFormat, trackTitle,
            artistNames, albumTitle, durationSeconds, householdOnly ? [ProviderAccountScope.Shared] : null, recordingId, isrc);
    }

    public Task<ProviderLyricsResult?> GetHouseholdLyricsAsync(
        ProtocolExecutionContext protocol, string providerId, string externalId,
        ProviderLyricsFormat? preferredFormat = null, string? trackTitle = null,
        IReadOnlyList<string>? artistNames = null, string? albumTitle = null, int? durationSeconds = null, string? isrc = null) =>
        GetLyricsCoreAsync(protocol, providerId, externalId, preferredFormat, trackTitle, artistNames, albumTitle,
            durationSeconds, [ProviderAccountScope.Shared], isrc: isrc);

    public Task<ProviderLyricsResult?> GetLyricsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        ProviderLyricsFormat? preferredFormat = null,
        string? trackTitle = null,
        IReadOnlyList<string>? artistNames = null,
        string? albumTitle = null,
        int? durationSeconds = null,
        string? isrc = null) =>
        GetLyricsCoreAsync(protocol, providerId, externalId, preferredFormat, trackTitle, artistNames, albumTitle,
            durationSeconds, null, isrc: isrc);

    private async Task<ProviderLyricsResult?> GetLyricsCoreAsync(
        ProtocolExecutionContext protocol, string providerId, string externalId, ProviderLyricsFormat? preferredFormat,
        string? trackTitle, IReadOnlyList<string>? artistNames, string? albumTitle, int? durationSeconds,
        IReadOnlyList<ProviderAccountScope>? allowedAccountScopes, Guid? recordingId = null, string? isrc = null)
    {
        providerId = NormalizeProvider(providerId);
        var trackId = new ProviderExternalResourceId(providerId, ProviderResourceKind.Track, externalId);
        var sourceTrackId = recordingId.HasValue
            ? new ProviderExternalResourceId("allstarr", ProviderResourceKind.Track, recordingId.Value.ToString("D")) : trackId;
        var routed = await PlanExactAsync<IProviderLyricsCapability>(
            protocol,
            providerId,
            ProviderCapabilityKind.Lyrics,
            "protocol-lyrics-get",
            sourceTrackId, allowedAccountScopes: allowedAccountScopes);
        if (routed.Candidate == null || recordingId.HasValue && routed.Candidate.TrackId == null) return null;
        var canonicalBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{providerId}\n{externalId}"));
        var canonicalId = recordingId ?? new Guid(canonicalBytes.AsSpan(0, 16));
        var outcome = await routed.Candidate.Implementation.FetchLyricsAsync(
            routed.Candidate.Context,
            new ProviderLyricsRequest(
                canonicalId,
                routed.Candidate.TrackId ?? trackId,
                preferredFormat: preferredFormat,
                trackTitle: trackTitle,
                artistNames: artistNames,
                albumTitle: albumTitle,
                durationSeconds: durationSeconds,
                isrc: isrc));
        if (outcome.IsSuccess)
        {
            var result = outcome.RequireValue();
            return result.Availability == ProviderLyricsAvailabilityState.Available ? result : null;
        }
        if (outcome.Error!.Kind is ProviderErrorKind.NotFound or ProviderErrorKind.CapabilityUnavailable or ProviderErrorKind.NotSupported)
            return null;
        ThrowRouteFailure(outcome.Error);
        return null;
    }

    private async Task<(ProviderRouteCandidate<TCapability>? Candidate, ProviderRoutePlan<TCapability> Plan)>
        PlanExactAsync<TCapability>(
            ProtocolExecutionContext protocol,
            string providerId,
            ProviderCapabilityKind capability,
            string operationId,
            ProviderExternalResourceId? sourceTrackId = null,
            ProviderAudioQuality quality = ProviderAudioQuality.Any,
            IReadOnlyList<ProviderAccountScope>? allowedAccountScopes = null)
        where TCapability : class, IProviderCapability
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var plan = await router.PlanAsync<TCapability>(Request(
            protocol,
            protocol.Actor ?? (capability == ProviderCapabilityKind.Metadata && IsPublicMetadataProvider(providerId) &&
                ResolveProviderOrder(capability).Contains(providerId)
                ? new ProviderActorContext(ProviderActorKind.PublicRead, null)
                : protocol.RequireActor()),
            capability,
            operationId,
            [providerId],
            sourceTrackId,
            quality, allowedAccountScopes: allowedAccountScopes,
            purpose: ProviderRoutePurpose.ManualSelection));
        var candidate = plan.Candidates.SingleOrDefault();
        if (candidate == null && plan.Decision.Candidates.Any(item =>
                item.ProviderId.Equals(providerId, StringComparison.Ordinal) &&
                item.ReasonCode != "capability-unavailable"))
        {
            ThrowRejectedRoute(plan.Decision.Candidates.First(item => item.ProviderId == providerId));
        }
        return (candidate, plan);
    }

    private bool IsPublicMetadataProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        return registry.FindByCapability(ProviderCapabilityKind.Metadata, includeNonOperational: true)
            .Any(descriptor => descriptor.Id.Equals(NormalizeProvider(providerId), StringComparison.Ordinal) &&
                               (descriptor.Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Metadata).AccountRequirement == ProviderAccountRequirement.None ||
                                descriptor.Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Metadata).SupportsPublicRead));
    }

    private bool IsPublicStreamingProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        return registry.FindByCapability(ProviderCapabilityKind.Streaming)
            .Any(descriptor => descriptor.Id.Equals(NormalizeProvider(providerId), StringComparison.Ordinal) &&
                               descriptor.Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Streaming)
                                   .AccountRequirement == ProviderAccountRequirement.None);
    }

    private bool IsAutomaticProvider(string providerId, EffectiveProviderPolicySnapshot? policy) =>
        policy?.IsAutomatic(providerId) ??
        (!registry.TryGet(providerId, out var provider) || provider!.DefaultUseMode == ProviderUseMode.Automatic);

    private IReadOnlyList<string> ResolveProviderOrder(
        ProviderCapabilityKind capability,
        EffectiveProviderPolicySnapshot? effectivePolicy = null)
    {
        var definition = ProviderOrderPolicyCatalog.Find(capability);
        if (definition == null) return [];
        var availableProviders = registry.FindByCapability(capability, includeNonOperational: true)
            .Select(provider => NormalizeProvider(provider.Id))
            .Concat(capability == ProviderCapabilityKind.Lyrics ? ["backend", "kept"] : Array.Empty<string>())
            .ToArray();
        if (effectivePolicy != null)
        {
            return effectivePolicy.ApplyProviderAvailability(capability, availableProviders);
        }
        var configured = (configuration?[definition.SettingKey] ??
                          configuration?[definition.BootstrapKey] ??
                          definition.DefaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var disabled = (configuration?["Providers:Disabled"] ??
                        configuration?["MULTI_PROVIDER_DISABLED_PROVIDERS"] ??
                        string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeProvider)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var ordered = ProviderOrderPolicyCatalog.NormalizeOrder(capability, configured)
            .Where(providerId => !disabled.Contains(providerId))
            .Where(providerId => availableProviders.Contains(providerId, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        ordered.AddRange(availableProviders
            .Where(providerId => !disabled.Contains(providerId) &&
                                 !ordered.Contains(providerId, StringComparer.Ordinal))
            .OrderBy(providerId => providerId == "monochrome")
            .ThenBy(providerId => providerId, StringComparer.Ordinal));
        return ordered;
    }

    private static ProviderRouteRequest Request(
        ProtocolExecutionContext protocol,
        ProviderActorContext actor,
        ProviderCapabilityKind capability,
        string operationId,
        IEnumerable<string> providerIds,
        ProviderExternalResourceId? sourceTrackId,
        ProviderAudioQuality quality = ProviderAudioQuality.Any,
        bool allowFallback = false,
        bool allowManagedDownloads = false,
        string? idempotencyKey = null,
        IReadOnlyList<ProviderAccountScope>? allowedAccountScopes = null,
        ProviderRoutePurpose purpose = ProviderRoutePurpose.Automatic) => Request(
        actor,
        capability,
        operationId,
        providerIds,
        sourceTrackId,
        quality,
        allowFallback,
        allowManagedDownloads,
        idempotencyKey,
        protocol.CorrelationId,
        protocol.Deadline,
        protocol.CancellationToken,
        allowedAccountScopes,
        purpose);

    private static ProviderRouteRequest Request(
        ProviderActorContext actor,
        ProviderCapabilityKind capability,
        string operationId,
        IEnumerable<string> providerIds,
        ProviderExternalResourceId? sourceTrackId,
        ProviderAudioQuality quality = ProviderAudioQuality.Any,
        bool allowFallback = false,
        bool allowManagedDownloads = false,
        string? idempotencyKey = null,
        string correlationId = "provider-route",
        DateTimeOffset deadline = default,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ProviderAccountScope>? allowedAccountScopes = null,
        ProviderRoutePurpose purpose = ProviderRoutePurpose.Automatic) => new(
        capability,
        actor,
        new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, quality == ProviderAudioQuality.Any
                ? ProviderAudioQuality.HighResolution
                : quality, allowTranscode: true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback,
            allowSharedAccount: actor.Kind != ProviderActorKind.PublicRead,
            allowManagedDownloads,
            providerIds),
        operationId,
        correlationId,
        deadline,
        providerIds,
        providerStates: providerIds.Select(id => new ProviderRouteProviderState(
            id,
            availableQualities:
            [
                ProviderAudioQuality.Any,
                ProviderAudioQuality.DataSaver,
                ProviderAudioQuality.Lossy,
                ProviderAudioQuality.Lossless,
                ProviderAudioQuality.HighResolution
            ], allowedAccountScopes: allowedAccountScopes)),
        sourceTrackId: sourceTrackId,
        idempotencyKey: idempotencyKey,
        cancellationToken: cancellationToken,
        purpose: purpose);

    private void ThrowRejectedRoute(ProviderRouteCandidateDecision rejection, bool quiet = false)
    {
        if (rejection.ProviderId == "apple-musickit" &&
            rejection.ReasonCode is "account-personal-to-another-listener" or "account-scope-denied")
            throw new ProtocolProviderAccountAccessException();
        if (rejection.ReasonCode == ProviderError.AppleDownloadGatewayUpdateRequired().Code)
        {
            if (!quiet) logger?.LogWarning("Apple download gateway needs an update ({Provider})", rejection.ProviderId);
            throw new ProtocolProviderUnavailableException(ProviderError.AppleDownloadGatewayUpdateRequired());
        }
        if (rejection.ReasonCode == ProviderError.AppleDownloadSignInRequired().Code ||
            rejection.ProviderId == "apple-musickit" && rejection.ReasonCode == "account-required")
        {
            if (!quiet) logger?.LogWarning("Apple download needs sign-in ({Provider})", rejection.ProviderId);
            throw new ProtocolProviderUnavailableException(ProviderError.AppleDownloadSignInRequired());
        }
        if (rejection.ReasonCode is "health-degraded" or "health-unavailable" or "health-unauthorized" or
            "circuit-open" or "sidecar-not-ready")
            throw new ProtocolProviderUnavailableException(new(ProviderErrorKind.CapabilityUnavailable));
        throw new UnauthorizedAccessException("The provider route is not available to this user.");
    }

    private static void ThrowRouteFailure(ProviderError error) => throw error switch
    {
        { Code: "stream-range-unavailable" } => new HttpRequestException(error.SafeMessage, null,
            HttpStatusCode.RequestedRangeNotSatisfiable),
        { Code: "apple-download-sign-in-required" } => new ProtocolProviderUnavailableException(error),
        _ => error.Kind switch
        {
            ProviderErrorKind.NotFound => new FileNotFoundException(error.SafeMessage),
            ProviderErrorKind.Unauthorized or ProviderErrorKind.Forbidden or
                ProviderErrorKind.AccountNeedsConfiguration or ProviderErrorKind.AccountNeedsReauthentication =>
                new UnauthorizedAccessException(error.SafeMessage),
            ProviderErrorKind.Canceled => new OperationCanceledException(error.SafeMessage),
            ProviderErrorKind.RateLimited => new HttpRequestException(error.SafeMessage, null,
                System.Net.HttpStatusCode.TooManyRequests),
            ProviderErrorKind.NotSupported or ProviderErrorKind.CapabilityUnavailable =>
                new NotSupportedException(error.SafeMessage),
            _ => new HttpRequestException(error.SafeMessage)
        }
    };

    internal static Song Map(ProviderTrackMetadata item)
    {
        var artists = item.Artists.Select(artist => artist.Name).ToList();
        return new Song
        {
            Id = ProtocolItemId(item.Id),
            ExternalProvider = PublicProviderId(item.Id),
            ExternalId = item.Id.Value,
            Title = item.Title,
            Artist = artists.FirstOrDefault() ?? string.Empty,
            Artists = artists,
            ArtistId = item.Artists.FirstOrDefault()?.ArtistId is { } primaryArtist
                ? ProtocolItemId(primaryArtist)
                : null,
            ArtistIds = item.Artists
                .Select(artist => artist.ArtistId is { } artistId ? ProtocolItemId(artistId) : string.Empty)
                .ToList(),
            Album = item.AlbumTitle ?? string.Empty,
            AlbumId = item.AlbumId is { } albumId ? ProtocolItemId(albumId) : null,
            Duration = item.Duration.HasValue ? (int)item.Duration.Value.TotalSeconds : null,
            PreciseDuration = item.Duration,
            Bitrate = item.Bitrate,
            Isrc = item.Isrc,
            CoverArtUrl = item.Artwork?.PublicUri?.ToString(),
            CoverArtUrlLarge = item.Artwork?.PublicUri?.ToString(),
            Track = item.TrackNumber,
            DiscNumber = item.DiscNumber,
            TotalTracks = item.TotalTracks,
            Year = item.Year,
            Genre = item.Genre,
            Bpm = item.Bpm,
            SpotifyId = item.SpotifyId,
            ReleaseDate = item.ReleaseDate,
            AlbumArtist = item.AlbumArtist,
            Composer = item.Composer,
            Label = item.Label,
            Copyright = item.Copyright,
            Contributors = item.Contributors.ToList(),
            ExplicitContentLyrics = item.ExplicitContentLyrics ?? (item.IsExplicit switch
            {
                true => 1,
                false => 0,
                null => null
            }),
            IsLocal = false
        };
    }

    private async Task<Song> EnrichRelationshipsAsync(
        ProviderRouteCandidate<IProviderMetadataCapability> candidate,
        ProviderTrackMetadata item)
    {
        var song = Map(item);
        if (!string.IsNullOrWhiteSpace(song.AlbumId) &&
            song.ArtistIds.All(id => !string.IsNullOrWhiteSpace(id)))
            return song;

        async Task<IReadOnlyList<T>> Search<T>(Func<Task<ProviderOutcome<ProviderPage<T>>>> action)
        {
            try
            {
                var outcome = await action();
                return outcome.IsSuccess ? outcome.RequireValue().Items : [];
            }
            catch (OperationCanceledException) when (candidate.Context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                logger?.LogDebug(
                    "Optional metadata relationship enrichment failed for provider {ProviderId}",
                    candidate.Provider.Id);
                return [];
            }
        }

        var missingArtists = item.Artists
            .Where(artist => artist.ArtistId == null)
            .Select(artist => artist.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(RelationshipSearchLimit)
            .Select(name => Search(() => candidate.Implementation.SearchArtistsAsync(
                candidate.Context,
                new ProviderMetadataSearchRequest(name, new ProviderPageRequest(RelationshipSearchLimit)))))
            .ToArray();
        var albumTask = item.AlbumId == null && !string.IsNullOrWhiteSpace(item.AlbumTitle)
            ? Search(() => candidate.Implementation.SearchAlbumsAsync(
                candidate.Context,
                new ProviderMetadataSearchRequest(
                    item.AlbumTitle,
                    new ProviderPageRequest(RelationshipSearchLimit))))
            : Task.FromResult<IReadOnlyList<ProviderAlbumMetadata>>([]);

        var artistTask = Task.WhenAll(missingArtists);
        await Task.WhenAll(artistTask, albumTask);
        return EnrichRelationships(
            song,
            await albumTask,
            (await artistTask).SelectMany(artists => artists));
    }

    private static Song EnrichRelationships(
        Song song,
        IEnumerable<ProviderAlbumMetadata> albums,
        IEnumerable<ProviderArtistMetadata> artists)
    {
        ProviderAlbumMetadata? album = null;
        if (string.IsNullOrWhiteSpace(song.AlbumId) && !string.IsNullOrWhiteSpace(song.Album))
        {
            var artistNames = song.Artists
                .Append(song.AlbumArtist)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matches = albums.Where(candidate =>
                    candidate.Title.Equals(song.Album, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Artists.Any(credit => artistNames.Contains(credit.Name)))
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                album = matches[0];
                song.AlbumId = ProtocolItemId(album.Id);
            }
        }

        for (var index = 0; index < song.Artists.Count && index < song.ArtistIds.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(song.ArtistIds[index])) continue;
            var name = song.Artists[index];
            var ids = artists
                .Where(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.Id)
                .Concat((album?.Artists ?? [])
                    .Where(credit => credit.ArtistId != null &&
                                     credit.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(credit => credit.ArtistId!))
                .Distinct()
                .Take(2)
                .ToArray();
            if (ids.Length == 1) song.ArtistIds[index] = ProtocolItemId(ids[0]);
        }

        if (song.ArtistIds.Count > 0 && !string.IsNullOrWhiteSpace(song.ArtistIds[0]))
            song.ArtistId = song.ArtistIds[0];
        return song;
    }

    internal static Album Map(ProviderAlbumMetadata item) => new()
    {
        Id = ProtocolItemId(item.Id),
        ExternalProvider = PublicProviderId(item.Id),
        ExternalId = item.Id.Value,
        Title = item.Title,
        Artist = item.Artists.FirstOrDefault()?.Name ?? string.Empty,
        ArtistId = item.Artists.FirstOrDefault()?.ArtistId is { } artistId
            ? ProtocolItemId(artistId)
            : null,
        SongCount = item.TrackCount,
        CoverArtUrl = item.Artwork?.PublicUri?.ToString(),
        Year = item.Year,
        ReleaseDate = item.ReleaseDate,
        Genre = item.Genre,
        Songs = item.Tracks.Select(Map).ToList(),
        IsLocal = false
    };

    internal static Artist Map(ProviderArtistMetadata item) => new()
    {
        Id = ProtocolItemId(item.Id),
        ExternalProvider = PublicProviderId(item.Id),
        ExternalId = item.Id.Value,
        Name = item.Name,
        ImageUrl = item.Artwork?.PublicUri?.ToString(),
        IsLocal = false
    };

    private static string PublicProviderId(ProviderExternalResourceId id) =>
        id.ProviderId == "apple-musickit" && !allstarr.Core.Providers.AppleMusicKit.AppleMusicKitMetadataCapabilityAdapter.IsLibrary(id.Value)
            ? "apple-download" : id.ProviderId;

    private static string ProtocolItemId(ProviderExternalResourceId id) =>
        $"ext-{PublicProviderId(id)}-{id.ResourceKind switch
        {
            ProviderResourceKind.Track => "song",
            ProviderResourceKind.Album => "album",
            ProviderResourceKind.Artist => "artist",
            ProviderResourceKind.Playlist => "playlist",
            _ => throw new ArgumentOutOfRangeException(nameof(id), id.ResourceKind, "Unsupported protocol resource kind.")
        }}-{id.Value}";

    internal static ExternalPlaylist Map(ProviderPlaylistSummary item) => new()
    {
        Id = ProtocolItemId(item.Id),
        Provider = PublicProviderId(item.Id),
        ExternalId = item.Id.Value,
        Name = item.Name,
        Description = item.Description,
        CuratorName = item.Owner.DisplayName,
        TrackCount = item.TrackCount ?? 0,
        Duration = item.DurationSeconds ?? 0,
        CoverUrl = item.Artwork?.PublicUri?.ToString(),
        CreatedDate = item.CreatedDate
    };

    private static List<T> MergeSearch<T>(
        IEnumerable<T> routed,
        int limit,
        Func<T, string> key,
        Func<T, string?> provider,
        Func<T, double> score,
        IReadOnlyList<string> providerOrder) =>
        routed.DistinctBy(key, StringComparer.Ordinal)
            .OrderByDescending(score)
            .ThenBy(item => ProviderPriority(providerOrder, provider(item)))
            .Take(Math.Max(0, limit))
            .ToList();

    private List<T> Merge<T>(
        IEnumerable<T> routed,
        int limit,
        Func<T, string> key,
        Func<T, string?> provider,
        IReadOnlyList<string>? preferredOrder = null)
    {
        var items = routed
            .DistinctBy(key, StringComparer.Ordinal)
            .ToList();
        var preferred = preferredOrder?
            .Select(NormalizeProvider)
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? (configuration?["Providers:MetadataOrder"] ??
                          configuration?["MULTI_PROVIDER_METADATA_ORDER"] ??
                          "apple-download,deezer,qobuz")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeProvider)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var groups = items
            .GroupBy(item => NormalizeProvider(provider(item)), StringComparer.Ordinal)
            .OrderBy(group =>
            {
                var index = preferred.IndexOf(group.Key);
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new Queue<T>(group))
            .ToList();
        var merged = new List<T>();
        while (merged.Count < Math.Max(0, limit) && groups.Any(group => group.Count > 0))
        {
            foreach (var group in groups)
            {
                if (group.Count > 0 && merged.Count < limit)
                {
                    merged.Add(group.Dequeue());
                }
            }
        }
        return merged;
    }

    private static string NormalizeProvider(string? provider) => provider?.ToLowerInvariant() switch
    {
        "applemusic" or "apple-download" => "apple-musickit",
        null or "" => "unknown",
        var value => value
    };

    private static string Key(string? providerId, string? externalId, string fallback) =>
        !string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(externalId)
            ? $"{providerId}:{externalId}"
            : fallback;

}
