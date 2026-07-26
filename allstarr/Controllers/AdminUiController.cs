using System.Text.Json;
using allstarr.Filters;
using allstarr.Core.Identity;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Models.Admin;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Admin;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/ui")]
[ServiceFilter(typeof(AdminPortFilter))]
public class AdminUiController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly DeezerSettings _deezerSettings;
    private readonly QobuzSettings _qobuzSettings;
    private readonly SquidWTFSettings _squidWtfSettings;
    private readonly AppleDownloadSettings _appleMusicSettings;
    private readonly MusicBrainzSettings _musicBrainzSettings;
    private readonly ExtensionManager _extensionManager;
    private readonly ProviderStatusManager _providerStatusManager;
    private readonly ProviderAccountManagementMode _providerAccountManagementMode;
    private readonly IProviderRegistry? _providerRegistry;
    private readonly ITrackMatchRepository _trackMatches;

    public AdminUiController(
        IConfiguration configuration,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings,
        IOptions<SquidWTFSettings> squidWtfSettings,
        IOptions<AppleDownloadSettings> appleMusicSettings,
        IOptions<MusicBrainzSettings> musicBrainzSettings,
        ExtensionManager extensionManager,
        ProviderStatusManager providerStatusManager,
        ProviderAccountManagementOptions providerAccountManagementOptions,
        ITrackMatchRepository trackMatches,
        IProviderRegistry? providerRegistry = null)
    {
        _configuration = configuration;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _squidWtfSettings = squidWtfSettings.Value;
        _appleMusicSettings = appleMusicSettings.Value;
        _musicBrainzSettings = musicBrainzSettings.Value;
        _extensionManager = extensionManager;
        _providerStatusManager = providerStatusManager;
        _providerAccountManagementMode = providerAccountManagementOptions.ParseManagementMode();
        _trackMatches = trackMatches;
        _providerRegistry = providerRegistry;
    }

    [HttpGet("schema")]
    public IActionResult GetSchema()
    {
        var activeBackend = _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin";
        if (!IsAdministratorSession())
        {
            return Ok(new AdminUiSchemaResponse
            {
                ActiveBackend = activeBackend,
                ProviderAccountManagementMode = _providerAccountManagementMode.ToString(),
                Providers = BuildProviders().Select(item => new AdminUiProvider
                {
                    Id = item.Id,
                    Name = item.Name,
                    Icon = item.Icon,
                    LogoUrl = item.LogoUrl,
                    AccountSettings = item.AccountSettings,
                    ConnectionKind = item.ConnectionKind,
                    Audience = item.Audience,
                    ImplementationOrigin = item.ImplementationOrigin,
                    RouteId = item.RouteId,
                    CapabilityRoutes = item.CapabilityRoutes
                }).ToList(),
                Routes =
                [
                    Route("sources", "#/sources", "Sources", "sources"),
                    Route("settings", "#/settings", "Settings", "system")
                ]
            });
        }

        var schema = new AdminUiSchemaResponse
        {
            ActiveBackend = activeBackend,
            ProviderAccountManagementMode = _providerAccountManagementMode.ToString(),
            Routes = BuildRoutes(),
            Backends = BuildBackends(),
            Providers = BuildProviders(),
            ProviderSupportMatrix = CurrentProviderSupportCatalog.All.ToList(),
            MultiProviderCategories = ["metadata", "streaming", "download", "playlist", "lyrics", "enrichment"],
            PriorityGroups = BuildPriorityGroups(),
            ConfigSections = BuildConfigSections(),
            ExtensionStore = new AdminUiExtensionStore
            {
                Repositories = [],
                RegistryEnvKey = "",
                StoreEndpoint = "/api/admin/extensions/store",
                InstalledEndpoint = "/api/admin/extensions/installed"
            },
            PluginCapabilities =
            [
                new()
                {
                    Id = "metadata",
                    Label = "Metadata and search",
                    Description = "Discovery only (titles, artists, albums, ISRCs). Playable selection always uses Streaming + Download order.",
                    Supported = true
                },
                new()
                {
                    Id = "playlist",
                    Label = "Playlist discovery",
                    Description = "Enabled playlist extensions use the same account-scoped provider contract as built-ins.",
                    Supported = true
                },
                new()
                {
                    Id = "download",
                    Label = "Download providers",
                    Description = "Enabled download extensions run as durable, idempotent jobs in a managed workspace.",
                    Supported = true
                },
                new()
                {
                    Id = "lyrics",
                    Label = "Lyrics providers",
                    Description = "Enabled lyrics extensions participate through the typed provider contract.",
                    Supported = true
                }
            ]
        };

        return Ok(schema);
    }

    [HttpGet("provider-summaries")]
    public async Task<IActionResult> GetProviderSummaries(CancellationToken cancellationToken = default)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionValue) ||
            sessionValue is not AdminAuthSession { IsAdministrator: true, TenantId: { } tenantId })
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Administrator permissions required" });
        }

        var contextFactory = HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.ProviderAccounts.AsNoTracking().ToListAsync(cancellationToken);
        var accountIds = accounts.Select(item => item.Id).ToArray();
        var rollups = await context.ProviderHealthRollups.AsNoTracking()
            .Where(item => accountIds.Contains(item.ProviderAccountId))
            .OrderByDescending(item => item.UpdatedAt)
            .Take(1000)
            .ToListAsync(cancellationToken);

        var summaries = accounts
            .GroupBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ids = group.Select(item => item.Id).ToHashSet();
                var samples = rollups.Where(item => ids.Contains(item.ProviderAccountId)).ToList();
                var latestSamples = samples
                    .GroupBy(item => new { item.ProviderAccountId, item.Capability })
                    .Select(capability => capability.OrderByDescending(item => item.UpdatedAt).First())
                    .ToList();
                var sampleCount = samples.Sum(item => item.SampleCount);
                var healthy = latestSamples.Count(item =>
                    string.Equals(item.LastState.ToString(), "Healthy", StringComparison.OrdinalIgnoreCase));
                var failed = latestSamples.Count - healthy;
                return new
                {
                    providerId = group.Key,
                    connectedAccountName = group.Where(item => item.Enabled).Select(item => item.DisplayName).FirstOrDefault(),
                    enabledAccountCount = group.Count(item => item.Enabled),
                    capabilityTotal = latestSamples.Count,
                    healthyCapabilityCount = healthy,
                    failedCapabilityCount = failed,
                    lastCheckedAt = samples.Count > 0 ? samples.Max(item => item.UpdatedAt) : (DateTimeOffset?)null,
                    successRate = sampleCount > 0
                        ? samples.Sum(item => item.SuccessCount) / (double)sampleCount
                        : (double?)null,
                    p95LatencyMilliseconds = samples.Where(item => item.P95LatencyMilliseconds.HasValue)
                        .Select(item => item.P95LatencyMilliseconds).Max(),
                    lastFailureCode = samples.OrderByDescending(item => item.UpdatedAt)
                        .Select(item => item.LastFailureCode).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
                };
            })
            .OrderBy(item => item.providerId)
            .ToList();

        return Ok(new { providers = summaries });
    }

    [HttpGet("activity")]
    public async Task<IActionResult> GetDashboardActivity(
        [FromQuery] int limit = 20,
        [FromQuery] DateTimeOffset? before = null,
        [FromQuery] Guid? beforeId = null,
        CancellationToken cancellationToken = default)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionValue) ||
            sessionValue is not AdminAuthSession { IsAdministrator: true, TenantId: { } tenantId })
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Administrator permissions required" });
        }

        limit = Math.Clamp(limit, 1, 100);
        var scanLimit = Math.Min(500, limit * 5);
        var contextFactory = HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.ProviderAccounts.AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var accountIds = accounts.Keys.ToArray();
        var jobs = await context.Jobs.AsNoTracking()
            .Where(item => item.TenantId == tenantId && (!before.HasValue || item.UpdatedAt < before.Value ||
                (item.UpdatedAt == before.Value && beforeId.HasValue && item.Id.CompareTo(beforeId.Value) < 0)))
            .OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id)
            .Take(scanLimit)
            .ToListAsync(cancellationToken);
        var health = await context.ProviderHealthSamples.AsNoTracking()
            .Where(item => accountIds.Contains(item.ProviderAccountId) && (!before.HasValue || item.ObservedAt < before.Value ||
                (item.ObservedAt == before.Value && beforeId.HasValue && item.Id.CompareTo(beforeId.Value) < 0)))
            .OrderByDescending(item => item.ObservedAt).ThenByDescending(item => item.Id)
            .Take(scanLimit)
            .ToListAsync(cancellationToken);
        var playlistRuns = await context.PlaylistSyncRuns.AsNoTracking()
            .Where(item => item.TenantId == tenantId && (!before.HasValue || (item.CompletedAt ?? item.StartedAt) < before.Value ||
                ((item.CompletedAt ?? item.StartedAt) == before.Value && beforeId.HasValue && item.Id.CompareTo(beforeId.Value) < 0)))
            .OrderByDescending(item => item.CompletedAt ?? item.StartedAt).ThenByDescending(item => item.Id)
            .Take(scanLimit)
            .ToListAsync(cancellationToken);
        var playlistLinkIds = playlistRuns.Select(item => item.PlaylistLinkId).Distinct().ToArray();
        var playlistNames = playlistLinkIds.Length == 0
            ? new Dictionary<Guid, string>()
            : (await context.PlaylistSourceSnapshots.AsNoTracking()
                .Where(item => playlistLinkIds.Contains(item.PlaylistLinkId))
                .GroupBy(item => item.PlaylistLinkId)
                .Select(group => group.OrderByDescending(item => item.SnapshotVersion)
                    .ThenByDescending(item => item.RetrievedAt).First())
                .ToListAsync(cancellationToken))
                .ToDictionary(item => item.PlaylistLinkId, item => item.Name);
        var matchActivity = await _trackMatches.GetActivityDataAsync(
            new TrackMatchActor(tenantId, Guid.Empty, true),
            before,
            beforeId,
            scanLimit,
            cancellationToken);
        var matches = matchActivity.Decisions;
        var externalSnapshots = matchActivity.Snapshots.ToDictionary(item => item.Id);
        var providerIdentities = matchActivity.ProviderIdentities.ToDictionary(item => item.Id);
        var libraryTracks = matchActivity.LibraryTracks.ToDictionary(item => item.Id);
        var audits = await context.AuditEvents.AsNoTracking()
            .Where(item => item.TenantId == tenantId && (!before.HasValue || item.CreatedAt < before.Value ||
                (item.CreatedAt == before.Value && beforeId.HasValue && item.Id.CompareTo(beforeId.Value) < 0)))
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Take(scanLimit)
            .ToListAsync(cancellationToken);
        var extensionLogs = await context.ExtensionLogs.AsNoTracking()
            .Where(item => !before.HasValue || item.CreatedAt < before.Value ||
                (item.CreatedAt == before.Value && beforeId.HasValue && item.Id.CompareTo(beforeId.Value) < 0))
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Take(scanLimit)
            .ToListAsync(cancellationToken);
        var downloadArtifacts = await context.ProviderDownloadArtifacts.AsNoTracking()
            .Where(item => item.TenantId == tenantId && (!before.HasValue || item.VerifiedAt < before.Value ||
                (item.VerifiedAt == before.Value && beforeId.HasValue && item.Id.CompareTo(beforeId.Value) < 0)))
            .OrderByDescending(item => item.VerifiedAt).ThenByDescending(item => item.Id)
            .Take(scanLimit)
            .ToListAsync(cancellationToken);

        var activity = new List<AdminUiActivityItem>();
        activity.AddRange(jobs.Select(item => new AdminUiActivityItem(
            item.Id.ToString("N"),
            "job",
            item.ProviderAccountId.HasValue && accounts.TryGetValue(item.ProviderAccountId.Value, out var account)
                ? account.ProviderId
                : "system",
            item.Type,
            item.State.ToString().ToLowerInvariant(),
            item.LastErrorMessage ?? $"{item.AttemptCount} run attempt{(item.AttemptCount == 1 ? "" : "s")}",
            item.UpdatedAt,
            item.CorrelationId,
            SeverityForState(item.State.ToString()),
            item.ProviderAccountId.HasValue && accounts.TryGetValue(item.ProviderAccountId.Value, out var providerAccount)
                ? providerAccount.ProviderId : null)));
        activity.AddRange(health.Select(item =>
        {
            var provider = accounts.TryGetValue(item.ProviderAccountId, out var account)
                ? account.ProviderId
                : "provider";
            return new AdminUiActivityItem(
                item.Id.ToString("N"),
                "provider_health",
                provider,
                $"{item.Capability} check",
                item.State.ToString().ToLowerInvariant(),
                item.FailureCode ?? (item.LatencyMilliseconds.HasValue ? $"{item.LatencyMilliseconds} ms" : "Connection checked"),
                item.ObservedAt,
                Severity: SeverityForState(item.State.ToString()),
                ProviderId: provider);
        }));
        activity.AddRange(playlistRuns.Select(item => new AdminUiActivityItem(
            item.Id.ToString("N"),
            "playlist",
            "playlists",
            "Playlist sync",
            item.State.ToString().ToLowerInvariant(),
            item.ConflictCode ?? $"Generation {item.Generation}",
            item.CompletedAt ?? item.StartedAt,
            Severity: SeverityForState(item.State.ToString()),
            PlaylistLinkId: item.PlaylistLinkId.ToString("N"),
            PlaylistName: playlistNames.GetValueOrDefault(item.PlaylistLinkId, "Playlist"))));
        activity.AddRange(matches.Select(item =>
        {
            externalSnapshots.TryGetValue(item.ExternalSnapshotId, out var snapshot);
            var identity = snapshot?.ProviderTrackIdentityId is { } identityId
                ? providerIdentities.GetValueOrDefault(identityId)
                : null;
            var libraryTrack = libraryTracks.GetValueOrDefault(item.LibraryTrackId ?? Guid.Empty);
            var providerId = identity?.ProviderId ?? snapshot?.ProviderId ?? "matching";
            var sourceTitle = snapshot == null ? null : AuditDetail(snapshot.PayloadJson, "title");
            var sourceArtist = snapshot == null ? null : AuditDetail(snapshot.PayloadJson, "artist");
            var sourceAlbum = snapshot == null ? null : AuditDetail(snapshot.PayloadJson, "album");
            var artworkUrl = snapshot == null ? null
                : AuditDetail(snapshot.PayloadJson, "artworkUrl")
                  ?? AuditDetail(snapshot.PayloadJson, "coverUrl")
                  ?? AuditDetail(snapshot.PayloadJson, "imageUrl");
            return new AdminUiActivityItem(
                item.Id.ToString("N"),
                "matching",
                providerId,
                MatchActivityLabel(item.State),
                item.State.ToString().ToLowerInvariant(),
                MatchActivityDetail(item, snapshot, identity, libraryTrack),
                item.DecidedAt,
                item.CorrelationId,
                SeverityForState(item.State.ToString()),
                ProviderId: providerId,
                ArtworkUrl: artworkUrl,
                SourceTitle: sourceTitle,
                SourceArtist: sourceArtist,
                SourceAlbum: sourceAlbum,
                TargetProviderId: libraryTrack == null ? null : "library",
                TargetTitle: libraryTrack?.Title,
                TargetArtist: libraryTrack?.Artist,
                ConfidenceLabel: $"{Math.Round(item.Confidence * 100, 1)}%",
                Isrc: snapshot == null ? libraryTrack?.Isrc : AuditDetail(snapshot.PayloadJson, "isrc") ?? libraryTrack?.Isrc,
                SourceProviderTrackId: identity?.ExternalId,
                BackendItemId: libraryTrack?.BackendItemId,
                Action: "track-match.evaluate",
                TechnicalDetails: new Dictionary<string, string>
                {
                    ["decisionId"] = item.Id.ToString("N"),
                    ["decisionVersion"] = item.DecisionVersion.ToString(),
                    ["policyVersion"] = item.PolicyVersion
                });
        }));
        activity.AddRange(audits.Select(AuditActivity));
        activity.AddRange(extensionLogs.Select(item => new AdminUiActivityItem(
            item.Id.ToString("N"),
            "extension",
            item.ExtensionId,
            HumanizeAuditCategory(item.EventCode),
            item.Level,
            item.Message,
            item.CreatedAt,
            item.CorrelationId,
            SeverityForState(item.Level),
            ProviderId: item.ExtensionId,
            Action: item.EventCode,
            TechnicalDetails: new Dictionary<string, string>
            {
                ["extensionPackageId"] = item.ExtensionPackageId.ToString("N"),
                ["eventCode"] = item.EventCode
            })));
        activity.AddRange(downloadArtifacts.Select(item => new AdminUiActivityItem(
            item.Id.ToString("N"),
            "caching",
            item.ProviderId,
            item.State == Core.Downloads.ProviderDownloadArtifactState.Placed
                ? "Download placed"
                : "Track cached",
            item.State.ToString().ToLowerInvariant(),
            $"{FormatBytes(item.Length)} verified from {item.ProviderId}",
            item.PlacedAt ?? item.VerifiedAt,
            item.DurableJobId.ToString("N"),
            SeverityForState(item.State.ToString()),
            ProviderId: item.ProviderId,
            SourceProviderTrackId: item.ProviderArtifactId,
            BackendItemId: item.ManagedFileId?.ToString("N"),
            Action: item.State == Core.Downloads.ProviderDownloadArtifactState.Placed
                ? "download-artifact.placed"
                : "download-artifact.verified",
            TechnicalDetails: new Dictionary<string, string>
            {
                ["artifactId"] = item.Id.ToString("N"),
                ["providerArtifactId"] = item.ProviderArtifactId,
                ["durableJobId"] = item.DurableJobId.ToString("N"),
                ["sizeBytes"] = item.Length.ToString(),
                ["sha256"] = item.ContentSha256,
                ["mimeType"] = item.MimeType ?? "unknown",
                ["container"] = item.Container ?? "unknown",
                ["codec"] = item.Codec ?? "unknown",
                ["bitrate"] = item.Bitrate?.ToString() ?? "unknown",
                ["sampleRate"] = item.SampleRate?.ToString() ?? "unknown",
                ["bitDepth"] = item.BitDepth?.ToString() ?? "unknown",
                ["channels"] = item.Channels?.ToString() ?? "unknown"
            })));

        var ordered = activity.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id).Take(limit + 1).ToArray();
        var items = ordered.Take(limit).ToArray();
        return Ok(new
        {
            items,
            hasMore = ordered.Length > limit || jobs.Count == scanLimit || health.Count == scanLimit ||
                      playlistRuns.Count == scanLimit || matches.Count == scanLimit || audits.Count == scanLimit ||
                      extensionLogs.Count == scanLimit || downloadArtifacts.Count == scanLimit,
            nextCursor = items.LastOrDefault()?.OccurredAt,
            nextCursorId = items.LastOrDefault()?.Id
        });
    }

    private static string FormatBytes(long length)
    {
        if (length >= 1024L * 1024L * 1024L) return $"{length / (1024d * 1024d * 1024d):0.##} GiB";
        if (length >= 1024L * 1024L) return $"{length / (1024d * 1024d):0.##} MiB";
        if (length >= 1024L) return $"{length / 1024d:0.##} KiB";
        return $"{length} B";
    }

    private static AdminUiActivityItem AuditActivity(AuditEventRecord item)
    {
        var category = item.Category.Trim().ToLowerInvariant();
        var kind = category switch
        {
            "scrobble" => "scrobble",
            "provider-route" => "streaming",
            "track-identity" or "track-match" => "matching",
            "library-index" => "library",
            _ => "administration"
        };
        var source = AuditDetail(item.DetailsJson, "providerId")
            ?? AuditDetail(item.DetailsJson, "selectedProviderId")
            ?? item.Category;
        var detail = category switch
        {
            "scrobble" => TrackDetail(item.DetailsJson),
            "provider-route" => RouteDetail(item.DetailsJson),
            _ => AuditDetail(item.DetailsJson, "message") ?? HumanizeAuditCategory(item.Category)
        };
        var label = category switch
        {
            "scrobble" => "Scrobble recorded",
            "provider-route" when item.Action == "plan" => "Playback route selected",
            "provider-route" => "Provider request completed",
            _ => HumanizeAuditCategory(item.Action)
        };
        var details = SafeAuditDetails(item.DetailsJson);
        return new AdminUiActivityItem(
            item.Id.ToString("N"), kind, source, label, item.Outcome, detail, item.CreatedAt,
            item.CorrelationId, SeverityForState(item.Outcome), source,
            AuditDetail(item.DetailsJson, "playlistLinkId"),
            AuditDetail(item.DetailsJson, "playlistName"),
            Isrc: Detail(details, "isrc"),
            SourceProviderTrackId: Detail(details, "sourceProviderTrackId", "sourceExternalId", "externalId"),
            TargetProviderTrackId: Detail(details, "targetProviderTrackId", "targetExternalId"),
            BackendItemId: Detail(details, "backendItemId", "targetBackendItemId"),
            RouteDecisionId: Detail(details, "routeDecisionId"),
            ActorUserId: item.ActorUserId?.ToString("N"),
            Action: item.Action,
            TechnicalDetails: details);
    }

    private static IReadOnlyDictionary<string, string> SafeAuditDetails(string json)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (result.Count >= 24) break;
                var key = property.Name.Trim();
                if (key.Length == 0 || IsSensitiveAuditKey(key)) continue;
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                        property.Value.GetRawText(),
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(value)) continue;
                result[key] = value.Length > 500 ? value[..500] : value;
            }
        }
        catch (JsonException)
        {
            // Invalid legacy details remain represented by the readable event text.
        }
        return result;
    }

    private static bool IsSensitiveAuditKey(string key)
    {
        var normalized = key.ToLowerInvariant();
        return normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("cookie", StringComparison.Ordinal) ||
               normalized.Contains("credential", StringComparison.Ordinal) ||
               normalized.Contains("authorization", StringComparison.Ordinal);
    }

    private static string? Detail(IReadOnlyDictionary<string, string> details, params string[] keys)
    {
        foreach (var key in keys)
            if (details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }

    private static string SeverityForState(string? state)
    {
        var normalized = state?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Contains("fail", StringComparison.Ordinal) || normalized.Contains("error", StringComparison.Ordinal) ||
            normalized.Contains("reject", StringComparison.Ordinal) || normalized.Contains("unhealthy", StringComparison.Ordinal)) return "error";
        if (normalized.Contains("warn", StringComparison.Ordinal) || normalized.Contains("degrad", StringComparison.Ordinal) ||
            normalized.Contains("conflict", StringComparison.Ordinal) || normalized.Contains("retry", StringComparison.Ordinal) ||
            normalized.Contains("ambiguous", StringComparison.Ordinal) || normalized.Contains("partial", StringComparison.Ordinal)) return "warning";
        return "info";
    }

    private static string MatchActivityLabel(TrackMatchState state) => state switch
    {
        TrackMatchState.Accepted or TrackMatchState.Pinned => "Track matched",
        TrackMatchState.Suggested => "Track match suggested",
        TrackMatchState.Ambiguous => "Track match needs review",
        TrackMatchState.Rejected => "Track match rejected",
        _ => "Track remains unmatched"
    };

    private static string MatchActivityDetail(
        TrackMatchRecord match,
        ExternalMetadataSnapshotRecord? snapshot,
        ProviderTrackIdentityRecord? identity,
        LibraryTrackRecord? libraryTrack)
    {
        var sourceTitle = snapshot == null ? null : AuditDetail(snapshot.PayloadJson, "title");
        var sourceArtist = snapshot == null ? null : AuditDetail(snapshot.PayloadJson, "artist");
        var source = TrackLabel(sourceTitle, sourceArtist)
            ?? (identity == null ? null : $"{identity.ProviderId}:{identity.ExternalId}")
            ?? "External track";
        var target = libraryTrack == null ? "no local track" : TrackLabel(libraryTrack.Title, libraryTrack.Artist) ?? libraryTrack.BackendItemId;
        var isrc = snapshot == null ? libraryTrack?.Isrc : AuditDetail(snapshot.PayloadJson, "isrc") ?? libraryTrack?.Isrc;
        var parts = new List<string> { $"{source} matched to {target}" };
        if (!string.IsNullOrWhiteSpace(isrc)) parts.Add($"ISRC {isrc}");
        parts.Add($"{Math.Round(match.Confidence * 100, 1)}% confidence");
        return string.Join(" · ", parts);
    }

    private static string? TrackLabel(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        return string.IsNullOrWhiteSpace(artist) ? title.Trim() : $"{artist.Trim()} - {title.Trim()}";
    }

    private static string TrackDetail(string json)
    {
        var title = AuditDetail(json, "Title") ?? "Unknown track";
        var artist = AuditDetail(json, "Artist");
        return string.IsNullOrWhiteSpace(artist) ? title : $"{title} · {artist}";
    }

    private static string RouteDetail(string json)
    {
        var capability = AuditDetail(json, "capability");
        var stage = AuditDetail(json, "Stage");
        var reason = AuditDetail(json, "ReasonCode");
        return string.Join(" · ", new[] { capability, stage, reason }.Where(value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } detail
            ? detail
            : "Provider route evaluated";
    }

    private static string? AuditDetail(string json, string property)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            foreach (var candidate in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase)) continue;
                return candidate.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? candidate.Value.GetString()
                    : candidate.Value.ToString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Older audit records may contain malformed details. Keep the event visible without exposing raw JSON.
        }
        return null;
    }

    private static string HumanizeAuditCategory(string value) =>
        string.Join(' ', value.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private bool IsAdministratorSession() =>
        ControllerContext.HttpContext?.Items.TryGetValue(
            AdminAuthSessionService.HttpContextSessionItemKey,
            out var value) == true &&
        value is AdminAuthSession { IsAdministrator: true };

    private static List<AdminUiRoute> BuildRoutes() =>
    [
        Route("home", "#/home", "Home", "home"),
        Route("library", "#/library", "Library", "library"),
        Route("sources", "#/sources", "Sources", "sources"),
        Route("activity", "#/activity", "Event log", "activity"),
        Route("settings", "#/settings", "Settings", "settings")
    ];

    private static AdminUiRoute Route(string id, string path, string label, string zone) =>
        new() { Id = id, Path = path, Label = label, Zone = zone };

    private static List<AdminUiBackend> BuildBackends() =>
    [
        new()
        {
            Id = "Subsonic",
            Name = "Subsonic",
            Icon = "subsonic",
            ConfigSchema =
            [
                Field("SUBSONIC_URL", "Server URL", "url", "subsonic.url"),
                Field("ENABLE_EXTERNAL_PLAYLISTS", "External playlists", "toggle", "enableExternalPlaylists")
            ]
        },
        new()
        {
            Id = "Jellyfin",
            Name = "Jellyfin",
            Icon = "jellyfin",
            ConfigSchema =
            [
                Field("JELLYFIN_URL", "Server URL", "url", "jellyfin.url"),
                Field("JELLYFIN_API_KEY", "API key", "password", "jellyfin.apiKey", sensitive: true),
                Field("JELLYFIN_USER_ID", "User ID", "text", "jellyfin.userId"),
                Field("JELLYFIN_LIBRARY_ID", "Music library ID", "text", "jellyfin.libraryId")
            ]
        }
    ];

    private List<AdminUiProvider> BuildProviders()
    {
        List<AdminUiProvider> providers =
        [
        new()
        {
            Id = "spotify",
            Name = "Spotify",
            Icon = "spotify",
            Status = ProviderStatus("spotify", _spotifyApiSettings.Enabled
                ? (!string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie) ? "configured" : "needs_config")
                : "disabled"),
            Categories = ["playlist", "lyrics"],
            ConfigSchema =
            [
                Field("SPOTIFY_API_ENABLED", "Enabled", "toggle", "spotifyApi.enabled"),
                Field("SPOTIFY_API_CACHE_DURATION_MINUTES", "Cache minutes", "number", "spotifyApi.cacheDurationMinutes", min: 1),
                Field("SPOTIFY_API_PREFER_ISRC_MATCHING", "Prefer ISRC matching", "toggle", "spotifyApi.preferIsrcMatching")
            ]
        },
        new()
        {
            Id = "apple-download",
            Name = "Apple download",
            Icon = "applemusic",
            Status = ProviderStatus("apple-download", string.IsNullOrWhiteSpace(_appleMusicSettings.BaseUrl) ? "needs_config" : "unknown"),
            Categories = ["metadata", "streaming", "download", "lyrics"],
            ConnectionKind = "operator_managed",
            Audience = "everyone",
            ImplementationOrigin = "built_in",
            RouteId = "builtin:apple-download",
            ConfigSchema =
            [
                Field("APPLE_DOWNLOAD_URL", "External provider URL", "url", "appleDownload.baseUrl"),
                Field("APPLE_DOWNLOAD_QUALITY", "Quality", "select", "appleDownload.quality", ["alac-24-192", "alac-24-96", "alac-24-48", "alac-16-44"])
            ]
        },
        new()
        {
            Id = "apple-musickit",
            Name = "Apple Music",
            Icon = "applemusic",
            Status = "available",
            Categories = ["playlist"],
            Notes = ["Personal playlists", "Music User Token", "Lyrics use a separate provider"],
            AccountSettings =
            [
                new AdminUiConfigField
                {
                    Key = "DeveloperToken",
                    Label = "Apple developer token",
                    Type = "password",
                    Sensitive = true,
                    Required = true,
                    Ownership = "provider-account",
                    HelpText = "The MusicKit developer token issued by your Apple developer integration. It authorizes Apple Music API access but does not replace the per-user token."
                },
                new AdminUiConfigField
                {
                    Key = "MusicUserToken",
                    Label = "Music User Token",
                    Type = "password",
                    Sensitive = true,
                    Required = true,
                    Ownership = "provider-account",
                    HelpText = "The per-user Apple Music authorization token used only to browse and import that user's playlists. Lyrics come from a separate lyrics-capable provider."
                }
            ]
        },
        new()
        {
            Id = "deezer",
            Name = "Deezer",
            Icon = "deezer",
            Status = ProviderStatus("deezer", string.IsNullOrWhiteSpace(_deezerSettings.Arl) ? "needs_config" : "configured"),
            Categories = ["metadata", "download", "streaming", "playlist"],
            ConfigSchema =
            [
                Field("DEEZER_QUALITY", "Quality", "select", "deezer.quality", ["MP3_128", "MP3_320", "FLAC"]),
                Field("DEEZER_MIN_REQUEST_INTERVAL_MS", "Minimum request interval", "number", "deezer.minRequestIntervalMs", min: 0)
            ]
        },
        new()
        {
            Id = "qobuz",
            Name = "Qobuz",
            Icon = "qobuz",
            Status = ProviderStatus("qobuz", string.IsNullOrWhiteSpace(_qobuzSettings.UserAuthToken) ? "needs_config" : "configured"),
            Categories = ["metadata", "download", "streaming", "playlist"],
            ConfigSchema =
            [
                Field("QOBUZ_QUALITY", "Quality", "select", "qobuz.quality", ["MP3_320", "FLAC", "HI_RES"]),
                Field("QOBUZ_MIN_REQUEST_INTERVAL_MS", "Minimum request interval", "number", "qobuz.minRequestIntervalMs", min: 0)
            ]
        },
        new()
        {
            Id = "squidwtf",
            Name = "SquidWTF",
            Icon = "squidwtf",
            Status = ProviderStatus("squidwtf", string.IsNullOrWhiteSpace(_squidWtfSettings.Quality) ? "unknown" : "configured"),
            Categories = ["metadata"],
            ConfigSchema =
            [
                Field("SQUIDWTF_QUALITY", "Quality", "select", "squidWtf.quality", ["LOW", "HIGH", "LOSSLESS"]),
                Field("SQUIDWTF_MIN_REQUEST_INTERVAL_MS", "Minimum request interval", "number", "squidWtf.minRequestIntervalMs", min: 0)
            ]
        },
        new()
        {
            Id = "musicbrainz",
            Name = "MusicBrainz enrichment",
            Icon = "musicbrainz",
            Status = _musicBrainzSettings.Enabled ? "configured" : "disabled",
            Categories = ["enrichment"],
            Notes = ["Genres only", "Optional enrichment"],
            ConfigSchema =
            [
                Field("MUSICBRAINZ_ENABLED", "Enabled", "toggle", "musicBrainz.enabled"),
                Field("MUSICBRAINZ_USERNAME", "Username", "text", "musicBrainz.username"),
                Field("MUSICBRAINZ_PASSWORD", "Password", "password", "musicBrainz.password", sensitive: true)
            ]
        },
        new()
        {
            Id = "lyricsplus",
            Name = "LyricsPlus",
            Icon = "lyrics",
            Status = "available",
            Categories = ["lyrics"]
        },
        new()
        {
            Id = "lrclib",
            Name = "LRCLib",
            Icon = "lyrics",
            Status = "available",
            Categories = ["lyrics"]
        }
        ];

        foreach (var provider in providers)
        {
            provider.ImplementationOrigin ??= "built_in";
            provider.RouteId ??= $"builtin:{provider.Id}";
            provider.CapabilityRoutes.Add(new AdminUiProviderCapabilityRoute
            {
                RouteId = provider.RouteId,
                Name = provider.Name,
                Origin = provider.ImplementationOrigin,
                Capabilities = provider.Categories.ToList()
            });
        }

        var runtimeStatuses = _providerStatusManager.GetAllStatuses();
        if (_providerRegistry != null)
        {
            foreach (var item in _providerRegistry.Providers
                         .Where(item => item.Origin == ProviderOrigin.Extension))
            {
                var categories = item.Capabilities
                    .Where(capability => capability.HasUsableImplementation)
                    .Select(capability => capability.Capability.ToString().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var accountSettings = item.Settings.Select(setting => new AdminUiConfigField
                {
                    Key = setting.Key,
                    Label = setting.Label,
                    Type = setting.ValueKind switch
                    {
                        ProviderSettingValueKind.Secret => "password",
                        ProviderSettingValueKind.Boolean => "toggle",
                        ProviderSettingValueKind.Integer => "number",
                        ProviderSettingValueKind.Choice => "select",
                        _ => "text"
                    },
                    Sensitive = setting.ValueKind == ProviderSettingValueKind.Secret,
                    Required = setting.Required,
                    Options = setting.Choices.ToList(),
                    HelpText = setting.HelpText,
                    DefaultValueJson = setting.DefaultJson,
                    Ownership = "provider-account"
                }).ToList();
                var route = new AdminUiProviderCapabilityRoute
                {
                    RouteId = $"extension:{item.Id}",
                    Name = $"{item.DisplayName} · Extension SDK {item.SdkVersion}",
                    Origin = "extension",
                    Capabilities = categories
                };
                var existing = providers.SingleOrDefault(provider =>
                    provider.Id.Equals(item.Id, StringComparison.Ordinal));
                if (existing != null)
                {
                    existing.Categories = existing.Categories
                        .Concat(categories)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    existing.AccountSettings = existing.AccountSettings
                        .Concat(accountSettings)
                        .GroupBy(setting => setting.Key, StringComparer.Ordinal)
                        .Select(group => group.First())
                        .ToList();
                    existing.CapabilityRoutes.Add(route);
                    continue;
                }

                providers.Add(new AdminUiProvider
                {
                    Id = item.Id,
                    Name = item.DisplayName,
                    Icon = "extension",
                    LogoUrl = item.Branding == null ? null : $"/api/admin/extensions/providers/{Uri.EscapeDataString(item.Id)}/icon",
                    Status = "unknown",
                    Categories = categories,
                    Notes = [$"Extension SDK {item.SdkVersion}"],
                    ConnectionKind = "extension",
                    ImplementationOrigin = "extension",
                    RouteId = route.RouteId,
                    AccountSettings = accountSettings,
                    CapabilityRoutes = [route]
                });
            }
        }
        foreach (var provider in providers)
        {
            var statuses = runtimeStatuses
                .Where(status => status.Provider.Equals(provider.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (statuses.Count == 0)
            {
                continue;
            }

            provider.RuntimeCapabilities = statuses.Select(ToAdminRuntimeCapability).ToList();
            provider.Status = AggregateProviderStatus(statuses);
        }

        return providers;
    }

    private AdminUiProviderRuntimeCapability ToAdminRuntimeCapability(ProviderRuntimeStatus status) =>
        new()
        {
            Id = status.Capability,
            Supported = status.IsSupported,
            Configuration = !status.IsSupported ? "unsupported" : status.Configuration switch
            {
                ProviderConfigurationState.NotRequired => "not_required",
                ProviderConfigurationState.Configured => "configured",
                _ => "needs_configuration"
            },
            Health = status.Health.ToString().ToLowerInvariant(),
            Ready = status.IsReady,
            CanAttempt = status.CanAttempt,
            CanTest = _providerStatusManager.CanTestCapability(status.Provider, status.Capability),
            TestedAt = status.TestedAt,
            ReasonCode = status.ReasonCode
        };

    private static string AggregateProviderStatus(IReadOnlyList<ProviderRuntimeStatus> statuses)
    {
        if (statuses.All(status => !status.IsSupported))
        {
            return "unsupported";
        }

        if (statuses.All(status => !status.IsEnabled))
        {
            return "disabled";
        }

        if (statuses.Any(status => status.Health == Services.Common.ProviderHealthState.Testing))
        {
            return "testing";
        }

        if (statuses.Any(status => status.Health == Services.Common.ProviderHealthState.Degraded))
        {
            return "degraded";
        }

        var enabledSupported = statuses.Where(status => status.IsEnabled && status.IsSupported).ToList();
        if (enabledSupported.Count > 0 && enabledSupported.All(status =>
                status.Configuration == ProviderConfigurationState.NeedsConfiguration || status.IsReady) &&
            enabledSupported.Any(status => status.IsReady))
        {
            return "healthy";
        }

        if (statuses.All(status => !status.CanAttempt))
        {
            return "needs_config";
        }

        if (statuses.Any(status => status.Configuration == ProviderConfigurationState.NeedsConfiguration))
        {
            return "partial_config";
        }

        return "available";
    }

    private List<AdminUiPriorityGroup> BuildPriorityGroups()
    {
        var activeBackend = _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin";
        var pinnedLocalProvider = BuildPinnedLocalProvider(activeBackend);
        return
        [
            Priority(
                "metadata",
                "Metadata search order",
                "Used only for discovery (titles, artists, albums, ISRCs). Playback uses Streaming and Download order below.",
                "MULTI_PROVIDER_METADATA_ORDER",
                "MULTI_PROVIDER_ENABLED_SEARCH",
                "apple-download,deezer,qobuz",
                pinnedProvider: null),
            Priority(
                "download",
                "Download priority",
                "Download routes after the local library. Drag to change which source fills a missing track.",
                "MULTI_PROVIDER_DOWNLOAD_ORDER",
                null,
                "apple-download,deezer,qobuz",
                pinnedProvider: pinnedLocalProvider),
            Priority(
                "streaming",
                "Streaming priority",
                "Stream routes after the local library. Drag to change which source plays a missing track.",
                "MULTI_PROVIDER_STREAMING_ORDER",
                null,
                "apple-download,deezer,qobuz",
                pinnedProvider: pinnedLocalProvider),
            Priority(
                "playlist",
                "Playlist discovery priority",
                "Order used when fetching playlists and playlist tracks from each source.",
                "MULTI_PROVIDER_PLAYLIST_ORDER",
                "MULTI_PROVIDER_ENABLED_PLAYLIST",
                "spotify,deezer,qobuz",
                pinnedProvider: null),
            Priority(
                "lyrics",
                "Lyrics priority",
                "Order used for lyrics lookup when a song is played or requested.",
                "MULTI_PROVIDER_LYRICS_ORDER",
                null,
                "spotify,apple-download,lyricsplus,lrclib",
                pinnedProvider: pinnedLocalProvider)
        ];
    }

    private AdminUiPinnedProvider? BuildPinnedLocalProvider(string activeBackend)
    {
        if (string.Equals(activeBackend, "Subsonic", StringComparison.OrdinalIgnoreCase))
        {
            return new AdminUiPinnedProvider
            {
                Id = "subsonic-local",
                Name = "Subsonic",
                Icon = "subsonic",
                Reason = "Local library is always checked first. This entry is fixed."
            };
        }

        return new AdminUiPinnedProvider
        {
            Id = "jellyfin-local",
            Name = "Jellyfin",
            Icon = "jellyfin",
            Reason = "Local library is always checked first. This entry is fixed."
        };
    }

    private AdminUiPriorityGroup Priority(
        string id,
        string label,
        string description,
        string envKey,
        string? enabledEnvKey,
        string fallback,
        AdminUiPinnedProvider? pinnedProvider)
    {
        var value = _configuration[envKey] ?? fallback;
        var providers = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .Where(p => id == "metadata" || p != "squidwtf")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_providerRegistry != null && Enum.TryParse<ProviderCapabilityKind>(id, true, out var capability))
        {
            providers.AddRange(_providerRegistry.FindByCapability(capability, includeNonOperational: true)
                .Select(provider => provider.Id)
                .Where(providerId => !providers.Contains(providerId, StringComparer.OrdinalIgnoreCase))
                .OrderBy(providerId => providerId, StringComparer.Ordinal));
        }
        return new AdminUiPriorityGroup
        {
            Id = id,
            Label = label,
            Description = description,
            EnvKey = envKey,
            EnabledEnvKey = enabledEnvKey,
            Providers = providers,
            PinnedProvider = pinnedProvider
        };
    }

    private string ProviderStatus(string id, string configuredStatus)
    {
        var disabled = (_configuration["MULTI_PROVIDER_DISABLED_PROVIDERS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => p.Equals(id, StringComparison.OrdinalIgnoreCase));
        return disabled ? "disabled" : configuredStatus;
    }

    private static List<AdminUiConfigSection> BuildConfigSections() =>
    [
        Section("general", "General",
        [
            DeploymentField("BACKEND_TYPE", "Backend", "select", "backendType", ["Jellyfin", "Subsonic"]),
            Field("STORAGE_MODE", "Storage mode", "select", "library.storageMode", ["Permanent", "Cache"]),
            Field("DOWNLOAD_MODE", "Download mode", "select", "library.downloadMode", ["Track", "Album"]),
            Field("EXPLICIT_FILTER", "Explicit filter", "select", "explicitFilter", ["All", "ExplicitOnly", "CleanOnly"])
        ]),
        Section("paths", "Library paths",
        [
            DeploymentField("LIBRARY_DOWNLOAD_PATH", "Download path", "text", "library.downloadPath"),
            DeploymentField("LIBRARY_KEPT_PATH", "Kept downloads path", "text", "library.keptPath"),
            Field("PLAYLISTS_DIRECTORY", "Playlists directory", "text", "playlistsDirectory")
        ]),
        Section("cache", "Cache",
        [
            Field("CACHE_DURATION_HOURS", "Track cache hours", "number", "library.cacheDurationHours", min: 1),
            Field("CACHE_SEARCH_RESULTS_MINUTES", "Search results minutes", "number", "cache.searchResultsMinutes", min: 1),
            Field("CACHE_LYRICS_DAYS", "Lyrics days", "number", "cache.lyricsDays", min: 1),
            Field("CACHE_METADATA_DAYS", "Metadata days", "number", "cache.metadataDays", min: 1),
            Field("CACHE_PROXY_IMAGES_DAYS", "Proxy images days", "number", "cache.proxyImagesDays", min: 1),
            Field("CACHE_TRANSCODE_MINUTES", "Transcode cache minutes", "number", "cache.transcodeCacheMinutes", min: 1),
            DeploymentField("CACHE_MEDIA_DIRECTORY", "Media cache directory", "text", "cache.mediaDirectory"),
            DeploymentField("CACHE_MEDIA_MAXIMUM_MEGABYTES", "Media cache total MiB", "number", "cache.mediaMaximumMegabytes"),
            DeploymentField("CACHE_MEDIA_MAXIMUM_ENTRY_MEGABYTES", "Media cache entry MiB", "number", "cache.mediaMaximumEntryMegabytes"),
            DeploymentField("CACHE_MEDIA_CLEANUP_FILE_LIMIT", "Cleanup scan file limit", "number", "cache.mediaCleanupFileLimit")
        ]),
        Section("network", "Network and security",
        [
            DeploymentField("ADMIN_BIND_ANY_IP", "Bind admin on all interfaces", "toggle", "admin.bindAnyIp"),
            DeploymentField("ADMIN_TRUSTED_SUBNETS", "Trusted admin subnets", "text", "admin.trustedSubnets"),
            DeploymentField("DEBUG_LOG_ALL_REQUESTS", "Request usage logging", "toggle", "debug.logAllRequests")
        ]),
        Section("spotify-import", "Imported playlist matching",
        [
            Field("SPOTIFY_IMPORT_ENABLED", "Enabled", "toggle", "spotifyImport.enabled"),
            Field("SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS", "Matching interval hours", "number", "spotifyImport.matchingIntervalHours", min: 0)
        ])
    ];

    private static AdminUiConfigSection Section(string id, string label, List<AdminUiConfigField> fields) =>
        new() { Id = id, Label = label, Fields = fields };

    private static AdminUiConfigField Field(
        string key,
        string label,
        string type,
        string? valuePath,
        List<string>? options = null,
        string? placeholder = null,
        bool sensitive = false,
        bool requiresRestart = false,
        string ownership = "durable",
        bool readOnly = false,
        string? helpText = null,
        int? min = null,
        int? max = null) =>
        new()
        {
            Key = key,
            Label = label,
            Type = type,
            ValuePath = valuePath,
            Options = options ?? [],
            Placeholder = placeholder,
            Sensitive = sensitive,
            RequiresRestart = requiresRestart,
            Ownership = ownership,
            ReadOnly = readOnly,
            HelpText = helpText,
            Min = min,
            Max = max
        };

    private static AdminUiConfigField DeploymentField(
        string key,
        string label,
        string type,
        string? valuePath,
        List<string>? options = null) =>
        Field(
            key,
            label,
            type,
            valuePath,
            options,
            ownership: "deployment",
            readOnly: true,
            helpText: "Edit in Compose/.env and recreate the container to apply this deployment-owned value.");
}

public sealed record AdminUiActivityItem(
    string Id,
    string Kind,
    string Source,
    string Label,
    string State,
    string Detail,
    DateTimeOffset OccurredAt,
    string? CorrelationId = null,
    string Severity = "info",
    string? ProviderId = null,
    string? PlaylistLinkId = null,
    string? PlaylistName = null,
    string? ArtworkUrl = null,
    string? SourceTitle = null,
    string? SourceArtist = null,
    string? SourceAlbum = null,
    string? TargetProviderId = null,
    string? TargetTitle = null,
    string? TargetArtist = null,
    string? ConfidenceLabel = null,
    string? Isrc = null,
    string? SourceProviderTrackId = null,
    string? TargetProviderTrackId = null,
    string? BackendItemId = null,
    string? RouteDecisionId = null,
    string? ActorUserId = null,
    string? Action = null,
    IReadOnlyDictionary<string, string>? TechnicalDetails = null);
