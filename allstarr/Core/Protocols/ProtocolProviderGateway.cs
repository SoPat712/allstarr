using System.Net.Http.Headers;
using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Services;

namespace allstarr.Core.Protocols;

/// <summary>
/// The provider boundary used by protocol adapters for synthesized resources.
/// Native backend resources never enter this gateway and remain transparent relays.
/// </summary>
public interface IProtocolProviderGateway
{
    Task<SearchResult> SearchAsync(
        ProtocolExecutionContext protocol,
        string query,
        int songLimit,
        int albumLimit,
        int artistLimit);

    Task<Song?> GetSongAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

    Task<Album?> GetAlbumAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

    Task<Artist?> GetArtistAsync(ProtocolExecutionContext protocol, string providerId, string externalId);

    Task<ProtocolProviderStream?> OpenStreamAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId,
        ProviderAudioQuality quality,
        string? rangeHeader);
}

public sealed record ProtocolProviderStream(HttpResponseMessage Response, ProviderStreamLease Lease);

public sealed class ProtocolProviderGateway(
    IProviderRouter router,
    IProviderRegistry registry,
    IProviderRouteAccountResolver accounts,
    IMusicMetadataService legacyMetadata,
    IHttpClientFactory httpClientFactory) : IProtocolProviderGateway
{
    private const string StreamingClientName = "ProtocolProviderStreaming";

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
        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(
            protocol,
            actor,
            ProviderCapabilityKind.Metadata,
            "protocol-metadata-search",
            providerIds: [],
            sourceTrackId: null));

        var routed = new SearchResult();
        foreach (var candidate in plan.Candidates)
        {
            var request = new ProviderMetadataSearchRequest(query, new ProviderPageRequest(fetchLimit));
            var songs = candidate.Implementation.SearchTracksAsync(candidate.Context, request);
            var albums = candidate.Implementation.SearchAlbumsAsync(candidate.Context, request);
            var artists = candidate.Implementation.SearchArtistsAsync(candidate.Context, request);
            await Task.WhenAll(songs, albums, artists);

            if ((await songs).IsSuccess)
            {
                routed.Songs.AddRange((await songs).RequireValue().Items.Select(Map));
            }
            if ((await albums).IsSuccess)
            {
                routed.Albums.AddRange((await albums).RequireValue().Items.Select(Map));
            }
            if ((await artists).IsSuccess)
            {
                routed.Artists.AddRange((await artists).RequireValue().Items.Select(Map));
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
            Songs = Merge(routed.Songs, legacy.Songs.Where(item => Allowed(item.ExternalProvider, allowedCompatibilityProviders)), songLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id)),
            Albums = Merge(routed.Albums, legacy.Albums.Where(item => Allowed(item.ExternalProvider, allowedCompatibilityProviders)), albumLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id)),
            Artists = Merge(routed.Artists, legacy.Artists.Where(item => Allowed(item.ExternalProvider, allowedCompatibilityProviders)), artistLimit, item => Key(item.ExternalProvider, item.ExternalId, item.Id))
        };
    }

    public async Task<Song?> GetSongAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        if (protocol.Actor is null && IsPublicMetadataProvider(providerId))
            return await legacyMetadata.GetSongAsync(providerId, externalId, protocol.CancellationToken);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, providerId, ProviderCapabilityKind.Metadata, "protocol-metadata-get-track");
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(providerId, ProviderResourceKind.Track, externalId);
            var outcome = await routed.Candidate.Implementation.GetTrackAsync(
                routed.Candidate.Context,
                new ProviderTrackLookupRequest(id));
            if (outcome.IsSuccess) return Map(outcome.RequireValue());
            if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return null;
            ThrowRouteFailure(outcome.Error);
        }
        await RequireCompatibilityProviderAsync(protocol, providerId);
        return await legacyMetadata.GetSongAsync(providerId, externalId, protocol.CancellationToken);
    }

    public async Task<Album?> GetAlbumAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        if (protocol.Actor is null && IsPublicMetadataProvider(providerId))
            return await legacyMetadata.GetAlbumAsync(providerId, externalId, protocol.CancellationToken);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, providerId, ProviderCapabilityKind.Metadata, "protocol-metadata-get-album");
        Album? typed = null;
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(providerId, ProviderResourceKind.Album, externalId);
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
        await RequireCompatibilityProviderAsync(protocol, providerId);
        return await legacyMetadata.GetAlbumAsync(providerId, externalId, protocol.CancellationToken);
    }

    public async Task<Artist?> GetArtistAsync(
        ProtocolExecutionContext protocol,
        string providerId,
        string externalId)
    {
        if (protocol.Actor is null && IsPublicMetadataProvider(providerId))
            return await legacyMetadata.GetArtistAsync(providerId, externalId, protocol.CancellationToken);
        var routed = await PlanExactAsync<IProviderMetadataCapability>(
            protocol, providerId, ProviderCapabilityKind.Metadata, "protocol-metadata-get-artist");
        if (routed.Candidate != null)
        {
            var id = new ProviderExternalResourceId(providerId, ProviderResourceKind.Artist, externalId);
            var outcome = await routed.Candidate.Implementation.GetArtistAsync(
                routed.Candidate.Context,
                new ProviderArtistLookupRequest(id));
            if (outcome.IsSuccess) return Map(outcome.RequireValue());
            if (outcome.Error!.Kind == ProviderErrorKind.NotFound) return null;
            ThrowRouteFailure(outcome.Error);
        }
        await RequireCompatibilityProviderAsync(protocol, providerId);
        return await legacyMetadata.GetArtistAsync(providerId, externalId, protocol.CancellationToken);
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
        ProviderActorContext actor)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in registry.FindByCapability(
                     ProviderCapabilityKind.Metadata,
                     includeNonOperational: true))
        {
            var capability = descriptor.Capabilities.Single(item =>
                item.Capability == ProviderCapabilityKind.Metadata);
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
                        ProviderCapabilityKind.Metadata,
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
        string providerId)
    {
        var allowed = await ResolveAllowedCompatibilityProvidersAsync(protocol, protocol.RequireActor());
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
            ProviderErrorKind.AccountNeedsConfiguration => new UnauthorizedAccessException(error.SafeMessage),
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
        var artistIds = item.Artists.Where(artist => artist.ArtistId != null)
            .Select(artist => artist.ArtistId!.Value).ToList();
        return new Song
        {
            Id = $"ext-{item.Id.ProviderId}-{item.Id.Value}",
            ExternalProvider = item.Id.ProviderId,
            ExternalId = item.Id.Value,
            Title = item.Title,
            Artist = artists.FirstOrDefault() ?? string.Empty,
            Artists = artists,
            ArtistId = item.Artists.FirstOrDefault()?.ArtistId?.Value,
            ArtistIds = artistIds,
            Album = item.AlbumTitle ?? string.Empty,
            AlbumId = item.AlbumId?.Value,
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
        ArtistId = item.Artists.FirstOrDefault()?.ArtistId?.Value,
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

    private static List<T> Merge<T>(
        IEnumerable<T> routed,
        IEnumerable<T> legacy,
        int limit,
        Func<T, string> key) => routed.Concat(legacy)
        .DistinctBy(key, StringComparer.Ordinal)
        .Take(Math.Max(0, limit))
        .ToList();

    private static string Key(string? providerId, string? externalId, string fallback) =>
        !string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(externalId)
            ? $"{providerId}:{externalId}"
            : fallback;

    private static bool Allowed(string? providerId, IReadOnlySet<string> allowed) =>
        providerId != null && allowed.Contains(providerId);
}
