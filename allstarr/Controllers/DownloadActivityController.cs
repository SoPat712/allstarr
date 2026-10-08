using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using allstarr.Filters;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/downloads")]
[ServiceFilter(typeof(AdminPortFilter))]
public class DownloadActivityController : ControllerBase
{
    private readonly IReadOnlyList<IPlaybackActivitySource> _playbackSources;
    private readonly IReadOnlyList<IPlaybackMetadataResolver> _metadataResolvers;
    private readonly IMediaAssetResolver _mediaAssets;
    private readonly IBackendLibraryAccessResolver _libraryAccess;
    private readonly ILogger<DownloadActivityController> _logger;
    private readonly IPlaybackDeliveryActivitySource? _playbackDeliveries;
    private readonly IDbContextFactory<AllstarrDbContext>? _contextFactory;

    public DownloadActivityController(
        IEnumerable<IPlaybackActivitySource> playbackSources,
        IEnumerable<IPlaybackMetadataResolver> metadataResolvers,
        IMediaAssetResolver mediaAssets,
        ILogger<DownloadActivityController> logger,
        IBackendLibraryAccessResolver libraryAccess,
        IPlaybackDeliveryActivitySource? playbackDeliveries = null,
        IDbContextFactory<AllstarrDbContext>? contextFactory = null)
    {
        _playbackSources = playbackSources.ToList();
        _metadataResolvers = metadataResolvers.ToList();
        _mediaAssets = mediaAssets;
        _logger = logger;
        _libraryAccess = libraryAccess;
        _playbackDeliveries = playbackDeliveries;
        _contextFactory = contextFactory;
    }

    [HttpGet("/api/admin/ui/now-playing")]
    public async Task<IActionResult> GetNowPlaying(CancellationToken cancellationToken)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession { IsAdministrator: true } session)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Administrator permissions required" });
        }

        var states = _playbackSources
            .SelectMany(source => source.GetActivePlaybackStates(TimeSpan.FromMinutes(5)))
            .Where(state => state.TenantId == session.TenantId)
            .GroupBy(state => state.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(state => state.LastActivity).First())
            .OrderByDescending(state => state.LastActivity)
            .ToList();
        var items = new List<NowPlayingEntry>(states.Count);
        var deliveryState = await LoadDeliveryStateAsync(session, states, cancellationToken);

        var viewer = session.AllstarrUserId is { } viewerId
            ? (await _libraryAccess.ResolveUserAsync(viewerId, cancellationToken)).Context
            : null;
        foreach (var state in states)
        {
            var itemId = NormalizeExternalItemId(state.ItemId);
            var metadata = await TryResolvePlaybackMetadataAsync(itemId, viewer, cancellationToken);
            var duration = metadata?.DurationSeconds;
            var position = (int)Math.Max(0, state.PositionTicks / TimeSpan.TicksPerSecond);
            deliveryState.TryGetValue(DeliveryKey(state.UserId, itemId), out var delivery);
            var streamSource = _playbackDeliveries?.StreamFor(state.TenantId, state.UserId, state.DeviceId, itemId);
            var externalIdentity = ExternalPlaybackMetadataResolver.ParseTrackIdentity(itemId);
            var threshold = duration is >= 30 ? Math.Min(duration.Value / 2d, 240d) : (double?)null;
            items.Add(new NowPlayingEntry
            {
                DeviceId = state.DeviceId,
                UserId = state.UserId,
                UserName = state.UserName ?? "Unknown listener",
                AvatarUrl = string.IsNullOrWhiteSpace(state.BackendUserId)
                    ? null
                    : $"/api/admin/ui/users/{Uri.EscapeDataString(state.BackendUserId)}/avatar",
                Client = state.Client ?? "Music client",
                Device = state.Device,
                ItemId = itemId,
                Title = metadata?.Title ?? ResolvePlaybackTitle(itemId),
                Artist = metadata?.Artist ?? "Unknown artist",
                Album = metadata?.Album,
                ProviderId = streamSource?.ProviderId ?? ResolvePlaybackProvider(itemId),
                CatalogProviderId = ResolvePlaybackProvider(itemId),
                SourceConfirmed = streamSource != null || externalIdentity == null,
                Cached = streamSource?.Cached ?? false,
                RouteReason = streamSource?.SelectionReason ??
                    (externalIdentity == null ? "native-local-library" : null),
                ProviderAccountName = streamSource?.AccountId == delivery?.Event.ProviderAccountId
                    ? delivery?.ProviderAccountName : null,
                ArtworkUrl = string.IsNullOrWhiteSpace(metadata?.CoverArtUrl) ? null : ArtworkUrl(itemId),
                PositionSeconds = position,
                DurationSeconds = duration,
                Progress = duration > 0 ? Math.Clamp(position / (double)duration.Value, 0d, 1d) : null,
                LastActivity = state.LastActivity,
                ScrobbleThresholdSeconds = threshold,
                ScrobbleEligible = threshold.HasValue && position >= threshold.Value,
                ScrobbleDeliveries = delivery?.Checkpoints.Select(item => new ScrobbleDeliveryEntry
                {
                    TargetId = item.TargetId,
                    Kind = item.Kind.ToString().ToLowerInvariant(),
                    State = item.State.ToString().ToLowerInvariant(),
                    RequiresReauthentication = item.RequiresReauthentication,
                    Message = item.SafeMessage,
                    UpdatedAt = item.UpdatedAt
                }).ToList() ?? [],
                Scrobbled = _playbackDeliveries?.WasDelivered(itemId, state.DeviceId) == true ||
                    delivery?.Checkpoints.Any(item => item.Kind == PlaybackScrobbleDeliveryKind.Completed &&
                        item.State is ScopedPlaybackScrobbleOutcome.Delivered or ScopedPlaybackScrobbleOutcome.Ignored) == true
            });
        }

        return Ok(new { items });
    }

    private async Task<Dictionary<string, PlaybackDeliveryState>> LoadDeliveryStateAsync(
        AdminAuthSession session,
        IReadOnlyCollection<PlaybackActivityState> states,
        CancellationToken cancellationToken)
    {
        if (_contextFactory == null || session.TenantId is not { } tenantId) return [];
        var userIds = states.Select(item => item.UserId).OfType<Guid>().Distinct().ToArray();
        if (userIds.Length == 0) return [];
        var trackReferences = states
            .SelectMany(item => new[] { item.ItemId, NormalizeExternalItemId(item.ItemId) })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var updatedAfter = DateTimeOffset.UtcNow.AddHours(-8);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var events = await db.ListeningEvents.AsNoTracking()
            .Where(item => item.TenantId == tenantId && userIds.Contains(item.OwnerUserId) &&
                trackReferences.Contains(item.TrackReference) && item.UpdatedAt >= updatedAfter)
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken);
        var latest = events
            .GroupBy(item => DeliveryKey(item.OwnerUserId, NormalizeExternalItemId(item.TrackReference)))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var occurrenceKeys = latest.Values.Select(item => item.OccurrenceKey).Distinct().ToArray();
        var checkpoints = occurrenceKeys.Length == 0
            ? []
            : await db.PlaybackDeliveryCheckpoints.AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.OccurrenceKey != null &&
                    occurrenceKeys.Contains(item.OccurrenceKey))
                .OrderByDescending(item => item.Kind)
                .ThenByDescending(item => item.UpdatedAt)
                .ToListAsync(cancellationToken);
        var accountIds = latest.Values.Select(item => item.ProviderAccountId).OfType<Guid>().Distinct().ToArray();
        var accountNames = accountIds.Length == 0
            ? []
            : await db.ProviderAccounts.AsNoTracking()
                .Where(item => accountIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);

        return latest.ToDictionary(
            item => item.Key,
            item => new PlaybackDeliveryState(
                item.Value,
                checkpoints.Where(checkpoint => checkpoint.OccurrenceKey == item.Value.OccurrenceKey)
                    .GroupBy(checkpoint => checkpoint.TargetId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(checkpoint => checkpoint.TargetId, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                item.Value.ProviderAccountId is { } accountId ? accountNames.GetValueOrDefault(accountId) : null),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string DeliveryKey(Guid? userId, string itemId) =>
        $"{userId?.ToString("N") ?? "unknown"}\n{itemId}";

    [HttpGet("artwork/{itemId}")]
    public async Task<IActionResult> GetPlaybackArtwork(
        string itemId,
        CancellationToken cancellationToken)
    {
        var normalizedItemId = NormalizeExternalItemId(itemId);
        var session = HttpContext.Items.TryGetValue(
            AdminAuthSessionService.HttpContextSessionItemKey, out var value)
            ? value as AdminAuthSession
            : null;
        if (ExternalPlaybackMetadataResolver.ParseTrackIdentity(normalizedItemId) == null)
        {
            if (session?.AllstarrUserId is not { } viewerId) return NotFound();
            var access = await _libraryAccess.ResolveUserAsync(viewerId, cancellationToken);
            if (access.Context == null || !access.Access.Succeeded || access.Access.LibraryIds.Length == 0) return NotFound();
            foreach (var resolver in _metadataResolvers)
            {
                var artwork = await resolver.ResolveArtworkAsync(normalizedItemId, access.Context, cancellationToken);
                if (artwork == null) continue;
                Response.Headers.CacheControl = "private, no-store";
                return File(artwork.Content, artwork.ContentType);
            }
            return NotFound();
        }
        var asset = await _mediaAssets.ResolveAsync(
            new MediaAssetIdentity(
                session?.TenantId,
                session?.AllstarrUserId,
                null,
                ResolvePlaybackProvider(normalizedItemId),
                "track",
                normalizedItemId,
                Width: 96),
            async token =>
            {
                foreach (var resolver in _metadataResolvers)
                {
                    var artwork = await resolver.ResolveArtworkAsync(normalizedItemId, token);
                    if (artwork != null)
                        return new MediaAssetSource(artwork.Content, artwork.ContentType);
                }
                return null;
            },
            5 * 1024 * 1024,
            cancellationToken);

        if (asset == null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=300";
        return File(asset.Bytes, asset.ContentType);
    }

    private async Task<PlaybackTrackMetadata?> TryResolvePlaybackMetadataAsync(
        string itemId,
        ProtocolExecutionContext? viewer,
        CancellationToken cancellationToken)
    {
        foreach (var resolver in _metadataResolvers)
        {
            try
            {
                var metadata = viewer == null
                    ? await resolver.ResolveAsync(itemId, cancellationToken)
                    : await resolver.ResolveAsync(itemId, viewer, cancellationToken);
                if (metadata != null)
                {
                    return metadata;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Playback metadata resolver failed for item {ItemId}", itemId);
            }
        }

        return null;
    }

    private static string NormalizeExternalItemId(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
        {
            return itemId;
        }

        var remainder = itemId[4..];
        if (remainder.Length == 0)
        {
            return itemId;
        }

        if (new[] { "-song-", "-album-", "-artist-" }.Any(marker =>
                remainder.IndexOf(marker, StringComparison.OrdinalIgnoreCase) > 0))
        {
            return itemId;
        }

        var separator = remainder.IndexOf('-');
        return separator > 0 && separator + 1 < remainder.Length
            ? $"ext-{remainder[..separator]}-song-{remainder[(separator + 1)..]}"
            : itemId;
    }

    private static string ResolvePlaybackProvider(string itemId)
    {
        if (!itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
        {
            return "jellyfin";
        }

        return ExternalPlaybackMetadataResolver.ParseTrackIdentity(itemId)?.Provider.ToLowerInvariant() ?? "external";
    }

    private static string ResolvePlaybackTitle(string itemId)
    {
        if (!itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
        {
            return "Local Jellyfin track";
        }

        return ExternalPlaybackMetadataResolver.ParseTrackIdentity(itemId)?.ExternalId ?? "External track";
    }

    private static string ArtworkUrl(string itemId) =>
        $"/api/admin/downloads/artwork/{Uri.EscapeDataString(itemId)}";

    private sealed class NowPlayingEntry
    {
        public required string DeviceId { get; init; }
        public Guid? UserId { get; init; }
        public required string UserName { get; init; }
        public string? AvatarUrl { get; init; }
        public required string Client { get; init; }
        public string? Device { get; init; }
        public required string ItemId { get; init; }
        public required string Title { get; init; }
        public required string Artist { get; init; }
        public string? Album { get; init; }
        public required string ProviderId { get; init; }
        public required string CatalogProviderId { get; init; }
        public bool SourceConfirmed { get; init; }
        public bool Cached { get; init; }
        public string? RouteReason { get; init; }
        public string? ProviderAccountName { get; init; }
        public string? ArtworkUrl { get; init; }
        public int PositionSeconds { get; init; }
        public int? DurationSeconds { get; init; }
        public double? Progress { get; init; }
        public DateTime LastActivity { get; init; }
        public double? ScrobbleThresholdSeconds { get; init; }
        public bool ScrobbleEligible { get; init; }
        public IReadOnlyList<ScrobbleDeliveryEntry> ScrobbleDeliveries { get; init; } = [];
        public bool Scrobbled { get; init; }
    }

    private sealed class ScrobbleDeliveryEntry
    {
        public required string TargetId { get; init; }
        public required string Kind { get; init; }
        public required string State { get; init; }
        public bool RequiresReauthentication { get; init; }
        public string? Message { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }

    private sealed record PlaybackDeliveryState(
        ListeningEventRecord Event,
        IReadOnlyList<PlaybackDeliveryCheckpointEntity> Checkpoints,
        string? ProviderAccountName);
}
