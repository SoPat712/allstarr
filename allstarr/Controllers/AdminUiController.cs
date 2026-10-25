using allstarr.Core.Health;
using allstarr.Core.Configuration;
using allstarr.Core.Extensions;
using System.Text.Json;
using allstarr.Filters;
using allstarr.Core.Identity;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Playback;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Models.Admin;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Admin;
using allstarr.Services.Spotify;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
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
    private readonly AppleDownloadSettings _appleMusicSettings;
    private readonly MusicBrainzSettings _musicBrainzSettings;
    private readonly ExtensionManager _extensionManager;
    private readonly ProviderStatusManager _providerStatusManager;
    private readonly ProviderAccountOptions _providerAccountOptions;
    private readonly IProviderRegistry? _providerRegistry;
    private readonly IProtocolProviderGateway? _providerGateway;
    private readonly IEffectiveProviderPolicyResolver? _effectivePolicies;
    private readonly ITrackMatchRepository _trackMatches;

    public AdminUiController(
        IConfiguration configuration,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings,
        IOptions<AppleDownloadSettings> appleMusicSettings,
        IOptions<MusicBrainzSettings> musicBrainzSettings,
        ExtensionManager extensionManager,
        ProviderStatusManager providerStatusManager,
        ProviderAccountOptions providerAccountManagementOptions,
        ITrackMatchRepository trackMatches,
        IProviderRegistry? providerRegistry = null,
        IProtocolProviderGateway? providerGateway = null,
        IEffectiveProviderPolicyResolver? effectivePolicies = null)
    {
        _configuration = configuration;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _appleMusicSettings = appleMusicSettings.Value;
        _musicBrainzSettings = musicBrainzSettings.Value;
        _extensionManager = extensionManager;
        _providerStatusManager = providerStatusManager;
        _providerAccountOptions = providerAccountManagementOptions;
        _trackMatches = trackMatches;
        _providerRegistry = providerRegistry;
        _providerGateway = providerGateway;
        _effectivePolicies = effectivePolicies;
    }

    [HttpGet("schema")]
    public async Task<IActionResult> GetSchema(CancellationToken cancellationToken = default)
    {
        var activeBackend = _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin";
        var policy = await TryEffectivePolicyAsync(cancellationToken);
        var disabledProviders = policy?.DisabledProviders;
        var originNames = await ExtensionOriginNamesAsync(cancellationToken);
        if (!IsAdministratorSession())
        {
            return Ok(new AdminUiSchemaResponse
            {
                ActiveBackend = activeBackend,
                ListenersCanConnectOwnAccounts = _providerAccountOptions.ListenersCanConnectOwnAccounts,
                Providers = BuildProviders(disabledProviders, originNames, policy).Select(item => new AdminUiProvider
                {
                    Id = item.Id,
                    Name = item.Name,
                    Description = item.Description,
                    Icon = item.Icon,
                    LogoUrl = item.LogoUrl,
                    Status = item.Status,
                    UseMode = item.UseMode,
                    Categories = item.Categories,
                    Notes = item.Notes,
                    AccountSettings = item.AccountSettings,
                    ConnectionKind = item.ConnectionKind,
                    UsesSignedSession = item.UsesSignedSession,
                    OriginName = item.OriginName,
                    Audience = item.Audience,
                    SupportedAudiences = item.SupportedAudiences,
                    SharingDescription = item.SharingDescription,
                    ImplementationOrigin = item.ImplementationOrigin,
                    RouteId = item.RouteId,
                    CapabilityRoutes = item.CapabilityRoutes
                }).ToList(),
                Routes =
                [
                    Route("home", "#/", "Home", "home"),
                    Route("playlists", "#/library/playlists", "Playlists", "library"),
                    Route("mappings", "#/library/mappings", "Mappings", "library"),
                    Route("sources", "#/integrations/services", "Integrations", "sources"),
                    Route("activity", "#/activity", "Activity", "activity"),
                    Route("preferences", "#/preferences", "Listening preferences", "system")
                ]
            });
        }

        var schema = new AdminUiSchemaResponse
        {
            ActiveBackend = activeBackend,
            ListenersCanConnectOwnAccounts = _providerAccountOptions.ListenersCanConnectOwnAccounts,
            Routes = BuildRoutes(),
            Backends = BuildBackends(),
            Providers = BuildProviders(disabledProviders, originNames, policy),
            ProviderSupportMatrix = CurrentProviderSupportCatalog.All.ToList(),
            MultiProviderCategories = ["metadata", "streaming", "download", "playlist", "lyrics", "enrichment"],
            PriorityGroups = BuildPriorityGroups(policy),
            ConfigSections = BuildConfigSections(),
            ExtensionStore = new AdminUiExtensionStore
            {
                Repositories = [],
                RegistryEnvKey = "",
                StoreEndpoint = "/api/admin/extensions/store",
                InstalledEndpoint = "/api/admin/extensions/packages"
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

    private async Task<EffectiveProviderPolicySnapshot?> TryEffectivePolicyAsync(
        CancellationToken cancellationToken)
    {
        if (_effectivePolicies == null ||
            !HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession { AllstarrUserId: { } userId })
            return null;
        try
        {
            return await _effectivePolicies.ResolveForUserAsync(userId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    [HttpGet("provider-summaries")]
    public async Task<IActionResult> GetProviderSummaries(CancellationToken cancellationToken = default)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionValue) ||
            sessionValue is not AdminAuthSession { IsAdministrator: true, AllstarrUserId: not null })
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Administrator permissions required" });
        }

        var contextFactory = HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.ProviderAccounts.AsNoTracking()
            .ToListAsync(cancellationToken);
        accounts = accounts.Where(item => !ProviderAccountSettings.IsRemoved(item)).ToList();
        var publicStatuses = _providerStatusManager.GetAllAccountFreeStatuses()
            .Concat(_providerStatusManager.GetAppleDownloadEndpointStatuses()).ToList();
        var providerIds = accounts.Select(item => item.ProviderId).Concat(publicStatuses.Select(item => item.Provider))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var summaries = providerIds.Select(providerId =>
            {
                var group = accounts.Where(item => item.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)).ToList();
                var enabled = group.Where(item => item.Enabled).ToList();
                var statuses = enabled.Count > 0
                    ? enabled.SelectMany(account => _providerStatusManager.GetAllManagedStatuses(
                        account.ProviderId, account.Id, new Dictionary<string, string>(), account.Revision)).ToList()
                    : group.Count > 0 ? [] : publicStatuses.Where(item => item.Provider == providerId).ToList();
                var samples = statuses.Where(item => item.HasCheckResult &&
                    _providerStatusManager.CanTestCapability(item.Provider, item.Capability)).ToList();
                return new
                {
                    providerId,
                    connectedAccountName = enabled
                        .Select(item => ProviderAccountNames.DisplayName(item, _providerRegistry)).FirstOrDefault(),
                    connectedAccounts = enabled.OrderBy(item => item.Id).Select(item => new
                    {
                        scope = item.Scope.ToString(),
                        customLabel = ProviderAccountNames.CustomLabel(item, _providerRegistry)
                    }).ToArray(),
                    enabledAccountCount = enabled.Count,
                    capabilityTotal = samples.Count,
                    healthyCapabilityCount = samples.Count(item => item.Health == Services.Common.ProviderHealthState.Healthy),
                    failedCapabilityCount = samples.Count(item => item.Health == Services.Common.ProviderHealthState.Degraded),
                    lastCheckedAt = samples.Max(item => item.TestedAt),
                    successRate = (double?)null,
                    p95LatencyMilliseconds = (long?)null,
                    lastFailureCode = samples.Where(item => item.Health == Services.Common.ProviderHealthState.Degraded)
                        .OrderByDescending(item => item.TestedAt).Select(item => item.ReasonCode).FirstOrDefault()
                };
            })
            .OrderBy(item => item.providerId)
            .ToList();

        return Ok(new { providers = summaries });
    }

    [HttpGet("home")]
    public async Task<IActionResult> GetHome(CancellationToken cancellationToken = default)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession { AllstarrUserId: not null } session ||
            !session.IsAdministrator && session.AllstarrUserId is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "A linked Allstarr user is required" });
        }

        var schema = (await GetSchema(cancellationToken) as OkObjectResult)?.Value as AdminUiSchemaResponse;
        if (schema == null) return StatusCode(StatusCodes.Status500InternalServerError);
        var services = HttpContext.RequestServices;
        var contextFactory = services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var linksQuery = context.PlaylistLinks.AsNoTracking();
        var jobsQuery = context.Jobs.AsNoTracking();
        var listensQuery = context.ListeningEvents.AsNoTracking();
        if (!session.IsAdministrator && session.AllstarrUserId is { } ownerUserId)
        {
            linksQuery = linksQuery.Where(item => item.OwnerUserId == ownerUserId);
            jobsQuery = jobsQuery.Where(item => item.OwnerUserId == ownerUserId);
            listensQuery = listensQuery.Where(item => item.OwnerUserId == ownerUserId);
        }

        var linkIds = await linksQuery.Select(item => item.Id).ToArrayAsync(cancellationToken);
        var projections = await services.GetRequiredService<DurablePlaylistProjectionReader>()
            .ReadByLinkIdsAsync(session.IsAdministrator ? null : session.AllstarrUserId,
                linkIds, cancellationToken, session.AllstarrUserId);
        var activeJobs = await jobsQuery.CountAsync(item =>
            item.State != DurableJobState.Succeeded && item.State != DurableJobState.Failed &&
            item.State != DurableJobState.Cancelled, cancellationToken);
        var lastDay = DateTimeOffset.UtcNow.AddHours(-24);
        var currentWeekStart = DateTimeOffset.UtcNow.AddDays(-7);
        var previousWeekStart = DateTimeOffset.UtcNow.AddDays(-14);
        var lastMonth = DateTimeOffset.UtcNow.AddDays(-30);
        var completedListens = await listensQuery.CountAsync(item =>
            item.State == ListeningEventState.Completed && item.ListenedAt >= lastDay, cancellationToken);
        var topArtist = await listensQuery
            .Where(item => item.State == ListeningEventState.Completed && item.ListenedAt >= lastMonth && item.Artist != null)
            .GroupBy(item => item.Artist)
            .Select(group => new { name = group.Key, listens = group.Count() })
            .OrderByDescending(item => item.listens)
            .ThenBy(item => item.name)
            .FirstOrDefaultAsync(cancellationToken);
        var scrobbleDeliveries = await context.PlaybackDeliveryCheckpoints.AsNoTracking().CountAsync(item =>
                (session.IsAdministrator || item.OwnerUserId == session.AllstarrUserId) &&
                item.Kind == PlaybackScrobbleDeliveryKind.Completed &&
                item.UpdatedAt >= lastDay &&
                (item.State == ScopedPlaybackScrobbleOutcome.Delivered ||
                 item.State == ScopedPlaybackScrobbleOutcome.Ignored &&
                 item.ProviderCode != "listen-incomplete" && item.ProviderCode != "listen-not-completed"), cancellationToken);
        var currentWeekListens = await listensQuery.CountAsync(item =>
            item.State == ListeningEventState.Completed && item.ListenedAt >= currentWeekStart,
            cancellationToken);
        var previousWeekListens = await listensQuery.CountAsync(item =>
            item.State == ListeningEventState.Completed && item.ListenedAt >= previousWeekStart &&
            item.ListenedAt < currentWeekStart, cancellationToken);
        int? cacheTracks = null;
        int? keptTracks = null;
        if (session.IsAdministrator)
        {
            var downloadRoot = Path.GetFullPath(_configuration["Library:DownloadPath"] ?? "./downloads");
            var paths = await context.DownloadedSongMappings.AsNoTracking()
                .Select(item => item.LocalPath)
                .ToArrayAsync(cancellationToken);
            (cacheTracks, keptTracks) = CountExistingStorageMappings(paths, downloadRoot);
        }
        var storage = services.GetService<DurableStorageState>()?.GetSnapshot();
        var providerHealth = session.IsAdministrator
            ? (await GetProviderSummaries(cancellationToken) as OkObjectResult)?.Value
            : new { providers = Array.Empty<object>() };
        var activity = (await GetDashboardActivity(8, cancellationToken: cancellationToken) as OkObjectResult)?.Value;

        return Ok(new
        {
            schema,
            status = new
            {
                version = AppVersion.Version,
                backendType = schema.ActiveBackend,
                durableStorage = storage == null ? null : new
                {
                    provider = storage.Provider.ToString(),
                    readiness = storage.Readiness.ToString(),
                    storage.ErrorCode,
                    storage.CheckedAt
                }
            },
            stats = new
            {
                linkedPlaylists = linkIds.Length,
                playableTracks = projections.Values.Sum(item => item.PlayableCount),
                unresolvedTracks = projections.Values.Sum(item => item.MissingCount),
                activeJobs,
                completedListens,
                currentWeekListens,
                previousWeekListens,
                scrobbleDeliveries,
                cacheTracks,
                keptTracks,
                topArtist
            },
            providerHealth,
            activity
        });
    }

    private static bool IsStoredUnder(string path, string root)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                   !Path.IsPathRooted(relative);
        }
        catch
        {
            return false;
        }
    }

    internal static (int CacheTracks, int KeptTracks) CountExistingStorageMappings(
        IEnumerable<string> paths,
        string downloadRoot)
    {
        var cacheTracks = 0;
        var keptTracks = 0;
        foreach (var path in paths)
        {
            if (!System.IO.File.Exists(path)) continue;
            if (IsStoredUnder(path, Path.Combine(downloadRoot, "cache")) ||
                IsStoredUnder(path, Path.Combine(downloadRoot, "transcoded")))
            {
                cacheTracks++;
            }
            else if (IsStoredUnder(path, Path.Combine(downloadRoot, "permanent")) ||
                     IsStoredUnder(path, Path.Combine(downloadRoot, "kept")))
            {
                keptTracks++;
            }
        }
        return (cacheTracks, keptTracks);
    }

    [HttpGet("activity")]
    public async Task<IActionResult> GetDashboardActivity(
        [FromQuery] int limit = 20,
        [FromQuery] DateTimeOffset? before = null,
        [FromQuery] Guid? beforeId = null,
        CancellationToken cancellationToken = default,
        [FromQuery] string scope = "mine")
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionValue) ||
            sessionValue is not AdminAuthSession { AllstarrUserId: { } userId } session)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "A linked Allstarr user is required" });
        }

        if (scope is not ("mine" or "everyone")) return BadRequest(new { error = "Unknown activity scope" });
        if (scope == "everyone" && !session.IsAdministrator) return Forbid();
        var everyone = session.IsAdministrator && scope == "everyone";

        limit = Math.Clamp(limit, 1, 100);
        var scanLimit = Math.Min(500, limit * 5);
        var contextFactory = HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.ProviderAccounts.AsNoTracking()
            .Where(item => session.IsAdministrator || item.OwnerUserId == userId || item.OwnerUserId == null)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var jobs = await context.Jobs.AsNoTracking()
            .Where(item => (everyone || item.OwnerUserId == userId) &&
                (!before.HasValue || item.UpdatedAt < before.Value ||
                (item.UpdatedAt == before.Value && beforeId.HasValue && item.Id.CompareTo(beforeId.Value) < 0)))
            .OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id)
            .Take(scanLimit)
            .ToListAsync(cancellationToken);
        var playlistRuns = await context.PlaylistSyncRuns.AsNoTracking()
            .Where(item => (everyone || item.OwnerUserId == userId) &&
                (!before.HasValue || (item.CompletedAt ?? item.StartedAt) < before.Value ||
                ((item.CompletedAt ?? item.StartedAt) == before.Value && beforeId.HasValue && item.Id.CompareTo(beforeId.Value) < 0)))
            .OrderByDescending(item => item.CompletedAt ?? item.StartedAt).ThenByDescending(item => item.Id)
            .Take(scanLimit)
            .ToListAsync(cancellationToken);
        var playlistLinkIds = playlistRuns.Select(item => item.PlaylistLinkId).Distinct().ToArray();
        var playlistLinks = playlistLinkIds.Length == 0
            ? new Dictionary<Guid, PlaylistLinkRecord>()
            : await context.PlaylistLinks.AsNoTracking()
                .Where(item => playlistLinkIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
        var playlistSnapshots = playlistLinkIds.Length == 0
            ? new Dictionary<Guid, PlaylistSourceSnapshotRecord>()
            : (await context.PlaylistSourceSnapshots.AsNoTracking()
                .Where(item =>
                    playlistLinkIds.Contains(item.PlaylistLinkId) &&
                    item.PublishedAt.HasValue)
                .GroupBy(item => item.PlaylistLinkId)
                .Select(group => group.OrderByDescending(item => item.SnapshotVersion)
                    .ThenByDescending(item => item.RetrievedAt).First())
                .ToListAsync(cancellationToken))
                .ToDictionary(item => item.PlaylistLinkId);
        var matchActivity = await _trackMatches.GetActivityDataAsync(
            new TrackMatchActor(userId, everyone),
            before,
            beforeId,
            scanLimit,
            cancellationToken);
        var matches = matchActivity.Decisions;
        var externalSnapshots = matchActivity.Snapshots.ToDictionary(item => item.Id);
        var providerIdentities = matchActivity.ProviderIdentities;
        var providerIdentityById = providerIdentities.ToDictionary(item => item.Id);
        var providerIdentitiesByHash = providerIdentities
            .GroupBy(item => item.ExternalIdHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var providerIdentitiesByCanonical = providerIdentities
            .GroupBy(item => item.CanonicalRecordingId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var libraryTracks = matchActivity.LibraryTracks;
        var audits = await context.AuditEvents.AsNoTracking()
            .Where(item => (everyone || item.ActorUserId == userId) &&
                (!before.HasValue || item.CreatedAt < before.Value ||
                (item.CreatedAt == before.Value && beforeId.HasValue && item.Id.CompareTo(beforeId.Value) < 0)))
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Take(scanLimit)
            .ToListAsync(cancellationToken);
        var extensionLogs = await context.ExtensionLogs.AsNoTracking()
            .Where(item => everyone && (!before.HasValue || item.CreatedAt < before.Value ||
                (item.CreatedAt == before.Value && beforeId.HasValue && item.Id.CompareTo(beforeId.Value) < 0)))
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Take(scanLimit)
            .ToListAsync(cancellationToken);
        var downloadArtifacts = await context.ProviderDownloadArtifacts.AsNoTracking()
            .Where(item => (everyone || item.OwnerUserId == userId) &&
                (!before.HasValue || item.VerifiedAt < before.Value ||
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
                ? providerAccount.ProviderId : null,
            ActorUserId: item.OwnerUserId?.ToString("N"))));
        activity.AddRange(playlistRuns.Select(item =>
        {
            playlistLinks.TryGetValue(item.PlaylistLinkId, out var link);
            playlistSnapshots.TryGetValue(item.PlaylistLinkId, out var snapshot);
            return new AdminUiActivityItem(
                item.Id.ToString("N"),
                "playlist",
                "playlists",
                "Playlist sync",
                item.State.ToString().ToLowerInvariant(),
                item.ConflictCode ?? $"Generation {item.Generation}",
                item.CompletedAt ?? item.StartedAt,
                Severity: SeverityForState(item.State.ToString()),
                ActorUserId: item.OwnerUserId.ToString("N"),
                PlaylistLinkId: item.PlaylistLinkId.ToString("N"),
                PlaylistName: snapshot?.Name ?? "Playlist",
                ArtworkUrl: snapshot?.ArtworkReferenceKey == null || link == null
                    ? null
                    : PlaylistArtworkUrl(link));
        }));
        var effectivePolicy = _effectivePolicies == null
            ? null
            : await _effectivePolicies.ResolveAsync(cancellationToken);
        var routeProviderPriority = _providerGateway == null
            ? null
            : ProviderOrder(ProviderCapabilityKind.Streaming)
                .Concat(ProviderOrder(ProviderCapabilityKind.Download))
                .Select(ExternalTrackPlaybackPolicy.Normalize)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        IReadOnlyList<string> ProviderOrder(ProviderCapabilityKind capability)
        {
            var available = _providerGateway.GetProviderOrder(capability);
            return effectivePolicy?.ApplyProviderAvailability(capability, available) ?? available;
        }
        activity.AddRange(matches.Select(item =>
        {
            externalSnapshots.TryGetValue(item.ExternalSnapshotId, out var snapshot);
            var identity = snapshot?.ProviderTrackIdentityId is { } identityId
                ? providerIdentityById.GetValueOrDefault(identityId)
                : null;
            identity ??= snapshot == null
                ? null
                : providerIdentitiesByHash.GetValueOrDefault(snapshot.ExternalIdHash)?
                    .FirstOrDefault(candidate => MatchesActivitySourceIdentity(snapshot, candidate));
            var routeCanonicalId = item.CanonicalRecordingId ?? identity?.CanonicalRecordingId;
            var routeIdentities = routeCanonicalId.HasValue &&
                                  providerIdentitiesByCanonical.TryGetValue(
                                      routeCanonicalId.Value,
                                      out var canonicalIdentities)
                ? canonicalIdentities
                : [];
            return MatchActivityItem(
                item,
                snapshot,
                identity,
                routeIdentities,
                libraryTracks,
                routeProviderPriority);
        }));
        activity.AddRange(audits.Select(AuditActivity));
        activity.AddRange(extensionLogs.Select(item => new AdminUiActivityItem(
            item.Id.ToString("N"),
            "extension",
            item.ExtensionId,
            HumanizeAuditCategory(item.EventCode),
            item.Level,
            ExtensionControlPlaneService.SanitizeExtensionDiagnostic(item.Message),
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
            $"{FormatBytes(item.Length)} verified",
            item.PlacedAt ?? item.VerifiedAt,
            item.DurableJobId.ToString("N"),
            SeverityForState(item.State.ToString()),
            ProviderId: item.ProviderId,
            ActorUserId: item.OwnerUserId?.ToString("N"),
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
        var actorIds = ordered.Select(item => Guid.TryParse(item.ActorUserId, out var id) ? id : (Guid?)null)
            .OfType<Guid>().Distinct().ToArray();
        var actors = await context.Users.AsNoTracking().Where(item => actorIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var items = ordered.Take(limit).Select(item =>
        {
            var actor = Guid.TryParse(item.ActorUserId, out var actorId) ? actors.GetValueOrDefault(actorId) : null;
            var library = item.Label == "library.index" || item.Kind == "library";
            return item with
            {
                ActorDisplayName = item.ActorUserId == null ? "Household" : ActivityPresentation.UserName(actor?.DisplayName),
                Source = library ? ActivityPresentation.LibraryName(actor?.BackendType) : item.Kind == "job" && item.ProviderId == null
                    ? ActivityPresentation.JobSource(item.Label, actor?.BackendType) : item.Source,
                ProviderId = library ? "library" : item.ProviderId
            };
        }).ToArray();
        return Ok(new
        {
            items,
            hasMore = ordered.Length > limit || jobs.Count == scanLimit ||
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

    internal static AdminUiActivityItem MatchActivityItem(
        TrackMatchRecord match,
        ExternalMetadataSnapshotRecord? snapshot,
        ProviderTrackIdentityRecord? sourceIdentity,
        IReadOnlyCollection<ProviderTrackIdentityRecord> providerIdentities,
        IReadOnlyCollection<LibraryTrackRecord> libraryTracks,
        IReadOnlyCollection<string>? providerPriority = null)
    {
        var route = snapshot == null
            ? null
            : TrackRouteProjector.Project(
                snapshot,
                match,
                manual: null,
                sourceIdentity,
                libraryTracks,
                providerIdentities,
                providerPriority);
        var libraryTrack = route?.LibraryTrack;
        var providerRoute = route?.PrimaryProviderRoute;
        var providerCandidate = providerRoute == null
            ? null
            : MatchCandidateForRoute(match.CandidateResultsJson, providerRoute);
        var providerId = sourceIdentity?.ProviderId ?? snapshot?.ProviderId ?? "matching";
        var sourceTitle = snapshot == null ? null : AuditDetail(snapshot.PayloadJson, "title");
        var sourceArtist = snapshot == null ? null : AuditDetail(snapshot.PayloadJson, "artist");
        var sourceAlbum = snapshot == null ? null : AuditDetail(snapshot.PayloadJson, "album");
        var targetTitle = libraryTrack?.Title ?? providerCandidate?.Title ??
            (providerRoute == null ? null : sourceTitle);
        var targetArtist = libraryTrack?.Artist ?? providerCandidate?.Artist ??
            (providerRoute == null ? null : sourceArtist);
        var artworkUrl = sourceIdentity != null
            ? ExternalArtworkUrl(providerId, sourceIdentity.ExternalId)
            : libraryTrack?.CoverArtReference == null
                ? null
                : LocalArtworkUrl(libraryTrack.BackendItemId);
        var technicalDetails = new Dictionary<string, string>
        {
            ["decisionId"] = match.Id.ToString("N"),
            ["decisionVersion"] = match.DecisionVersion.ToString(),
            ["policyVersion"] = match.PolicyVersion
        };
        foreach (var component in MatchScoreComponents(match.CandidateResultsJson, match.LibraryTrackId))
            technicalDetails[$"score.{component.Key}"] = component.Value.ToString(
                "0.###",
                System.Globalization.CultureInfo.InvariantCulture);
        return new AdminUiActivityItem(
            match.Id.ToString("N"),
            "matching",
            providerId,
            MatchActivityLabel(match.State),
            match.State.ToString().ToLowerInvariant(),
            MatchActivityDetail(
                match,
                snapshot,
                sourceIdentity,
                targetTitle,
                targetArtist,
                libraryTrack?.BackendItemId ?? providerRoute?.ExternalId),
            match.DecidedAt,
            match.CorrelationId,
            SeverityForState(match.State.ToString()),
            ProviderId: providerId,
            ArtworkUrl: artworkUrl,
            SourceTitle: sourceTitle,
            SourceArtist: sourceArtist,
            SourceAlbum: sourceAlbum,
            TargetProviderId: libraryTrack != null ? "library" : providerRoute?.ProviderId,
            TargetTitle: targetTitle,
            TargetArtist: targetArtist,
            ConfidenceLabel: $"{Math.Round(match.Confidence * 100, 1)}%",
            DurationMilliseconds: libraryTrack?.DurationMilliseconds ??
                (snapshot == null ? null : AuditDurationMilliseconds(snapshot.PayloadJson)),
            Isrc: snapshot == null
                ? libraryTrack?.Isrc
                : AuditDetail(snapshot.PayloadJson, "isrc") ?? libraryTrack?.Isrc,
            SourceProviderTrackId: sourceIdentity?.ExternalId,
            TargetProviderTrackId: providerRoute?.ExternalId,
            BackendItemId: libraryTrack?.BackendItemId,
            ActorUserId: match.OwnerUserId.ToString("N"),
            Action: "track-match.evaluate",
            TechnicalDetails: technicalDetails);
    }

    private static IReadOnlyDictionary<string, double> MatchScoreComponents(
        string json,
        Guid? selectedLibraryTrackId)
    {
        try
        {
            var candidates = JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(json);
            return candidates?.FirstOrDefault(item =>
                item.LibraryTrackId == selectedLibraryTrackId)?.Components ??
                candidates?.FirstOrDefault()?.Components ??
                new Dictionary<string, double>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, double>();
        }
    }

    private static TrackMatchCandidateScore? MatchCandidateForRoute(
        string json,
        DurableProviderRoute route)
    {
        try
        {
            return JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(json)?
                .FirstOrDefault(candidate => candidate.ProviderTrackIds?.Any(item =>
                    ExternalTrackPlaybackPolicy.Normalize(item.Key) ==
                        ExternalTrackPlaybackPolicy.Normalize(route.ProviderId) &&
                    item.Value == route.ExternalId) == true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool MatchesActivitySourceIdentity(
        ExternalMetadataSnapshotRecord snapshot,
        ProviderTrackIdentityRecord identity) =>
        identity.ProviderId.Equals(snapshot.ProviderId, StringComparison.OrdinalIgnoreCase) &&
        (identity.ProviderAccountId == snapshot.ProviderAccountId ||
         identity.Scope == ProviderIdentityScope.Catalog && !identity.ProviderAccountId.HasValue) &&
        identity.ResourceKind == ProviderResourceKind.Track &&
        identity.ExternalIdHash == snapshot.ExternalIdHash;

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
            "scrobble" => AuditDetail(item.DetailsJson, "message") ?? TrackDetail(item.DetailsJson),
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
        var sourceTrackId = Detail(details, "sourceProviderTrackId", "sourceExternalId", "externalId");
        var backendItemId = Detail(details, "backendItemId", "targetBackendItemId");
        return new AdminUiActivityItem(
            item.Id.ToString("N"), kind, source, label, item.Outcome, detail, item.CreatedAt,
            item.CorrelationId, SeverityForState(item.Outcome), source,
            AuditDetail(item.DetailsJson, "playlistLinkId"),
            AuditDetail(item.DetailsJson, "playlistName"),
            ArtworkUrl: sourceTrackId == null
                ? backendItemId == null ? null : LocalArtworkUrl(backendItemId)
                : ExternalArtworkUrl(source, sourceTrackId),
            Isrc: Detail(details, "isrc"),
            SourceProviderTrackId: sourceTrackId,
            TargetProviderTrackId: Detail(details, "targetProviderTrackId", "targetExternalId"),
            BackendItemId: backendItemId,
            RouteDecisionId: Detail(details, "routeDecisionId"),
            ActorUserId: item.ActorUserId?.ToString("N"),
            Action: item.Action,
            DurationMilliseconds: LongDetail(details, "durationMilliseconds", "durationMs"),
            TechnicalDetails: details);
    }

    private static long? AuditDurationMilliseconds(string json)
    {
        var milliseconds = AuditDetail(json, "durationMilliseconds") ?? AuditDetail(json, "durationMs");
        if (long.TryParse(milliseconds, out var value) && value > 0) return value;
        return double.TryParse(AuditDetail(json, "durationSeconds"), out var seconds) && seconds > 0
            ? checked((long)Math.Round(seconds * 1000d))
            : null;
    }

    private static long? LongDetail(IReadOnlyDictionary<string, string> details, params string[] names)
    {
        var value = Detail(details, names);
        return long.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static string PlaylistArtworkUrl(PlaylistLinkRecord link) => link.ProviderAccountId.HasValue
        ? $"/api/admin/playlist-sources/{link.ProviderAccountId}/playlists/{Uri.EscapeDataString(link.SourcePlaylistId)}/artwork"
        : $"/api/admin/playlist-sources/public/{Uri.EscapeDataString(link.SourceProviderId)}/playlists/{Uri.EscapeDataString(link.SourcePlaylistId)}/artwork";

    private static string LocalArtworkUrl(string backendItemId) =>
        $"/api/admin/downloads/artwork/{Uri.EscapeDataString(backendItemId)}";

    private static string ExternalArtworkUrl(string providerId, string externalId) =>
        LocalArtworkUrl($"ext-{providerId}-song-{externalId}");

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
        string? targetTitle,
        string? targetArtist,
        string? targetId)
    {
        var sourceTitle = snapshot == null ? null : AuditDetail(snapshot.PayloadJson, "title");
        var sourceArtist = snapshot == null ? null : AuditDetail(snapshot.PayloadJson, "artist");
        var source = TrackLabel(sourceTitle, sourceArtist)
            ?? (identity == null ? null : $"{identity.ProviderId}:{identity.ExternalId}")
            ?? "External track";
        var target = TrackLabel(targetTitle, targetArtist) ?? targetId ?? "no playable match";
        var isrc = snapshot == null ? null : AuditDetail(snapshot.PayloadJson, "isrc");
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

    // Extensions are labelled with the registry they came from; everything else is built in.
    private async Task<IReadOnlyDictionary<string, string>> ExtensionOriginNamesAsync(CancellationToken cancellationToken)
    {
        var factory = HttpContext?.RequestServices?.GetService<IDbContextFactory<AllstarrDbContext>>();
        if (factory == null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await (from package in db.ExtensionPackages.AsNoTracking()
                          where package.State == ExtensionPackageState.Active && package.RegistryId != null
                          join registry in db.ExtensionRegistries.AsNoTracking() on package.RegistryId equals registry.Id
                          select new { package.ExtensionId, registry.Name }).ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.ExtensionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
    }

    private List<AdminUiProvider> BuildProviders(IReadOnlySet<string>? disabledProviders = null,
        IReadOnlyDictionary<string, string>? originNames = null,
        EffectiveProviderPolicySnapshot? usePolicy = null)
    {
        List<AdminUiProvider> providers =
        [
        new()
        {
            Id = "spotify",
            Name = "Spotify",
            Icon = "spotify",
            Status = ProviderStatus("spotify", !string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie)
                ? "configured" : "needs_config", disabledProviders),
            Categories = ["playlist", "lyrics"],
            ConfigSchema =
            [
                Field("SPOTIFY_API_CACHE_DURATION_MINUTES", "Cache minutes", "number", "spotifyApi.cacheDurationMinutes", min: 1),
                Field("SPOTIFY_API_PREFER_ISRC_MATCHING", "Prefer ISRC matching", "toggle", "spotifyApi.preferIsrcMatching"),
                Field("SPOTIFY_PATHFINDER_ENDPOINT", "Pathfinder endpoint", "text", "spotifyApi.pathfinderEndpoint", advanced: true, defaultValue: SpotifyApiSettings.DefaultPathfinderEndpoint),
                Field("SPOTIFY_LIBRARY_QUERY_HASH", "Library query hash", "text", "spotifyApi.libraryQueryHash", advanced: true, defaultValue: SpotifyApiSettings.DefaultLibraryQueryHash),
                Field("SPOTIFY_PLAYLIST_QUERY_HASH", "Playlist query hash", "text", "spotifyApi.playlistQueryHash", advanced: true, defaultValue: SpotifyApiSettings.DefaultPlaylistQueryHash),
                Field("SPOTIFY_LIKED_SONGS_QUERY_HASH", "Liked Songs query hash", "text", "spotifyApi.likedSongsQueryHash", advanced: true, defaultValue: SpotifyApiSettings.DefaultLikedSongsQueryHash),
                Field("SPOTIFY_TOTP_SECRETS_URL", "Public TOTP secrets URL", "text", "spotifyApi.totpSecretsUrl", advanced: true, defaultValue: SpotifyApiSettings.DefaultTotpSecretsUrl),
                Field("SPOTIFY_TOKEN_ENDPOINT", "Token endpoint", "text", "spotifyApi.tokenEndpoint", advanced: true, defaultValue: SpotifyApiSettings.DefaultTokenEndpoint)
            ]
        },
        new()
        {
            Id = "apple-musickit",
            Name = "Apple Music",
            Icon = "applemusic",
            Status = "available",
            Categories = ["metadata", "playlist", "streaming", "download", "lyrics"],
            Notes = ["Public catalog", "Personal library and playlists", "Optional GAMDL downloads"],
            ConfigSchema = [Field("APPLE_DOWNLOAD_URL", "Optional download gateway URL", "url", "appleDownload.baseUrl")],
            AccountSettings =
            [
                new AdminUiConfigField
                {
                    Key = "musicUserToken", Label = "Media User Token", Type = "password", Sensitive = true,
                    Required = true, Ownership = "provider-account",
                    HelpText = "Your Apple Music media-user-token. Used only for your library, playlists and optional downloads."
                },
                new AdminUiConfigField
                {
                    Key = "storefront", Label = "Storefront", Type = "text", Required = true,
                    DefaultValueJson = "\"us\"", Ownership = "provider-account",
                    HelpText = "Two-letter country code for your Apple Music account, such as us."
                }
            ]
        },
        new()
        {
            Id = "deezer",
            Name = "Deezer",
            Icon = "deezer",
            Status = ProviderStatus("deezer", string.IsNullOrWhiteSpace(_deezerSettings.Arl) ? "needs_config" : "configured", disabledProviders),
            Categories = ["metadata", "download", "streaming", "playlist"],
            ConfigSchema =
            [
                Field("DEEZER_MIN_REQUEST_INTERVAL_MS", "Minimum request interval", "number", "deezer.minRequestIntervalMs", min: 0)
            ]
        },
        new()
        {
            Id = "qobuz",
            Name = "Qobuz",
            Icon = "qobuz",
            LogoUrl = "/images/providers/qobuz.webp",
            Status = ProviderStatus("qobuz", string.IsNullOrWhiteSpace(_qobuzSettings.UserAuthToken) ? "needs_config" : "configured", disabledProviders),
            Categories = ["metadata", "download", "streaming", "playlist"],
            Notes = ["A Qobuz account is required for metadata search and playback."],
            ConfigSchema =
            [
                Field("QOBUZ_MIN_REQUEST_INTERVAL_MS", "Minimum request interval", "number", "qobuz.minRequestIntervalMs", min: 0)
            ]
        },
        new()
        {
            Id = "musicbrainz",
            Name = "MusicBrainz enrichment",
            Icon = "musicbrainz",
            Status = "available",
            Categories = ["enrichment"],
            Notes = ["Genres only", "Optional enrichment"],
            ConfigSchema =
            [
                Field("MUSICBRAINZ_SOURCE_ID", "Metadata source identifier", "text", "musicBrainz.sourceId", requiresRestart: true),
                Field("MUSICBRAINZ_BASE_URL", "Metadata service URL", "url", "musicBrainz.baseUrl", requiresRestart: true),
                Field("MUSICBRAINZ_RATE_LIMIT_MS", "Request interval (ms)", "number", "musicBrainz.rateLimitMs", requiresRestart: true, min: 1000, max: 60000),
                Field("MUSICBRAINZ_AUTHORIZED_USER_AGENT_OVERRIDE", "Authorized user agent override", "text", "musicBrainz.authorizedUserAgentOverride", requiresRestart: true,
                    helpText: "Leave blank for the standard application identity. Use an override only with the metadata service operator's permission.")
            ]
        },
        new()
        {
            Id = "lrcred",
            Name = "lrc.red",
            Icon = "lyrics",
            Status = "available",
            Categories = ["lyrics"],
            Notes = ["Public word-synced TTML; no account required"]
        },
        new()
        {
            Id = "unison",
            Name = "Unison",
            Icon = "lyrics",
            Status = "available",
            Categories = ["lyrics"],
            Notes = ["Lyrics from Unison (https://unison.boidu.dev)"]
        },
        new()
        {
            Id = "paxsenix",
            Name = "Paxsenix",
            Icon = "lyrics",
            Status = "available",
            Categories = ["lyrics"],
            Notes = ["Apple Music lyrics through Paxsenix; no account required"]
        },
        new()
        {
            Id = "lyricsplus",
            Name = "LyricsPlus",
            Icon = "lyrics",
            Status = "available",
            Categories = ["lyrics"],
            Notes = ["Public instances with fallback; optional self-hosted service"],
            ConfigSchema = [Field("LYRICSPLUS_URL", "Optional self-hosted URL", "url", "lyricsPlus.baseUrl",
                helpText: "Leave blank to use public instances with fallback. A configured service is used exclusively.")]
        },
        new()
        {
            Id = "lrclib",
            Name = "LRCLib",
            Icon = "lyrics",
            Status = "available",
            Categories = ["lyrics"]
        },
        new()
        {
            Id = "genius",
            Name = "Genius",
            Icon = "genius",
            Status = "available",
            Categories = ["lyrics"],
            Notes = ["Public plain lyrics; no account required"]
        },
        new()
        {
            Id = "spicylyrics",
            Name = "SpicyLyrics",
            Icon = "spicylyrics",
            LogoUrl = "/images/providers/spicylyrics.ico",
            Status = "needs_config",
            Categories = ["lyrics"],
            Notes = ["Free server API key; known Spotify track required"]
        }
        ];

        providers.AddRange(CurrentProviderSupportCatalog.All
            .Where(item => item.Id is "lastfm" or "listenbrainz" or "multi-scrobbler" or "koito")
            .Select(item => new AdminUiProvider
            {
                Id = item.Id,
                Name = item.Name,
                Icon = item.Id,
                Status = "available",
                Categories = ["scrobbling"]
            }));

        foreach (var provider in providers)
        {
            provider.ConnectionKind ??= provider.ConfigSchema.Count > 0
                ? "operator_managed"
                : "built_in";
            provider.ImplementationOrigin ??= "built_in";
            provider.OriginName ??= "Built-in";
            provider.RouteId ??= $"builtin:{provider.Id}";
            provider.CapabilityRoutes.Add(new AdminUiProviderCapabilityRoute
            {
                RouteId = provider.RouteId,
                Name = provider.Name,
                Origin = provider.ImplementationOrigin,
                Capabilities = provider.Categories.ToList()
            });
        }

        var runtimeStatuses = _providerStatusManager.GetAllAccountFreeStatuses()
            .Concat(_providerStatusManager.GetAppleDownloadEndpointStatuses()).ToList();
        if (_providerRegistry != null)
        {
            foreach (var item in _providerRegistry.Providers)
            {
                var categories = item.Capabilities
                    .Where(capability =>
                        capability.SupportState != ProviderCapabilitySupportState.Unavailable)
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
                    Multiline = setting.Multiline,
                    Required = setting.Required,
                    Options = setting.Choices.ToList(),
                    HelpText = setting.HelpText,
                    DefaultValueJson = setting.DefaultJson,
                    Ownership = "provider-account"
                }).ToList();
                var route = new AdminUiProviderCapabilityRoute
                {
                    RouteId = $"{item.Origin.ToString().ToLowerInvariant()}:{item.Id}",
                    Name = item.Origin == ProviderOrigin.Extension
                        ? $"{item.DisplayName} · Extension SDK {item.SdkVersion}"
                        : item.DisplayName,
                    Origin = item.Origin.ToString().ToLowerInvariant(),
                    Capabilities = categories
                };
                var existing = providers.SingleOrDefault(provider =>
                    provider.Id.Equals(item.Id, StringComparison.Ordinal));
                if (existing != null)
                {
                    existing.Description ??= item.Description;
                    existing.Categories = existing.Categories
                        .Concat(categories)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    existing.AccountSettings = existing.AccountSettings
                        .Concat(accountSettings)
                        .GroupBy(setting => setting.Key, StringComparer.Ordinal)
                        .Select(group => group.First())
                        .ToList();
                    if (existing.CapabilityRoutes.All(existingRoute =>
                            existingRoute.RouteId != route.RouteId))
                    {
                        existing.CapabilityRoutes.Add(route);
                    }
                    continue;
                }

                providers.Add(new AdminUiProvider
                {
                    Id = item.Id,
                    Name = item.DisplayName,
                    Description = item.Description,
                    Icon = item.Id == "monochrome" ? "monochrome" : "extension",
                    LogoUrl = item.Origin == ProviderOrigin.Extension && item.Branding != null
                        ? $"/api/admin/extensions/providers/{Uri.EscapeDataString(item.Id)}/icon"
                        : null,
                    Status = "unknown",
                    Categories = categories,
                    Notes = item.Origin == ProviderOrigin.Extension
                        ? [$"Extension SDK {item.SdkVersion}"]
                        : [],
                    ConnectionKind = item.Origin == ProviderOrigin.Extension
                        ? "extension"
                        : "built_in",
                    UsesSignedSession = item.UsesSignedSession,
                    OriginName = item.Origin == ProviderOrigin.Extension
                        ? originNames?.GetValueOrDefault(item.Id) ?? "Extension"
                        : "Built-in",
                    ImplementationOrigin = item.Origin.ToString().ToLowerInvariant(),
                    RouteId = route.RouteId,
                    AccountSettings = accountSettings,
                    CapabilityRoutes = [route]
                });
            }
        }
        foreach (var provider in providers)
        {
            var registered = _providerRegistry?.TryGet(provider.Id, out var useDescriptor) == true
                ? useDescriptor : null;
            var useMode = usePolicy?.GetProviderMode(provider.Id) ?? (registered == null
                ? ProviderUseMode.Automatic : ProviderUsePolicy.DefaultMode(registered));
            provider.UseMode = ProviderUsePolicy.Name(useMode);
            provider.SupportedAudiences = allstarr.Core.Identity.ProviderAccountAudiences.Supported(_providerRegistry, provider.Id)
                .Select(scope => scope.ToString()).ToList();
            provider.SharingDescription = allstarr.Core.Identity.ProviderAccountAudiences.Description(_providerRegistry, provider.Id);
            var statuses = runtimeStatuses
                .Where(status => status.Provider.Equals(provider.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (statuses.Count > 0)
            {
                provider.RuntimeCapabilities = statuses.Select(ToAdminRuntimeCapability).ToList();
                provider.Status = AggregateProviderStatus(statuses);
            }
            else if (_providerRegistry?.TryGet(provider.Id, out var descriptor) == true &&
                     descriptor!.Origin == ProviderOrigin.BuiltIn &&
                     descriptor.Capabilities.Any(capability => capability.HasUsableImplementation) &&
                     descriptor.Capabilities.Where(capability => capability.HasUsableImplementation)
                         .All(capability => capability.AccountRequirement == ProviderAccountRequirement.Required))
            {
                provider.Status = "needs_config";
            }
            if (disabledProviders?.Contains(provider.Id) == true || useMode == ProviderUseMode.Off)
                provider.Status = "disabled";
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
            LatencyMilliseconds = status.LatencyMilliseconds,
            VerificationMeasurementId = status.VerificationMeasurementId,
            VerificationMethod = status.VerificationMethod,
            ReasonCode = status.ReasonCode
        };

    internal static string AggregateProviderStatus(IReadOnlyList<ProviderRuntimeStatus> statuses)
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

        var enabledSupported = statuses.Where(status => status.IsEnabled && status.IsSupported).ToList();
        if (enabledSupported.All(status =>
                status.Configuration == ProviderConfigurationState.NeedsConfiguration))
        {
            return "needs_config";
        }

        if (enabledSupported.Any(status =>
                status.Configuration == ProviderConfigurationState.NeedsConfiguration))
        {
            return "partial_config";
        }

        if (enabledSupported.Any(status => status.Health == Services.Common.ProviderHealthState.Degraded))
        {
            return "degraded";
        }

        if (enabledSupported.Any(status => status.IsReady))
        {
            return "healthy";
        }

        return "available";
    }

    private List<AdminUiPriorityGroup> BuildPriorityGroups(EffectiveProviderPolicySnapshot? policy)
    {
        var activeBackend = _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin";
        var pinnedLocalProvider = BuildPinnedLocalProvider(activeBackend);
        return
        [
            Priority(
                ProviderCapabilityKind.Metadata,
                "metadata",
                "Metadata search order",
                "Orders sources among similarly relevant search results. Title, artist, and requested versions determine relevance. Playback uses Streaming and Download order below.",
                "MULTI_PROVIDER_ENABLED_SEARCH",
                pinnedProvider: null,
                policy: policy),
            Priority(
                ProviderCapabilityKind.Download,
                "download",
                "Download priority",
                "Download routes after the local library. Drag to change which source fills a missing track.",
                null,
                pinnedProvider: pinnedLocalProvider,
                policy: policy),
            Priority(
                ProviderCapabilityKind.Streaming,
                "streaming",
                "Streaming priority",
                "Stream routes after the local library. Drag to change which source plays a missing track.",
                null,
                pinnedProvider: pinnedLocalProvider,
                policy: policy),
            Priority(
                ProviderCapabilityKind.Playlist,
                "playlist",
                "Playlist discovery priority",
                "Order used when fetching playlists and playlist tracks from each source.",
                "MULTI_PROVIDER_ENABLED_PLAYLIST",
                pinnedProvider: null,
                policy: policy),
            Priority(
                ProviderCapabilityKind.Lyrics,
                "lyrics",
                "Lyrics priority",
                "Choose when media-server lyrics and other available sources are tried. Synced lyrics are preferred.",
                null,
                pinnedProvider: null,
                policy: policy)
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
        ProviderCapabilityKind capability,
        string id,
        string label,
        string description,
        string? enabledEnvKey,
        AdminUiPinnedProvider? pinnedProvider,
        EffectiveProviderPolicySnapshot? policy)
    {
        var definition = ProviderOrderPolicyCatalog.Find(capability)!;
        var providers = (policy?.GetProviderOrder(capability) ??
                (_configuration[definition.BootstrapKey] ?? definition.DefaultValue)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_providerRegistry != null)
        {
            var available = _providerRegistry.FindByCapability(capability, includeNonOperational: true);
            providers.AddRange(available.Select(provider => provider.Id)
                .Where(providerId => !providers.Contains(providerId, StringComparer.OrdinalIgnoreCase))
                .OrderBy(providerId => providerId, StringComparer.Ordinal));
            if (capability == ProviderCapabilityKind.Metadata)
            {
                var lookupOnly = available.Where(provider => !provider.Capabilities
                        .Single(item => item.Capability == capability).Hooks.Any(hook => hook is "searchTracks" or "searchAlbums" or "searchArtists"))
                    .Select(provider => provider.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                providers.RemoveAll(lookupOnly.Contains);
            }
        }
        if (capability == ProviderCapabilityKind.Lyrics)
            foreach (var source in new[] { "backend", "kept" })
                if (!providers.Contains(source, StringComparer.Ordinal)) providers.Add(source);
        return new AdminUiPriorityGroup
        {
            Id = id,
            Label = label,
            Description = description,
            EnvKey = definition.BootstrapKey,
            EnabledEnvKey = enabledEnvKey,
            Providers = providers,
            PinnedProvider = pinnedProvider
        };
    }

    private string ProviderStatus(
        string id,
        string configuredStatus,
        IReadOnlySet<string>? disabledProviders)
    {
        var disabled = disabledProviders?.Contains(id) ??
            (_configuration["MULTI_PROVIDER_DISABLED_PROVIDERS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(id, StringComparer.OrdinalIgnoreCase);
        return disabled ? "disabled" : configuredStatus;
    }

    private static List<AdminUiConfigSection> BuildConfigSections() =>
    [
        Section("operator", "Service and retention settings", OperatorSettingPolicy.Definitions
            .Where(item => !item.Key.StartsWith("MusicBrainz:", StringComparison.Ordinal) && item.Key is not ("Debug:LogAllRequests" or "Gateway:PublicListenerUrl"))
            .Select(item => Field(
                LegacyEnvParser.DurableAliasKeys.First(key => LegacyEnvParser.TryGetDurableAlias(key, out var target) && target == item.Key),
                OperatorSettingLabel(item.Key),
                item.ValueType == RuntimeSettingValueType.Boolean ? "toggle" : item.ValueType == RuntimeSettingValueType.Integer ? "number" : "text",
                "operatorSettings." + item.Key, requiresRestart: item.RequiresRestart, min: item.Minimum, max: item.Maximum,
                placeholder: item.Key == "ChallengeSolver:Url" ? "http://flaresolverr:8191" : null,
                helpText: item.Key == "ChallengeSolver:Url"
                    ? "Optional. Your FlareSolverr or Byparr address, for example http://flaresolverr:8191. Used only for sources that sit behind Cloudflare checks."
                    : item.Key == "ChallengeSolver:TimeoutSeconds" ? "Seconds to wait for the solver. Default: 60."
                    : "Saved in the data folder. Allstarr restarts after saving this setting."))
            .Append(Field("ODESLI_API_KEY", "Optional song.link API key", "password", "operatorSettings.Odesli:ApiKey",
                sensitive: true, requiresRestart: true,
                helpText: "Stored encrypted. Optional; blank clears a saved key. Allstarr restarts after saving."))
            .ToList()),
        Section("general", "General",
        [
            Field(
                "AUDIO_QUALITY",
                "Audio quality",
                "audio-quality",
                "audio.quality",
                AudioQualityPolicy.Steps.ToList(),
                helpText: "Sets the maximum quality Allstarr uses for playback and downloads. A listener's bandwidth limit can lower playback quality."),
            Field("LISTENERS_CAN_CONNECT_OWN_ACCOUNTS", "Listeners can connect their own accounts", "boolean",
                "providerAccounts.listenersCanConnectOwnAccounts",
                helpText: "Allows listeners to add Personal provider accounts. Existing accounts remain usable and removable when turned off."),
            DeploymentField("BACKEND_TYPE", "Backend", "select", "backendType", ["Jellyfin", "Subsonic"]),
            Field("STORAGE_MODE", "Storage mode", "select", "library.storageMode", ["Permanent", "Cache"]),
            Field("DOWNLOAD_MODE", "Download mode", "select", "library.downloadMode", ["Track", "Album"]),
            Field("EXPLICIT_FILTER", "Explicit filter", "select", "explicitFilter", ["All", "ExplicitOnly", "CleanOnly"]),
            Field(
                "MATCHING_LOCAL_PREFERENCE_PERCENT",
                "Tentative local window",
                "number",
                "matching.localPreferencePercent",
                helpText: "Only used when no candidate qualifies for automatic acceptance. Accepted matches always use local first, then Streaming priority. This window never raises confidence. Default: 7 points.",
                min: 0,
                max: 20),
            Field("PLAYBACK_START_PREFERENCE", "Playback start", "select", "playback.startPreference",
                ["FastStart", "Balanced", "BestQuality"],
                helpText: "Default for listeners. Fast start prefers the quickest lossless source; Balanced waits for better quality when it usually starts within three seconds; Best quality always prefers the highest quality."),
            Field("PLAYBACK_PREPARE_UPCOMING_SONGS", "Prepare upcoming songs", "toggle",
                "playback.prepareUpcomingSongs",
                helpText: "Off by default. Turn on only if your player waits until a song ends before requesting the next one."),
            Field("PLAYBACK_PREPARE_UPCOMING_COUNT", "Upcoming songs to prepare", "number",
                "playback.prepareUpcomingCount", helpText: "Number of songs to prepare when enabled. Default: 1.", min: 1, max: 2),
            Field(
                "PLAYBACK_LASTFM_SIMILAR_ENABLED",
                "Last.fm similar-song fallback",
                "toggle",
                "playback.lastFmSimilarEnabled",
                helpText: "Uses a connected Last.fm account for similar-song and Instant Mix suggestions when the provider has none. Default: on."),
            Field(
                "PLAYBACK_LISTENBRAINZ_SIMILAR_ENABLED",
                "ListenBrainz similar-song fallback",
                "toggle",
                "playback.listenBrainzSimilarEnabled",
                helpText: "Uses the public ListenBrainz similar-recordings fallback when the provider and Last.fm have none. Default: on.")
        ]),
        Section("paths", "Library paths",
        [
            DeploymentField("LIBRARY_DOWNLOAD_PATH", "Download path", "text", "library.downloadPath"),
            DeploymentField("LIBRARY_KEPT_PATH", "Kept downloads path", "text", "library.keptPath"),
            Field("PLAYLISTS_DIRECTORY", "Playlists directory", "text", "playlistsDirectory")
        ]),
        Section("audio-limits", "Audio limits",
        [
            Field("CACHE_AUDIO_MAXIMUM_GIB", "Maximum audio cache (GiB)", "number", "cache.audioMaximumGiB", min: 1, max: 1024,
                helpText: "Applies to disposable managed audio and temporary read-ahead. Active readers and protected files are preserved. Default: 2 GiB."),
            Field("PROVIDER_STREAMS_PER_LISTENER", "Provider streams per listener", "number", "playback.providerStreamsPerListener", min: 1, max: 64,
                helpText: "Includes opening and active external streams and original downloads. Native backend streams are unaffected. Default: 6."),
            Field("MEDIA_MAXIMUM_CONCURRENT_CONVERSIONS", "Concurrent media conversions", "number", "media.maximumConcurrentConversions", min: 1, max: 16,
                helpText: "Maximum simultaneous core media conversions. New requests use the saved limit without restarting. Default: 2.")
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
            Field("CACHE_MEDIA_MAXIMUM_MEGABYTES", "Media cache total MiB", "number", "cache.mediaMaximumMegabytes"),
            Field("CACHE_MEDIA_MAXIMUM_ENTRY_MEGABYTES", "Media cache entry MiB", "number", "cache.mediaMaximumEntryMegabytes"),
            Field("CACHE_MEDIA_CLEANUP_FILE_LIMIT", "Cleanup scan file limit", "number", "cache.mediaCleanupFileLimit")
        ]),
        Section("client-address", "Music-client address",
        [
            Field("PUBLIC_LISTENER_URL", "Music-client address", "text", "operatorSettings.Gateway:PublicListenerUrl",
                helpText: "Optional. The Allstarr address listeners use in music clients, including any gateway path prefix. Shown in Getting started. Changes apply immediately.")
        ]),
        Section("network", "Network and security",
        [
            DeploymentField("ADMIN_BIND_ANY_IP", "Bind admin on all interfaces", "toggle", "admin.bindAnyIp"),
            DeploymentField("ADMIN_TRUSTED_SUBNETS", "Trusted admin subnets", "text", "admin.trustedSubnets"),
            Field("DEBUG_LOG_ALL_REQUESTS", "Request usage logging", "toggle", "debug.logAllRequests", requiresRestart: true)
        ]),
        Section("spotify-import", "Imported playlist matching",
        [
            Field("SPOTIFY_IMPORT_ENABLED", "Enabled", "toggle", "spotifyImport.enabled"),
            Field("SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS", "Matching interval hours", "number", "spotifyImport.matchingIntervalHours", min: 0)
        ])
    ];

    private static string OperatorSettingLabel(string key) => key switch
    {
        "ChallengeSolver:Url" => "Challenge solver URL",
        "ChallengeSolver:TimeoutSeconds" => "Solver timeout",
        "Backend:MusicLibraryIds" => "Music library identifiers (blank selects all permitted libraries)",
        "Extensions:AllowRemoteInstall" => "Allow remote extension installation",
        "Odesli:Enabled" => "Enable cross-service song links",
        "Odesli:RateLimitMs" => "Optional song.link request interval (ms)",
        "Cors:AllowedOrigins" => "Browser origins (comma separated)",
        "Cors:AllowedMethods" => "Browser HTTP methods (blank uses defaults)",
        "Cors:AllowedHeaders" => "Browser request headers (blank uses defaults)",
        "Cors:AllowCredentials" => "Allow browser credentials for explicit origins",
        "Storage:BackupRetentionCount" => "Automatic backup copies to retain",
        "Operations:EventLog:RetentionDays" => "Activity retention days",
        "Operations:EventLog:MaximumRows" => "Maximum activity rows",
        "Operations:EventLog:CleanupIntervalMinutes" => "Activity cleanup interval (minutes)",
        "Operations:EventLog:CleanupBatchSize" => "Activity cleanup batch size",
        "Operations:EventLog:JobHistoryRetentionDays" => "Job history retention days",
        _ => key
    };

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
        int? max = null,
        bool advanced = false,
        string? defaultValue = null) =>
        new()
        {
            Key = key,
            Label = label,
            Type = type,
            ValuePath = valuePath,
            Options = options ?? [],
            Placeholder = placeholder,
            Sensitive = sensitive,
            RequiresRestart = requiresRestart || (LegacyEnvParser.TryGetDurableAlias(key, out var target) && RuntimeSettingCatalog.Require(target).RequiresRestart),
            Ownership = ownership,
            ReadOnly = readOnly,
            HelpText = helpText,
            Advanced = advanced,
            DefaultValueJson = defaultValue == null ? null : JsonSerializer.Serialize(defaultValue),
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
    long? DurationMilliseconds = null,
    IReadOnlyDictionary<string, string>? TechnicalDetails = null,
    string? ActorDisplayName = null);
