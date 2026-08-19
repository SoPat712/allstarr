using allstarr.Models.Download;
using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using allstarr.Services;
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
    private readonly IEnumerable<IDownloadService> _downloadServices;
    private readonly IReadOnlyList<IPlaybackActivitySource> _playbackSources;
    private readonly IReadOnlyList<IPlaybackMetadataResolver> _metadataResolvers;
    private readonly IMediaAssetResolver _mediaAssets;
    private readonly ILogger<DownloadActivityController> _logger;
    private readonly IPlaybackDeliveryActivitySource? _playbackDeliveries;
    private readonly IDbContextFactory<AllstarrDbContext>? _contextFactory;

    public DownloadActivityController(
        IEnumerable<IDownloadService> downloadServices,
        IEnumerable<IPlaybackActivitySource> playbackSources,
        IEnumerable<IPlaybackMetadataResolver> metadataResolvers,
        IMediaAssetResolver mediaAssets,
        ILogger<DownloadActivityController> logger,
        IPlaybackDeliveryActivitySource? playbackDeliveries = null,
        IDbContextFactory<AllstarrDbContext>? contextFactory = null)
    {
        _downloadServices = downloadServices;
        _playbackSources = playbackSources.ToList();
        _metadataResolvers = metadataResolvers.ToList();
        _mediaAssets = mediaAssets;
        _logger = logger;
        _playbackDeliveries = playbackDeliveries;
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Returns the current download queue as JSON.
    /// </summary>
    [HttpGet("queue")]
    public async Task<IActionResult> GetDownloadQueue()
    {
        var allDownloads = await GetAllActivityEntriesAsync(HttpContext.RequestAborted);
        return Ok(allDownloads);
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

        foreach (var state in states)
        {
            var itemId = NormalizeExternalItemId(state.ItemId);
            var metadata = await TryResolvePlaybackMetadataAsync(itemId, cancellationToken);
            var duration = metadata?.DurationSeconds;
            var position = (int)Math.Max(0, state.PositionTicks / TimeSpan.TicksPerSecond);
            deliveryState.TryGetValue(DeliveryKey(state.UserId, itemId), out var delivery);
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
                ProviderId = delivery?.Event.ProviderId ?? ResolvePlaybackProvider(itemId),
                ProviderAccountName = delivery?.ProviderAccountName,
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
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var events = await db.ListeningEvents.AsNoTracking()
            .Where(item => item.TenantId == tenantId && userIds.Contains(item.OwnerUserId) &&
                trackReferences.Contains(item.TrackReference) && item.UpdatedAt >= DateTimeOffset.UtcNow.AddHours(-8))
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

    private async Task<List<DownloadActivityEntry>> GetAllActivityEntriesAsync(CancellationToken cancellationToken)
    {
        var allDownloads = new List<DownloadInfo>();
        foreach (var service in _downloadServices)
        {
            allDownloads.AddRange(service.GetActiveDownloads());
        }

        var orderedDownloads = allDownloads
            .OrderByDescending(d => d.Status == DownloadStatus.InProgress)
            .ThenByDescending(d => d.StartedAt)
            .ToList();

        var playbackByItemId = _playbackSources
            .SelectMany(source => source.GetActivePlaybackStates(TimeSpan.FromMinutes(5)))
            .GroupBy(state => NormalizeExternalItemId(state.ItemId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(state => state.LastActivity).First());

        var entries = orderedDownloads
            .Select(download =>
            {
                var normalizedSongId = NormalizeExternalItemId(download.SongId);
                var hasPlayback = playbackByItemId.TryGetValue(normalizedSongId, out var playbackState);
                var playbackProgress = hasPlayback && download.DurationSeconds.GetValueOrDefault() > 0
                    ? Math.Clamp(
                        playbackState!.PositionTicks / (double)TimeSpan.TicksPerSecond / download.DurationSeconds!.Value,
                        0d,
                        1d)
                    : (double?)null;

                return new DownloadActivityEntry
                {
                    SongId = download.SongId,
                    ExternalId = download.ExternalId,
                    ExternalProvider = download.ExternalProvider,
                    Title = download.Title,
                    Artist = download.Artist,
                    Status = download.Status,
                    Progress = download.Progress,
                    RequestedForStreaming = download.RequestedForStreaming,
                    CoverArtUrl = string.IsNullOrWhiteSpace(download.CoverArtUrl)
                        ? null
                        : ArtworkUrl(normalizedSongId),
                    DurationSeconds = download.DurationSeconds,
                    LocalPath = download.LocalPath,
                    ErrorMessage = download.ErrorMessage,
                    StartedAt = download.StartedAt,
                    CompletedAt = download.CompletedAt,
                    IsPlaying = hasPlayback,
                    PlaybackLastActivity = hasPlayback ? playbackState!.LastActivity : null,
                    PlaybackPositionSeconds = hasPlayback
                        ? (int)Math.Max(0, playbackState!.PositionTicks / TimeSpan.TicksPerSecond)
                        : null,
                    PlaybackProgress = playbackProgress,
                    Scrobbled = hasPlayback && _playbackDeliveries?.WasDelivered(
                        normalizedSongId,
                        playbackState!.DeviceId) == true
                };
            })
            .ToList();

        var knownIds = orderedDownloads
            .Select(download => NormalizeExternalItemId(download.SongId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (itemId, playbackState) in playbackByItemId)
        {
            if (string.IsNullOrWhiteSpace(itemId) || knownIds.Contains(itemId))
            {
                continue;
            }

            var playbackMetadata = await TryResolvePlaybackMetadataAsync(itemId, cancellationToken);

            entries.Add(new DownloadActivityEntry
            {
                SongId = itemId,
                ExternalId = itemId,
                ExternalProvider = ResolvePlaybackProvider(itemId),
                Title = playbackMetadata?.Title ?? ResolvePlaybackTitle(itemId),
                Artist = playbackMetadata?.Artist ?? "External provider",
                Status = DownloadStatus.Completed,
                Progress = 1,
                RequestedForStreaming = false,
                StartedAt = playbackState.LastActivity,
                IsPlaying = true,
                PlaybackLastActivity = playbackState.LastActivity,
                CoverArtUrl = string.IsNullOrWhiteSpace(playbackMetadata?.CoverArtUrl)
                    ? null
                    : ArtworkUrl(itemId),
                DurationSeconds = playbackMetadata?.DurationSeconds,
                PlaybackPositionSeconds = (int)Math.Max(0, playbackState.PositionTicks / TimeSpan.TicksPerSecond),
                PlaybackProgress = playbackMetadata?.DurationSeconds > 0
                    ? Math.Clamp(
                        playbackState.PositionTicks / (double)TimeSpan.TicksPerSecond /
                        playbackMetadata.DurationSeconds.Value,
                        0d,
                        1d)
                    : null,
                Scrobbled = _playbackDeliveries?.WasDelivered(itemId, playbackState.DeviceId) == true
            });
        }

        return entries
            .OrderByDescending(entry => entry.IsPlaying)
            .ThenByDescending(entry => entry.Status == DownloadStatus.InProgress)
            .ThenByDescending(entry => entry.StartedAt)
            .ToList();
    }

    private async Task<PlaybackTrackMetadata?> TryResolvePlaybackMetadataAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        foreach (var resolver in _metadataResolvers)
        {
            try
            {
                var metadata = await resolver.ResolveAsync(itemId, cancellationToken);
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

    private sealed class DownloadActivityEntry : DownloadInfo
    {
        public bool IsPlaying { get; init; }
        public DateTime? PlaybackLastActivity { get; init; }
        public int? PlaybackPositionSeconds { get; init; }
        public double? PlaybackProgress { get; init; }
        public bool Scrobbled { get; init; }
    }

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
