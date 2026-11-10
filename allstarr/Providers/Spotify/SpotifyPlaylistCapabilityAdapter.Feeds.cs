using allstarr.Core.Capabilities;
using allstarr.Core.Playlists;

namespace allstarr.Core.Providers.Spotify;

public sealed partial class SpotifyPlaylistCapabilityAdapter
{
    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetServiceFeedsAsync(
        ProviderExecutionContext context, ProviderPageRequest page) => ExecuteAsync(context, async (token, cancellationToken) =>
    {
        var library = await _pathfinder.GetUserPlaylistsAsync(token, page, null, cancellationToken);
        if (!library.IsSuccess) return library;
        var saved = library.RequireValue();
        var items = saved.Items.AsEnumerable();
        if (saved.NextCursor == null && !saved.IsPartial)
        {
            var home = await _pathfinder.GetHomePlaylistsAsync(token, context.Account!.AccountId, cancellationToken);
            if (home.IsSuccess) items = items.Concat(home.RequireValue());
        }
        return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new(StableProviderId,
            items.Where(item => PlaylistSourceKinds.Classify(StableProviderId, item.Id.Value, item.Owner.ProviderUserId, null) == PlaylistSourceKinds.Feed)
                .DistinctBy(item => item.Id.Value), saved.NextCursor, saved.IsPartial));
    });
}
