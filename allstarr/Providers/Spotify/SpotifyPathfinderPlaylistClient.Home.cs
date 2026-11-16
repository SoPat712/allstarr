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
            // Errors beside readable sections mean one shelf failed to load; the rest of home is still good.
            if (!TryPath(document.RootElement, out var sections, "data", "home", "sectionContainer", "sections", "items") ||
                sections.ValueKind != JsonValueKind.Array)
            {
                if (GraphQlFailure(document.RootElement, HomeOperation, logFailure: false) is { } failure &&
                    failure.Code != ProviderError.CompatibilityContractChanged().Code)
                    return ProviderOutcome<IReadOnlyList<ProviderPlaylistSummary>>.Failure(failure);
                return DisableHome(accountId, operation.Sha256Hash, "missing_sections");
            }
            var feeds = new List<ProviderPlaylistSummary>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unreadable = 0;
            foreach (var section in sections.EnumerateArray())
            {
                // Home mixes shelves and tiles of every kind (shortcuts, podcasts, Liked Songs, placeholders); only playlists become feeds.
                if (!TryPath(section, out var items, "sectionItems", "items") || items.ValueKind != JsonValueKind.Array) continue;
                var title = TryPath(section, out var data, "data") ? ContractText(Text(data, "title"), 500) : null;
                foreach (var item in items.EnumerateArray())
                {
                    if (!TryPath(item, out var playlist, "content", "data") || String(playlist, "__typename") != "Playlist" ||
                        !SpotifyPlaylistReference.TryNormalize(String(playlist, "uri") ?? string.Empty, out var id) ||
                        id == SpotifyPlaylistReference.LikedSongsId) continue;
                    var artwork = ArtworkUri(playlist);
                    var summary = MapSummary(playlist, id, artwork);
                    if (summary == null)
                    {
                        unreadable++;
                        continue;
                    }
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
            // Playlists that are listed but none of which can be read mean the shape changed, not that home is empty.
            if (feeds.Count == 0 && unreadable > 0) return DisableHome(accountId, operation.Sha256Hash, "unreadable_playlists");
            return ProviderOutcome<IReadOnlyList<ProviderPlaylistSummary>>.Success(feeds);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return DisableHome(accountId, operation.Sha256Hash, "unreadable_response");
        }
    }

    private ProviderOutcome<IReadOnlyList<ProviderPlaylistSummary>> DisableHome(Guid accountId, string hash, string detail)
    {
        if (disabledHomeQueries.TryAdd(accountId, hash))
            logger?.LogWarning("Spotify home discovery disabled for account {AccountId} until its query hash changes. ReasonCode: {ReasonCode}. Detail: {Detail}",
                accountId, "provider-contract-changed", detail);
        return ProviderOutcome<IReadOnlyList<ProviderPlaylistSummary>>.Failure(ProviderError.CompatibilityContractChanged());
    }
}
