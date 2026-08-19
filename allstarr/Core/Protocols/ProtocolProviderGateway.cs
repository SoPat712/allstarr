using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging;

namespace allstarr.Core.Protocols;

/// <summary>
/// The provider boundary used by protocol adapters for synthesized resources.
/// Native backend resources never enter this gateway and remain transparent relays.
/// </summary>
public interface IProtocolProviderGateway
{
    IReadOnlyList<string> GetProviderOrder(ProviderCapabilityKind capability);

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

    Task<Song?> GetSongAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

    Task<Album?> GetAlbumAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

    Task<Artist?> GetArtistAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

    Task<List<Album>> GetArtistAlbumsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId);

    Task<List<Song>> GetArtistTracksAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId);

    Task<List<ExternalPlaylist>> SearchPlaylistsAsync(
        ProtocolExecutionContext protocol,
        string query,
        int limit);

    Task<ExternalPlaylist?> GetPlaylistAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId);

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

    Task<ProviderLyricsResult?> GetLyricsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        ProviderLyricsFormat? preferredFormat = null,
        string? trackTitle = null,
        IReadOnlyList<string>? artistNames = null,
        string? albumTitle = null,
        int? durationSeconds = null);
}

public sealed record ProtocolProviderStream(
    HttpResponseMessage Response,
    ProviderStreamLease Lease,
    string ServingProviderId);

public sealed class ProtocolProviderGateway(
    IProviderRouter router,
    IProviderRegistry registry,
    IProviderRouteAccountResolver accounts,
    IMusicMetadataService legacyMetadata,
    IHttpClientFactory httpClientFactory,
    IConfiguration? configuration = null,
    IApplicationCache? applicationCache = null,
    ILogger<ProtocolProviderGateway>? logger = null) : IProtocolProviderGateway
{
    private const string StreamingClientName = "ProtocolProviderStreaming";
    private const int ProviderSearchConcurrency = 4;
    private const int RelationshipSearchLimit = 10;
    private static readonly TimeSpan ExactRouteMissTtl = TimeSpan.FromMinutes(2);

    public IReadOnlyList<string> GetProviderOrder(ProviderCapabilityKind capability) =>
        ResolveProviderOrder(capability);

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
        if (protocol.Actor is null)
        {
            var publicLegacy = await legacyMetadata.SearchAllAsync(
                query, songLimit, albumLimit, artistLimit, protocol.CancellationToken);
            return new SearchResult
            {
                Songs = publicLegacy.Songs
                    .Where(item => IsRequestedPublicProvider(item.ExternalProvider) &&
                                   IsPublicStreamingProvider(item.ExternalProvider))
                    .Take(songLimit)
                    .ToList(),
                Albums = publicLegacy.Albums.Where(item => IsRequestedPublicProvider(item.ExternalProvider)).Take(albumLimit).ToList(),
                Artists = publicLegacy.Artists.Where(item => IsRequestedPublicProvider(item.ExternalProvider)).Take(artistLimit).ToList()
            };

            bool IsRequestedPublicProvider(string? itemProvider) =>
                IsPublicMetadataProvider(itemProvider) &&
                (requestedProviderId == null ||
                 NormalizeProvider(itemProvider) == requestedProviderId);
        }
        var actor = protocol.RequireActor();
        var playableProviders = songLimit > 0
            ? (await ResolvePlayableProviderOrderAsync(
                    protocol,
                    actor,
                    ResolveProviderOrder(ProviderCapabilityKind.Streaming)))
                .ToHashSet(StringComparer.Ordinal)
            : [];
        var fetchLimit = Math.Clamp(Math.Max(songLimit, Math.Max(albumLimit, artistLimit)), 1, 200);
        var providerOrder = ResolveProviderOrder(ProviderCapabilityKind.Metadata)
            .Where(item => requestedProviderId == null || item == requestedProviderId)
            .ToArray();
        if (providerOrder.Length == 0) return new SearchResult();
        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            protocol,
            actor,
            ProviderCapabilityKind.Metadata,
            "protocol-metadata-search",
            providerIds: providerOrder,
            sourceTrackId: null));

        var routed = new SearchResult();
        using var metadataSearchGate = new SemaphoreSlim(ProviderSearchConcurrency);
        var searchTasks = plan.Candidates.Select(async candidate =>
        {
            await metadataSearchGate.WaitAsync(protocol.CancellationToken);
            try
            {
                var request = new ProviderMetadataSearchRequest(query, new ProviderPageRequest(fetchLimit));
                var songsTask = candidate.Implementation.SearchTracksAsync(candidate.Context, request);
                var albumsTask = candidate.Implementation.SearchAlbumsAsync(candidate.Context, request);
                var artistsTask = candidate.Implementation.SearchArtistsAsync(candidate.Context, request);
                await Task.WhenAll(songsTask, albumsTask, artistsTask);
                return new
                {
                    ProviderId = NormalizeProvider(candidate.Provider.Id),
                    SongsResult = await songsTask,
                    AlbumsResult = await albumsTask,
                    ArtistsResult = await artistsTask
                };
            }
            finally
            {
                metadataSearchGate.Release();
            }
        }).ToList();

        var searchOutcomes = await Task.WhenAll(searchTasks);

        foreach (var outcome in searchOutcomes)
        {
            var albums = outcome.AlbumsResult.IsSuccess
                ? outcome.AlbumsResult.RequireValue().Items
                : [];
            var artists = outcome.ArtistsResult.IsSuccess
                ? outcome.ArtistsResult.RequireValue().Items
                : [];
            if (playableProviders.Contains(outcome.ProviderId) && outcome.SongsResult.IsSuccess)
            {
                routed.Songs.AddRange(outcome.SongsResult.RequireValue().Items
                    .Select(item => EnrichRelationships(Map(item), albums, artists)));
            }
            if (outcome.AlbumsResult.IsSuccess)
            {
                routed.Albums.AddRange(outcome.AlbumsResult.RequireValue().Items.Select(Map));
            }
            if (outcome.ArtistsResult.IsSuccess)
            {
                routed.Artists.AddRange(outcome.ArtistsResult.RequireValue().Items.Select(Map));
            }
        }

        return new SearchResult
        {
            Songs = Merge(routed.Songs, [], songLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id), item => item.ExternalProvider),
            Albums = Merge(routed.Albums, [], albumLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id), item => item.ExternalProvider),
            Artists = Merge(routed.Artists, [], artistLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id), item => item.ExternalProvider)
        };
    }

    public async Task<IReadOnlyList<Song>> SearchPlayableSongsAsync(
        ProtocolExecutionContext protocol,
        string query,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        limit = Math.Clamp(limit, 1, 200);
        var configuredProviderOrder = ResolveProviderOrder(ProviderCapabilityKind.Streaming)
            .Select(NormalizeProvider)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (configuredProviderOrder.Length == 0) return [];

        if (protocol.Actor is null)
        {
            var publicLegacy = await legacyMetadata.SearchPlayableSongsAsync(
                query, limit, protocol.CancellationToken);
            return publicLegacy
                .Where(item => IsPublicMetadataProvider(item.ExternalProvider))
                .Where(item => configuredProviderOrder.Contains(
                    NormalizeProvider(item.ExternalProvider), StringComparer.Ordinal))
                .Take(limit)
                .ToArray();
        }

        var actor = protocol.RequireActor();
        var providerOrder = await ResolvePlayableProviderOrderAsync(
            protocol, actor, configuredProviderOrder);
        if (providerOrder.Count == 0) return [];
        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            protocol,
            actor,
            ProviderCapabilityKind.Metadata,
            "protocol-playable-track-search",
            providerIds: providerOrder,
            sourceTrackId: null));
        using var searchGate = new SemaphoreSlim(ProviderSearchConcurrency);
        var tasks = plan.Candidates.Select(async candidate =>
        {
            await searchGate.WaitAsync(protocol.CancellationToken);
            try
            {
                return await candidate.Implementation.SearchTracksAsync(
                    candidate.Context,
                    new ProviderMetadataSearchRequest(query, new ProviderPageRequest(limit)));
            }
            catch (OperationCanceledException) when (protocol.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                searchGate.Release();
            }
        });
        var routed = (await Task.WhenAll(tasks))
            .Where(outcome => outcome?.IsSuccess == true)
            .SelectMany(outcome => outcome!.RequireValue().Items)
            .Select(Map)
            .Where(item => providerOrder.Contains(
                NormalizeProvider(item.ExternalProvider), StringComparer.Ordinal));
        return Merge(
            routed,
            [],
            limit,
            item => Key(item.ExternalProvider, item.ExternalId, item.Id),
            item => item.ExternalProvider,
            providerOrder);
    }

    private async Task<IReadOnlyList<string>> ResolvePlayableProviderOrderAsync(
        ProtocolExecutionContext protocol,
        ProviderActorContext actor,
        IReadOnlyList<string> configuredProviderOrder)
    {
        if (configuredProviderOrder.Count == 0) return [];
        var streaming = await router.PlanAsync<IProviderStreamingCapability>(Request(
            protocol,
            actor,
            ProviderCapabilityKind.Streaming,
            "protocol-playable-provider-check",
            configuredProviderOrder,
            sourceTrackId: null));
        var allowed = streaming.Candidates.Select(item => item.Provider.Id)
            .Select(NormalizeProvider)
            .ToHashSet(StringComparer.Ordinal);
        var typed = registry.FindByCapability(ProviderCapabilityKind.Streaming)
            .Select(item => NormalizeProvider(item.Id))
            .ToHashSet(StringComparer.Ordinal);
        var compatibility = (await ResolveAllowedCompatibilityProvidersAsync(
                protocol, actor, ProviderCapabilityKind.Streaming))
            .Where(providerId => !typed.Contains(providerId));
        allowed.UnionWith(compatibility);
        return configuredProviderOrder.Where(allowed.Contains).ToArray();
    }

    public async Task<Song?> GetSongAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        if (protocol.Actor is null && IsPublicMetadataProvider(providerId))
            return await legacyMetadata.GetSongAsync(providerId, externalId, protocol.CancellationToken);
        var routedProviderId = NormalizeProvider(providerId);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, routedProviderId, ProviderCapabilityKind.Metadata, "protocol-metadata-get-track");
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(routedProviderId, ProviderResourceKind.Track, externalId);
            var outcome = await routed.Candidate.Implementation.GetTrackAsync(
                routed.Candidate.Context,
                new ProviderTrackLookupRequest(id));
            if (outcome.IsSuccess)
                return await EnrichRelationshipsAsync(routed.Candidate, outcome.RequireValue());
            if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return null;
            ThrowRouteFailure(outcome.Error);
        }
        await RequireCompatibilityProviderAsync(protocol, routedProviderId);
        return null;
    }

    public async Task<Album?> GetAlbumAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        if (protocol.Actor is null && IsPublicMetadataProvider(providerId))
            return await legacyMetadata.GetAlbumAsync(providerId, externalId, protocol.CancellationToken);
        var routedProviderId = NormalizeProvider(providerId);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, routedProviderId, ProviderCapabilityKind.Metadata, "protocol-metadata-get-album");
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(routedProviderId, ProviderResourceKind.Album, externalId);
            var outcome = await routed.Candidate.Implementation.GetAlbumAsync(
                routed.Candidate.Context,
                new ProviderAlbumLookupRequest(id));
            if (outcome.IsSuccess) return Map(outcome.RequireValue());
            if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return null;
            ThrowRouteFailure(outcome.Error);
        }
        await RequireCompatibilityProviderAsync(protocol, routedProviderId);
        return null;
    }

    public async Task<Artist?> GetArtistAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        if (protocol.Actor is null && IsPublicMetadataProvider(providerId))
            return await legacyMetadata.GetArtistAsync(providerId, externalId, protocol.CancellationToken);
        var routedProviderId = NormalizeProvider(providerId);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, routedProviderId, ProviderCapabilityKind.Metadata, "protocol-metadata-get-artist");
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(routedProviderId, ProviderResourceKind.Artist, externalId);
            var outcome = await routed.Candidate.Implementation.GetArtistAsync(
                routed.Candidate.Context,
                new ProviderArtistLookupRequest(id));
            if (outcome.IsSuccess) return Map(outcome.RequireValue());
            if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return null;
            ThrowRouteFailure(outcome.Error);
        }
        await RequireCompatibilityProviderAsync(protocol, routedProviderId);
        return null;
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (protocol.Actor is null && IsPublicMetadataProvider(providerId))
            return await legacyMetadata.GetArtistAlbumsAsync(providerId, externalId, protocol.CancellationToken);
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
            return albums;
        }
        await RequireCompatibilityProviderAsync(protocol, routedProviderId);
        return [];
    }

    public async Task<List<Song>> GetArtistTracksAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (protocol.Actor is null && IsPublicMetadataProvider(providerId))
            return await legacyMetadata.GetArtistTracksAsync(providerId, externalId, protocol.CancellationToken);
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
            return tracks;
        }
        await RequireCompatibilityProviderAsync(protocol, routedProviderId);
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
        var providerOrder = ResolveProviderOrder(ProviderCapabilityKind.Playlist);
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
        ArgumentNullException.ThrowIfNull(protocol);
        if (protocol.Actor is null)
            throw new UnauthorizedAccessException("A resolved user is required for provider playlists.");

        var routed = await PlanExactAsync<IProviderPlaylistCapability>(
            protocol, providerId, ProviderCapabilityKind.Playlist, "protocol-playlist-get");
        if (routed.Candidate != null)
        {
            var playlistId = new ProviderExternalResourceId(providerId, ProviderResourceKind.Playlist, externalId);
            var outcome = await routed.Candidate.Implementation.GetPlaylistTracksAsync(
                routed.Candidate.Context,
                new ProviderPlaylistTracksRequest(playlistId, new ProviderPageRequest(1)));
            if (outcome.IsSuccess) return Map(outcome.RequireValue().Playlist);
            if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return null;
            ThrowRouteFailure(outcome.Error);
        }

        await RequireCompatibilityProviderAsync(protocol, providerId, ProviderCapabilityKind.Playlist);
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
            return tracks;
        }

        await RequireCompatibilityProviderAsync(protocol, providerId, ProviderCapabilityKind.Playlist);
        return [];
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

        providerId = NormalizeProvider(providerId);
        var actor = protocol.RequireActor();
        var exactRouteMissKey = CacheKeyBuilder.BuildPlaybackRouteNegativeKey(
            actor.TenantId,
            actor.EffectiveUserId,
            protocol.LibraryScopeId,
            providerId,
            externalId,
            quality.ToString());
        if (applicationCache?.IsEnabled == true &&
            await applicationCache.ExistsAsync(exactRouteMissKey))
        {
            logger?.LogDebug(
                "Skipping repeated playback translation miss for chosen provider {ChosenProvider}",
                providerId);
            return null;
        }

        var parsedRange = ParseRange(rangeHeader);
        var rangeStart = parsedRange?.Ranges.Single().From;
        var trackId = new ProviderExternalResourceId(
            providerId, ProviderResourceKind.Track, externalId);
        var providerOrder = new[] { providerId }
            .Concat(ResolveProviderOrder(ProviderCapabilityKind.Streaming))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var plan = await router.PlanAsync<IProviderStreamingCapability>(Request(
            protocol,
            actor,
            ProviderCapabilityKind.Streaming,
            "protocol-stream-open",
            providerOrder,
            trackId,
            quality,
            allowFallback: true,
            allowManagedDownloads: true));
        if (plan.Candidates.Count == 0)
        {
            if (plan.Decision.Candidates.Any(item =>
                    item.ProviderId.Equals(providerId, StringComparison.Ordinal) &&
                    item.ReasonCode != "capability-unavailable"))
            {
                throw new UnauthorizedAccessException(
                    "The provider route is not available to this user.");
            }

            var isTranslationMiss = plan.Decision.Candidates.Any(item =>
                    item.ReasonCode == "verified-identity-required") &&
                plan.Decision.Candidates.All(item =>
                    item.ReasonCode is "capability-unavailable" or "verified-identity-required");
            if (isTranslationMiss && applicationCache?.IsEnabled == true)
            {
                await applicationCache.SetStringAsync(
                    exactRouteMissKey,
                    "1",
                    ExactRouteMissTtl);
            }
            return null;
        }

        for (var candidateIndex = 0; candidateIndex < plan.Candidates.Count; candidateIndex++)
        {
            var candidate = plan.Candidates[candidateIndex];
            var leaseRequest = new ProviderStreamLeaseRequest(
                candidate.TrackId ?? trackId,
                quality,
                rangeStart);

            async Task<ProviderOutcome<ProviderStreamLease>> ResolveLeaseAsync() =>
                await candidate.Implementation.GetStreamLeaseAsync(
                    candidate.Context,
                    leaseRequest);

            async Task<HttpResponseMessage> OpenLeaseAsync(ProviderStreamLease lease)
            {
                using var request = new HttpRequestMessage(
                    headOnly ? HttpMethod.Head : HttpMethod.Get,
                    lease.ProtectedSourceUri);
                if (parsedRange != null && lease.SupportsByteRanges)
                    request.Headers.Range = parsedRange;
                return lease.ProtectedResponseFactory != null
                    ? await lease.ProtectedResponseFactory(request, protocol.CancellationToken)
                    : await httpClientFactory.CreateClient(StreamingClientName).SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        protocol.CancellationToken);
            }

            var leaseOutcome = await ResolveLeaseAsync();
            if (!leaseOutcome.IsSuccess)
            {
                var fallback = router.EvaluateFallback(
                    plan,
                    candidateIndex,
                    leaseOutcome.Error!);
                if (fallback.Disposition == ProviderFallbackDisposition.Advance)
                {
                    logger?.LogInformation(
                        "Exact playback fallback advanced from chosen provider {ChosenProvider} after {FailureCode}",
                        providerId,
                        leaseOutcome.Error!.Code);
                    continue;
                }
                ThrowRouteFailure(leaseOutcome.Error!);
                return null;
            }

            var lease = leaseOutcome.RequireValue();
            HttpResponseMessage response;
            try
            {
                response = await OpenLeaseAsync(lease);
            }
            catch (HttpRequestException) when (
                lease.RetryBehavior != ProviderStreamRetryBehavior.DoNotRetry)
            {
                if (lease.RetryBehavior == ProviderStreamRetryBehavior.RefreshLease)
                {
                    var refreshed = await ResolveLeaseAsync();
                    if (!refreshed.IsSuccess) ThrowRouteFailure(refreshed.Error!);
                    lease = refreshed.RequireValue();
                }
                response = await OpenLeaseAsync(lease);
            }
            if (ShouldRetry(lease, response.StatusCode))
            {
                response.Dispose();
                if (lease.RetryBehavior == ProviderStreamRetryBehavior.RefreshLease)
                {
                    var refreshed = await ResolveLeaseAsync();
                    if (!refreshed.IsSuccess) ThrowRouteFailure(refreshed.Error!);
                    lease = refreshed.RequireValue();
                }
                response = await OpenLeaseAsync(lease);
            }

            logger?.LogInformation(
                "Opened stream with chosen provider {ChosenProvider} and serving provider {ServingProvider}",
                providerId,
                candidate.Provider.Id);
            return new ProtocolProviderStream(response, lease, candidate.Provider.Id);
        }

        return null;
    }

    private static bool ShouldRetry(ProviderStreamLease lease, HttpStatusCode statusCode) =>
        lease.RetryBehavior != ProviderStreamRetryBehavior.DoNotRetry &&
        (statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
         statusCode >= HttpStatusCode.InternalServerError ||
         lease.RetryBehavior == ProviderStreamRetryBehavior.RefreshLease &&
         statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

    public async Task<ProviderLyricsResult?> GetLyricsAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        ProviderLyricsFormat? preferredFormat = null,
        string? trackTitle = null,
        IReadOnlyList<string>? artistNames = null,
        string? albumTitle = null,
        int? durationSeconds = null)
    {
        var trackId = new ProviderExternalResourceId(providerId, ProviderResourceKind.Track, externalId);
        var routed = await PlanExactAsync<IProviderLyricsCapability>(
            protocol,
            providerId,
            ProviderCapabilityKind.Lyrics,
            "protocol-lyrics-get",
            trackId);
        if (routed.Candidate == null) return null;
        var canonicalBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{providerId}\n{externalId}"));
        var canonicalId = new Guid(canonicalBytes.AsSpan(0, 16));
        var outcome = await routed.Candidate.Implementation.FetchLyricsAsync(
            routed.Candidate.Context,
            new ProviderLyricsRequest(
                canonicalId,
                routed.Candidate.TrackId ?? trackId,
                preferredFormat: preferredFormat,
                trackTitle: trackTitle,
                artistNames: artistNames,
                albumTitle: albumTitle,
                durationSeconds: durationSeconds));
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
            ProviderAudioQuality quality = ProviderAudioQuality.Any)
        where TCapability : class, IProviderCapability
    {
        ArgumentNullException.ThrowIfNull(protocol);
        var plan = await router.PlanAsync<TCapability>(Request(
            protocol,
            protocol.RequireActor(),
            capability,
            operationId,
            [providerId],
            sourceTrackId,
            quality));
        var candidate = plan.Candidates.SingleOrDefault();
        if (candidate == null && plan.Decision.Candidates.Any(item =>
                item.ProviderId.Equals(providerId, StringComparison.Ordinal) &&
                item.ReasonCode != "capability-unavailable"))
        {
            throw new UnauthorizedAccessException("The provider route is not available to this user.");
        }
        return (candidate, plan);
    }

    private async Task<HashSet<string>> ResolveAllowedCompatibilityProvidersAsync(
        ProtocolExecutionContext protocol,
        ProviderActorContext actor,
        ProviderCapabilityKind capabilityKind = ProviderCapabilityKind.Metadata)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in registry.FindByCapability(
                     capabilityKind,
                     includeNonOperational: true))
        {
            var capability = descriptor.Capabilities.Single(item =>
                item.Capability == capabilityKind);
            if (capability.HasUsableImplementation) continue;
            if (capability.AccountRequirement == ProviderAccountRequirement.None)
            {
                allowed.Add(descriptor.Id);
                continue;
            }

            ProviderRouteAccountResolution? resolution;
            try
            {
                resolution = await accounts.ResolveAsync(
                    new ProviderRouteAccountRequest(
                        actor,
                        descriptor.Id,
                        capabilityKind,
                        RequestedAccountId: null,
                        protocol.LibraryScopeId),
                    protocol.CancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            if (resolution?.Account is { Enabled: true } account &&
                resolution.CurrentRevision == account.Revision &&
                capability.AllowedAccountScopes.Contains(account.Scope))
            {
                allowed.Add(descriptor.Id);
            }
            else if (capability.AccountRequirement == ProviderAccountRequirement.Optional)
            {
                allowed.Add(descriptor.Id);
            }
        }
        return allowed;
    }

    private async Task RequireCompatibilityProviderAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        ProviderCapabilityKind capabilityKind = ProviderCapabilityKind.Metadata)
    {
        var allowed = await ResolveAllowedCompatibilityProvidersAsync(
            protocol, protocol.RequireActor(), capabilityKind);
        if (!allowed.Contains(providerId))
        {
            throw new UnauthorizedAccessException("The provider route is not available to this user.");
        }
    }

    private bool IsPublicMetadataProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        return registry.FindByCapability(ProviderCapabilityKind.Metadata, includeNonOperational: true)
            .Any(descriptor => descriptor.Id.Equals(providerId, StringComparison.Ordinal) &&
                               descriptor.Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Metadata)
                                   .AccountRequirement == ProviderAccountRequirement.None);
    }

    private bool IsPublicStreamingProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        return registry.FindByCapability(ProviderCapabilityKind.Streaming, includeNonOperational: true)
            .Any(descriptor => descriptor.Id.Equals(providerId, StringComparison.Ordinal) &&
                               descriptor.Capabilities.Single(item => item.Capability == ProviderCapabilityKind.Streaming)
                                   .AccountRequirement == ProviderAccountRequirement.None);
    }

    private IReadOnlyList<string> ResolveProviderOrder(ProviderCapabilityKind capability)
    {
        var (settingKey, environmentKey, fallback) = capability switch
        {
            ProviderCapabilityKind.Metadata => ("Providers:MetadataOrder", "MULTI_PROVIDER_METADATA_ORDER", "apple-download,deezer,qobuz"),
            ProviderCapabilityKind.Playlist => ("Providers:PlaylistOrder", "MULTI_PROVIDER_PLAYLIST_ORDER", "spotify,apple-download,deezer,qobuz"),
            ProviderCapabilityKind.Lyrics => ("Providers:LyricsOrder", "MULTI_PROVIDER_LYRICS_ORDER", "spotify,apple-download,lrclib"),
            ProviderCapabilityKind.Streaming => ("Providers:StreamingOrder", "MULTI_PROVIDER_STREAMING_ORDER", "apple-download,deezer,qobuz"),
            ProviderCapabilityKind.Download => ("Providers:DownloadOrder", "MULTI_PROVIDER_DOWNLOAD_ORDER", "apple-download,deezer,qobuz"),
            _ => (string.Empty, string.Empty, string.Empty)
        };
        var configured = configuration?[settingKey] ?? configuration?[environmentKey] ?? fallback;
        var ordered = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeProvider)
            .Where(providerId => registry.FindByCapability(capability, includeNonOperational: true)
                .Any(provider => provider.Id.Equals(providerId, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        ordered.AddRange(registry.FindByCapability(capability, includeNonOperational: true)
            .Select(provider => provider.Id)
            .Where(providerId => !ordered.Contains(providerId, StringComparer.Ordinal))
            .OrderBy(providerId => providerId, StringComparer.Ordinal));
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
        string? idempotencyKey = null) => new(
        capability,
        actor,
        new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, quality == ProviderAudioQuality.Any
                ? ProviderAudioQuality.HighResolution
                : quality, allowTranscode: true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback,
            allowSharedAccount: true,
            allowManagedDownloads,
            providerIds),
        operationId,
        protocol.CorrelationId,
        protocol.Deadline,
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
            ])),
        library: protocol.LibraryScopeId == null
            ? null
            : new ProviderLibraryContext(actor.TenantId, protocol.LibraryScopeId),
        sourceTrackId: sourceTrackId,
        idempotencyKey: idempotencyKey,
        cancellationToken: protocol.CancellationToken);

    private static RangeHeaderValue? ParseRange(string? rangeHeader)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader)) return null;
        if (!RangeHeaderValue.TryParse(rangeHeader, out var parsed) || parsed.Ranges.Count != 1)
        {
            throw new InvalidOperationException("Only one valid byte range may be requested.");
        }
        return parsed;
    }

    private static void ThrowRouteFailure(ProviderError error) => throw error.Kind switch
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
    };

    private static Song Map(ProviderTrackMetadata item)
    {
        var artists = item.Artists.Select(artist => artist.Name).ToList();
        return new Song
        {
            Id = ProtocolItemId(item.Id),
            ExternalProvider = item.Id.ProviderId,
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

    private static Album Map(ProviderAlbumMetadata item) => new()
    {
        Id = $"ext-{item.Id.ProviderId}-album-{item.Id.Value}",
        ExternalProvider = item.Id.ProviderId,
        ExternalId = item.Id.Value,
        Title = item.Title,
        Artist = item.Artists.FirstOrDefault()?.Name ?? string.Empty,
        ArtistId = item.Artists.FirstOrDefault()?.ArtistId is { } artistId
            ? ProtocolItemId(artistId)
            : null,
        SongCount = item.TrackCount,
        CoverArtUrl = item.Artwork?.PublicUri?.ToString(),
        Year = item.Year,
        Genre = item.Genre,
        Songs = item.Tracks.Select(Map).ToList(),
        IsLocal = false
    };

    private static Artist Map(ProviderArtistMetadata item) => new()
    {
        Id = $"ext-{item.Id.ProviderId}-artist-{item.Id.Value}",
        ExternalProvider = item.Id.ProviderId,
        ExternalId = item.Id.Value,
        Name = item.Name,
        ImageUrl = item.Artwork?.PublicUri?.ToString(),
        IsLocal = false
    };

    private static string ProtocolItemId(ProviderExternalResourceId id) =>
        $"ext-{id.ProviderId}-{id.ResourceKind switch
        {
            ProviderResourceKind.Track => "song",
            ProviderResourceKind.Album => "album",
            ProviderResourceKind.Artist => "artist",
            ProviderResourceKind.Playlist => "playlist",
            _ => throw new ArgumentOutOfRangeException(nameof(id), id.ResourceKind, "Unsupported protocol resource kind.")
        }}-{id.Value}";

    private static ExternalPlaylist Map(ProviderPlaylistSummary item) => new()
    {
        Id = $"ext-{item.Id.ProviderId}-playlist-{item.Id.Value}",
        Provider = item.Id.ProviderId,
        ExternalId = item.Id.Value,
        Name = item.Name,
        Description = item.Description,
        CuratorName = item.Owner.DisplayName,
        TrackCount = item.TrackCount ?? 0,
        Duration = item.DurationSeconds ?? 0,
        CoverUrl = item.Artwork?.PublicUri?.ToString(),
        CreatedDate = item.CreatedDate
    };

    private List<T> Merge<T>(
        IEnumerable<T> routed,
        IEnumerable<T> legacy,
        int limit,
        Func<T, string> key,
        Func<T, string?> provider,
        IReadOnlyList<string>? preferredOrder = null)
    {
        var items = routed.Concat(legacy)
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
        "applemusic" => "apple-download",
        null or "" => "unknown",
        var value => value
    };

    private static string Key(string? providerId, string? externalId, string fallback) =>
        !string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(externalId)
            ? $"{providerId}:{externalId}"
            : fallback;

}
