using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Routing;
using allstarr.Models.Domain;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private async Task<List<Album>> EnrichAlbumReleasesAsync(ProtocolExecutionContext protocol, List<Album> albums,
        IReadOnlyList<ProviderRouteCandidate<IProviderMetadataCapability>> candidates)
    {
        albums = MusicAlbumReleases.Collapse(albums);
        var incomplete = albums.GroupBy(MusicAlbumReleases.FamilyKey).Where(group => group.Count() > 1)
            .SelectMany(group => group.Where(album => !MusicAlbumReleases.HasReleaseEvidence(album) ||
                !MusicAlbumReleases.HasTrackEvidence(album) && group.Any(other =>
                    !ReferenceEquals(album, other) && album.IsExplicit == other.IsExplicit &&
                    MusicAlbumReleases.NormalizeUpc(album.Upc) != MusicAlbumReleases.NormalizeUpc(other.Upc))))
            .Where(album => !string.IsNullOrWhiteSpace(album.ExternalProvider) && !string.IsNullOrWhiteSpace(album.ExternalId))
            .DistinctBy(album => (album.ExternalProvider, album.ExternalId)).Take(16).ToArray();
        if (incomplete.Length == 0) return albums;
        var clock = timeProvider ?? TimeProvider.System;
        var deadline = clock.GetUtcNow().AddSeconds(3);
        if (protocol.Deadline < deadline) deadline = protocol.Deadline;
        if (deadline <= clock.GetUtcNow()) return albums;
        using var timeout = new CancellationTokenSource(deadline - clock.GetUtcNow(), clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(protocol.CancellationToken, timeout.Token);
        var enriched = await Task.WhenAll(incomplete.Select(async album =>
        {
            var candidate = candidates.FirstOrDefault(item => NormalizeProvider(item.Provider.Id) == NormalizeProvider(album.ExternalProvider!));
            if (candidate == null) return (Original: album, Full: (Album?)null);
            try
            {
                var cached = await ReadCachedMetadataAsync<Album>(candidate.Context.Actor, candidate.Provider.Id,
                    ProviderResourceKind.Album, album.ExternalId!, true, candidate.Context.Account).WaitAsync(cancellation.Token);
                if (cached != null && MusicAlbumReleases.HasTrackEvidence(cached))
                    return (Original: album, Full: cached);
                if (cached != null && !MusicAlbumReleases.HasReleaseEvidence(album) && MusicAlbumReleases.HasReleaseEvidence(cached))
                    return (Original: album, Full: cached);
                var context = new ProviderExecutionContext(candidate.Context.Actor, candidate.Provider.Id, candidate.Context.Account,
                    candidate.Context.Policy, "album-release-evidence", candidate.Context.CorrelationId, deadline, cancellation.Token);
                var id = new ProviderExternalResourceId(candidate.Provider.Id, ProviderResourceKind.Album, album.ExternalId!);
                var outcome = await candidate.Implementation.GetAlbumAsync(context, new(id)).WaitAsync(cancellation.Token);
                if (!outcome.IsSuccess || outcome.RequireValue().Id != id) return (Original: album, Full: (Album?)null);
                var full = Map(outcome.RequireValue());
                await CacheResourcesAsync(context, [full], [], true);
                return (Original: album, Full: (Album?)full);
            }
            catch (Exception) when (!protocol.CancellationToken.IsCancellationRequested)
            {
                return (Original: album, Full: (Album?)null);
            }
        }));
        protocol.CancellationToken.ThrowIfCancellationRequested();
        return albums.Select(album => enriched.FirstOrDefault(item => ReferenceEquals(item.Original, album)).Full ?? album).ToList();
    }
}
