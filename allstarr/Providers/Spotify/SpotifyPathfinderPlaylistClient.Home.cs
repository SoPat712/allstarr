using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists;

namespace allstarr.Core.Providers.Spotify;

public sealed partial class SpotifyPathfinderPlaylistClient
{
    private const string HomeOperation = "home";
    private readonly ConcurrentDictionary<Guid, string> disabledHomeQueries = new();

    public async Task<ProviderOutcome<IReadOnlyList<ProviderPlaylistSummary>>> GetHomePlaylistsAsync(
        string token, Guid accountId, CancellationToken cancellationToken)
    {
        var operation = new PathfinderOperationDefinition(HomeOperation, settings.HomeQueryHash, 1);
        if (disabledHomeQueries.TryGetValue(accountId, out var disabledHash))
        {
            if (disabledHash == operation.Sha256Hash)
                return ProviderOutcome<IReadOnlyList<ProviderPlaylistSummary>>.Failure(ProviderError.CompatibilityContractChanged());
            disabledHomeQueries.TryRemove(new KeyValuePair<Guid, string>(accountId, disabledHash));
        }
        var zone = TimeZoneInfo.Local;
        var timeZone = zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : "Etc/UTC";
        var response = await QueryAsync(token, operation, new
        {
            includeHomeChapterVideoCards = false,
            homeEndUserIntegration = settings.HomeEndUserIntegration,
            timeZone,
            sp_t = "",
            facet = "",
            sectionItemsLimit = 10,
            includeEpisodeContentRatingsV2 = false
        }, cancellationToken);
        if (!response.Outcome.IsSuccess)
            return ProviderOutcome<IReadOnlyList<ProviderPlaylistSummary>>.Failure(response.Outcome.Error!);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            if (GraphQlFailure(document.RootElement, HomeOperation, logFailure: false) is { } failure)
            {
                if (failure.Code == ProviderError.CompatibilityContractChanged().Code)
                    return DisableHome(accountId, operation.Sha256Hash);
                return ProviderOutcome<IReadOnlyList<ProviderPlaylistSummary>>.Failure(failure);
            }
            if (!TryPath(document.RootElement, out var sections, "data", "home", "sectionContainer", "sections", "items") ||
                sections.ValueKind != JsonValueKind.Array)
                return DisableHome(accountId, operation.Sha256Hash);
            var feeds = new List<ProviderPlaylistSummary>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var section in sections.EnumerateArray())
            {
                if (!TryPath(section, out var items, "sectionItems", "items") || items.ValueKind != JsonValueKind.Array)
                    return DisableHome(accountId, operation.Sha256Hash);
                var title = TryPath(section, out var data, "data") ? ContractText(Text(data, "title"), 500) : null;
                foreach (var item in items.EnumerateArray())
                {
                    if (!TryPath(item, out var playlist, "content", "data") || String(playlist, "__typename") is not { } type)
                        return DisableHome(accountId, operation.Sha256Hash);
                    if (type != "Playlist") continue;
                    if (!SpotifyPlaylistReference.TryNormalize(String(playlist, "uri")!, out var id) ||
                        id == SpotifyPlaylistReference.LikedSongsId)
                        return DisableHome(accountId, operation.Sha256Hash);
                    var artwork = ArtworkUri(playlist);
                    var summary = MapSummary(playlist, id, artwork);
                    if (summary == null) return DisableHome(accountId, operation.Sha256Hash);
                    var spotifyOwner = PlaylistSourceKinds.IsService(ProviderId, summary.Owner.ProviderUserId) ||
                                       PlaylistSourceKinds.IsService(ProviderId, summary.Owner.DisplayName ?? "");
                    if (!spotifyOwner && !id.StartsWith("37i9dQZ", StringComparison.Ordinal) || !seen.Add(id)) continue;
                    feeds.Add(new(summary.Id, summary.Name,
                        spotifyOwner ? new("spotify", summary.Owner.DisplayName) : summary.Owner,
                        summary.SourceRevision, title, summary.Artwork, summary.TrackCount));
                    if (artwork != null)
                    {
                        await CacheArtworkAsync(token, id, summary.SourceRevision, artwork);
                        await CacheArtworkAsync(token, id, summary.Artwork?.Revision, artwork);
                    }
                }
            }
            return ProviderOutcome<IReadOnlyList<ProviderPlaylistSummary>>.Success(feeds);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return DisableHome(accountId, operation.Sha256Hash);
        }
    }

    private ProviderOutcome<IReadOnlyList<ProviderPlaylistSummary>> DisableHome(Guid accountId, string hash)
    {
        if (disabledHomeQueries.TryAdd(accountId, hash))
            logger?.LogWarning("Spotify home discovery disabled for account {AccountId} until its query hash changes. ReasonCode: {ReasonCode}",
                accountId, "provider-contract-changed");
        return ProviderOutcome<IReadOnlyList<ProviderPlaylistSummary>>.Failure(ProviderError.CompatibilityContractChanged());
    }
}
