using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services;

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
        int artistLimit);

    Task<Song?> GetSongAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

    Task<Album?> GetAlbumAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

    Task<Artist?> GetArtistAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

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
        string? rangeHeader);

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

public sealed record ProtocolProviderStream(HttpResponseMessage Response, ProviderStreamLease Lease);

public sealed class ProtocolProviderGateway(
    IProviderRouter router,
    IProviderRegistry registry,
    IProviderRouteAccountResolver accounts,
    IMusicMetadataService legacyMetadata,
    IHttpClientFactory httpClientFactory,
    IConfiguration? configuration = null) : IProtocolProviderGateway
{
    private const string StreamingClientName = "ProtocolProviderStreaming";
    private const int ProviderSearchConcurrency = 4;

    public IReadOnlyList<string> GetProviderOrder(ProviderCapabilityKind capability) =>
        ResolveProviderOrder(capability);

    public async Task<SearchResult> SearchAsync(
        ProtocolExecutionContext protocol,
        string query,
        int songLimit,
        int albumLimit,
        int artistLimit)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (protocol.Actor is null)
        {
            var publicLegacy = await legacyMetadata.SearchAllAsync(
                query, songLimit, albumLimit, artistLimit, protocol.CancellationToken);
            return new SearchResult
            {
                Songs = publicLegacy.Songs.Where(item => IsPublicMetadataProvider(item.ExternalProvider)).Take(songLimit).ToList(),
                Albums = publicLegacy.Albums.Where(item => IsPublicMetadataProvider(item.ExternalProvider)).Take(albumLimit).ToList(),
                Artists = publicLegacy.Artists.Where(item => IsPublicMetadataProvider(item.ExternalProvider)).Take(artistLimit).ToList()
            };
        }
        var actor = protocol.RequireActor();
        var fetchLimit = Math.Clamp(Math.Max(songLimit, Math.Max(albumLimit, artistLimit)), 1, 200);
        var providerOrder = ResolveProviderOrder(ProviderCapabilityKind.Metadata);
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
            if (outcome.SongsResult.IsSuccess)
            {
                routed.Songs.AddRange(outcome.SongsResult.RequireValue().Items.Select(Map));
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

        // ConfiguredOnly built-ins remain compatibility adapters until their durable-account
        // implementations expose the typed contract. Merge them without allowing them to
        // overwrite a routed result for the same provider-native identity.
        var legacy = await legacyMetadata.SearchAllAsync(
            query,
            songLimit,
            albumLimit,
            artistLimit,
            protocol.CancellationToken);
        var allowedCompatibilityProviders = await ResolveAllowedCompatibilityProvidersAsync(protocol, actor);
        return new SearchResult
        {
            Songs = Merge(routed.Songs, legacy.Songs.Where(item => Allowed(item.ExternalProvider, allowedCompatibilityProviders)), songLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id), item => item.ExternalProvider),
            Albums = Merge(routed.Albums, legacy.Albums.Where(item => Allowed(item.ExternalProvider, allowedCompatibilityProviders)), albumLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id), item => item.ExternalProvider),
            Artists = Merge(routed.Artists, legacy.Artists.Where(item => Allowed(item.ExternalProvider, allowedCompatibilityProviders)), artistLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id), item => item.ExternalProvider)
        };
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
            if (outcome.IsSuccess) return Map(outcome.RequireValue());
            if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return null;
            ThrowRouteFailure(outcome.Error);
        }
        await RequireCompatibilityProviderAsync(protocol, routedProviderId);
        return await legacyMetadata.GetSongAsync(providerId, externalId, protocol.CancellationToken);
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
        Album? typed = null;
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(routedProviderId, ProviderResourceKind.Album, externalId);
            var outcome = await routed.Candidate.Implementation.GetAlbumAsync(
                routed.Candidate.Context,
                new ProviderAlbumLookupRequest(id));
            if (outcome.IsSuccess) typed = Map(outcome.RequireValue());
            else if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return null;
            else ThrowRouteFailure(outcome.Error);
        }

        // The v1 metadata contract intentionally does not embed album tracks. Preserve the
        // compatibility implementation's richer album when it is available.
        if (typed != null)
        {
            return await legacyMetadata.GetAlbumAsync(providerId, externalId, protocol.CancellationToken) ?? typed;
        }
        await RequireCompatibilityProviderAsync(protocol, routedProviderId);
        return await legacyMetadata.GetAlbumAsync(providerId, externalId, protocol.CancellationToken);
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
        return await legacyMetadata.GetArtistAsync(providerId, externalId, protocol.CancellationToken);
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

        var allowedCompatibilityProviders = await ResolveAllowedCompatibilityProvidersAsync(
            protocol, actor, ProviderCapabilityKind.Playlist);
        if (allowedCompatibilityProviders.Count == 0)
        {
            return routed.Take(limit).ToList();
        }
        var legacy = await legacyMetadata.SearchPlaylistsAsync(query, limit, protocol.CancellationToken);
        return Merge(
            routed,
            legacy.Where(item => Allowed(item.Provider, allowedCompatibilityProviders)),
            limit,
            item => Key(item.Provider, item.ExternalId, item.Id),
            item => item.Provider);
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
        return await legacyMetadata.GetPlaylistAsync(providerId, externalId, protocol.CancellationToken);
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
        return await legacyMetadata.GetPlaylistTracksAsync(providerId, externalId, protocol.CancellationToken);
    }

    public async Task<ProtocolProviderStream?> OpenStreamAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        ProviderAudioQuality quality,
        string? rangeHeader)
    {
        var rangeStart = ParseRangeStart(rangeHeader);
        var routed = await PlanExactAsync<IProviderStreamingCapability>(
            protocol,
            providerId,
            ProviderCapabilityKind.Streaming,
            "protocol-stream-open",
            new ProviderExternalResourceId(providerId, ProviderResourceKind.Track, externalId),
            quality);
        if (routed.Candidate == null) return null;

        var trackId = routed.Candidate.TrackId ??
                      new ProviderExternalResourceId(providerId, ProviderResourceKind.Track, externalId);
        var outcome = await routed.Candidate.Implementation.GetStreamLeaseAsync(
            routed.Candidate.Context,
            new ProviderStreamLeaseRequest(trackId, quality, rangeStart));
        if (!outcome.IsSuccess) ThrowRouteFailure(outcome.Error!);

        var lease = outcome.RequireValue();
        var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        if (rangeHeader != null && lease.SupportsByteRanges)
        {
            request.Headers.Range = RangeHeaderValue.Parse(rangeHeader);
        }
        var response = await httpClientFactory.CreateClient(StreamingClientName).SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            protocol.CancellationToken);
        request.Dispose();
        return new ProtocolProviderStream(response, lease);
    }

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

    private IReadOnlyList<string> ResolveProviderOrder(ProviderCapabilityKind capability)
    {
        var (settingKey, environmentKey, fallback) = capability switch
        {
            ProviderCapabilityKind.Metadata => ("Providers:MetadataOrder", "MULTI_PROVIDER_METADATA_ORDER", "apple-download,deezer,qobuz"),
            ProviderCapabilityKind.Playlist => ("Providers:PlaylistOrder", "MULTI_PROVIDER_PLAYLIST_ORDER", "spotify,apple-download,deezer,qobuz"),
            ProviderCapabilityKind.Lyrics => ("Providers:LyricsOrder", "MULTI_PROVIDER_LYRICS_ORDER", "spotify,apple-download,lyricsplus,lrclib"),
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
        ProviderAudioQuality quality = ProviderAudioQuality.Any) => new(
        capability,
        actor,
        new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, quality == ProviderAudioQuality.Any
                ? ProviderAudioQuality.HighResolution
                : quality, allowTranscode: true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: false,
            allowSharedAccount: true,
            allowManagedDownloads: false,
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
                ProviderAudioQuality.Lossy,
                ProviderAudioQuality.Lossless,
                ProviderAudioQuality.HighResolution
            ])),
        library: protocol.LibraryScopeId == null
            ? null
            : new ProviderLibraryContext(actor.TenantId, protocol.LibraryScopeId),
        sourceTrackId: sourceTrackId,
        cancellationToken: protocol.CancellationToken);

    private static long? ParseRangeStart(string? rangeHeader)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader)) return null;
        if (!RangeHeaderValue.TryParse(rangeHeader, out var parsed) || parsed.Ranges.Count != 1)
        {
            throw new InvalidOperationException("Only one valid byte range may be requested.");
        }
        var range = parsed.Ranges.Single();
        if (!range.From.HasValue)
        {
            throw new InvalidOperationException("Suffix byte ranges are not supported for provider leases.");
        }
        return range.From.Value;
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
            ArtistIds = item.Artists.Where(artist => artist.ArtistId != null)
                .Select(artist => ProtocolItemId(artist.ArtistId!)).ToList(),
            Album = item.AlbumTitle ?? string.Empty,
            AlbumId = item.AlbumId is { } albumId ? ProtocolItemId(albumId) : null,
            Duration = item.Duration.HasValue ? (int)item.Duration.Value.TotalSeconds : null,
            Isrc = item.Isrc,
            CoverArtUrl = item.Artwork?.PublicUri?.ToString(),
            CoverArtUrlLarge = item.Artwork?.PublicUri?.ToString(),
            IsLocal = false
        };
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
        CoverUrl = item.Artwork?.PublicUri?.ToString()
    };

    private List<T> Merge<T>(
        IEnumerable<T> routed,
        IEnumerable<T> legacy,
        int limit,
        Func<T, string> key,
        Func<T, string?> provider)
    {
        var items = routed.Concat(legacy)
            .DistinctBy(key, StringComparer.Ordinal)
            .ToList();
        var preferred = (configuration?["Providers:MetadataOrder"] ??
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

    private static bool Allowed(string? providerId, IReadOnlySet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;

        var normalized = NormalizeProvider(providerId);
        return allowed.Any(item =>
            NormalizeProvider(item).Equals(normalized, StringComparison.Ordinal));
    }
}
