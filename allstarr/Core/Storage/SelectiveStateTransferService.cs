using System.IO.Compression;
using System.Text.Json;
using allstarr.Core.Downloads;
using allstarr.Core.Favorites;
using allstarr.Core.Intelligence;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Playback;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public enum TransferCategory
{
    Settings,
    Accounts,
    Playlists,
    Intelligence,
    Extensions
}

public sealed record SelectiveTransferReport(
    IReadOnlyDictionary<string, int> RowsByEntry,
    IReadOnlyList<string> IncludedCategories,
    IReadOnlyList<string> ExcludedCategories,
    int TotalRows);

public sealed record SelectiveExportRequest
{
    public bool IncludeSettings { get; init; } = true;
    public bool IncludeAccounts { get; init; } = true;
    public bool IncludePlaylists { get; init; } = true;
    public bool IncludeIntelligence { get; init; } = true;
    public bool IncludeExtensions { get; init; } = true;
}

public sealed record SelectiveImportRequest
{
    public bool ImportSettings { get; init; } = true;
    public bool ImportAccounts { get; init; } = true;
    public bool ImportPlaylists { get; init; } = true;
    public bool ImportIntelligence { get; init; } = true;
    public bool ImportExtensions { get; init; } = true;
    public string BackupJson { get; init; } = string.Empty;
}

public sealed class SelectiveTransferValidationException : Exception
{
    public SelectiveTransferValidationException(string message) : base(message) { }
}

public sealed class SelectiveTransferSchemaMismatchException : Exception
{
    public SelectiveTransferSchemaMismatchException(string message) : base(message) { }
}

/// <summary>
/// Selective subset of <see cref="DurableStateTransferService"/> that exports and imports
/// only the user-chosen categories (Settings, Accounts, Playlists, Intelligence, Extensions).
/// The full archive format is preserved so a selective archive is a strict subset of a
/// full archive; missing entries are simply absent and a full archive is accepted by a
/// selective import that requests only the categories it needs.
/// </summary>
public sealed class SelectiveStateTransferService
{
    public const int CurrentFormatVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly IReadOnlyDictionary<TransferCategory, IReadOnlyList<string>> CategoryEntries =
        new Dictionary<TransferCategory, IReadOnlyList<string>>
        {
            [TransferCategory.Settings] = new[]
            {
                "tenants",
                "tenant-runtime-settings",
                "audit-events"
            },
            [TransferCategory.Accounts] = new[]
            {
                "users",
                "backend-identities",
                "provider-accounts",
                "secret-references",
                "secret-versions"
            },
            [TransferCategory.Playlists] = new[]
            {
                "library-tracks",
                "external-metadata-snapshots",
                "track-matches",
                "manual-track-overrides",
                "canonical-recordings",
                "provider-track-identities",
                "provider-route-decisions",
                "provider-route-outcomes",
                "playlist-links",
                "playlist-source-snapshots",
                "playlist-source-entries",
                "playlist-sync-runs",
                "playlist-sync-entry-results",
                "playlist-target-memberships"
            },
            [TransferCategory.Intelligence] = new[]
            {
                "intelligence-policies",
                "listening-signals",
                "listening-profiles",
                "recommendation-runs",
                "recommendation-candidates",
                "generated-sets",
                "generated-set-entries",
                "metadata-enrichment-plans",
                "metadata-enrichment-applications",
                "favorite-events",
                "favorite-actions",
                "favorite-states",
                "favorite-action-policies",
                "managed-files",
                "managed-file-references",
                "provider-download-workspaces",
                "provider-download-artifacts",
                "playback-delivery-checkpoints",
                "jobs",
                "job-attempts",
                "job-schedules",
                "outbox",
                "health-samples",
                "health-rollups",
                "circuits"
            },
            [TransferCategory.Extensions] = new[]
            {
                "extension-registries",
                "extension-packages",
                "extension-permission-reviews",
                "extension-logs"
            }
        };

    /// <summary>
    /// Dependencies: importing a category requires the dependency to be present either
    /// in the archive or already in the target. Settings is the root for everything.
    /// Accounts depends on Settings. Playlists and Intelligence depend on Accounts.
    /// Extensions depend on Settings.
    /// </summary>
    private static readonly IReadOnlyDictionary<TransferCategory, IReadOnlyList<TransferCategory>> CategoryDependencies =
        new Dictionary<TransferCategory, IReadOnlyList<TransferCategory>>
        {
            [TransferCategory.Settings] = Array.Empty<TransferCategory>(),
            [TransferCategory.Accounts] = new[] { TransferCategory.Settings },
            [TransferCategory.Playlists] = new[] { TransferCategory.Settings, TransferCategory.Accounts },
            [TransferCategory.Intelligence] = new[] { TransferCategory.Settings, TransferCategory.Accounts },
            [TransferCategory.Extensions] = new[] { TransferCategory.Settings }
        };

    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableStorageOptions _options;
    private readonly DurableStorageState _storageState;

    public SelectiveStateTransferService(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageOptions options,
        DurableStorageState storageState)
    {
        _contextFactory = contextFactory;
        _options = options;
        _storageState = storageState;
    }

    public IReadOnlyList<TransferCategory> ResolveIncludedCategories(SelectiveExportRequest request)
    {
        var included = new List<TransferCategory>();
        if (request.IncludeSettings) included.Add(TransferCategory.Settings);
        if (request.IncludeAccounts) included.Add(TransferCategory.Accounts);
        if (request.IncludePlaylists) included.Add(TransferCategory.Playlists);
        if (request.IncludeIntelligence) included.Add(TransferCategory.Intelligence);
        if (request.IncludeExtensions) included.Add(TransferCategory.Extensions);
        return included;
    }

    public IReadOnlyList<TransferCategory> ResolveIncludedCategories(SelectiveImportRequest request)
    {
        var included = new List<TransferCategory>();
        if (request.ImportSettings) included.Add(TransferCategory.Settings);
        if (request.ImportAccounts) included.Add(TransferCategory.Accounts);
        if (request.ImportPlaylists) included.Add(TransferCategory.Playlists);
        if (request.ImportIntelligence) included.Add(TransferCategory.Intelligence);
        if (request.ImportExtensions) included.Add(TransferCategory.Extensions);
        return included;
    }

    public async Task<(DurableStateTransferArtifact Artifact, SelectiveTransferReport Report)> ExportAsync(
        string destinationDirectory,
        SelectiveExportRequest request,
        bool writesQuiesced,
        CancellationToken cancellationToken = default)
    {
        if (!writesQuiesced)
        {
            throw new InvalidOperationException(
                "A selective export requires confirmed write quiescence.");
        }

        var snapshot = _storageState.GetSnapshot();
        if (snapshot.Readiness != DurableStorageReadiness.Ready)
        {
            throw new InvalidOperationException("Durable storage must be ready before export.");
        }

        var included = ResolveIncludedCategories(request);
        if (included.Count == 0)
        {
            throw new SelectiveTransferValidationException(
                "At least one category must be selected for export.");
        }

        var includedSet = new HashSet<TransferCategory>(included);
        var excluded = Enum.GetValues<TransferCategory>()
            .Where(category => !includedSet.Contains(category))
            .ToArray();

        var directory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(directory);
        var createdAt = DateTimeOffset.UtcNow;
        var path = Path.Combine(
            directory,
            $"allstarr-selective-{createdAt:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}.zip");

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var compatibility = await DurableSchemaCompatibility.InspectAsync(context, cancellationToken);
        if (!compatibility.IsCurrent)
        {
            throw new InvalidOperationException(
                "Durable storage schema must match this Allstarr build before export.");
        }

        var schemaVersion = compatibility.CurrentSchemaVersion;
        var rowsByEntry = new Dictionary<string, int>(StringComparer.Ordinal);

        await using (var stream = new FileStream(
                         path,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None,
                         81920,
                         FileOptions.Asynchronous))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifest = new SelectiveManifest
            {
                FormatVersion = CurrentFormatVersion,
                IsFullExport = false,
                SourceProvider = _options.ParseProvider().ToString(),
                SchemaVersion = schemaVersion,
                ApplicationVersion = AppVersion.Version,
                CreatedAt = createdAt,
                SecretKeyMaterialIncluded = false,
                IncludedCategories = included.Select(category => category.ToString()).ToArray()
            };
            await WriteManifestAsync(archive, manifest, cancellationToken);

            var orderedEntries = included
                .SelectMany(category => CategoryEntries[category])
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            foreach (var entry in orderedEntries)
            {
                var count = await WriteCategoryEntryAsync(context, archive, entry, cancellationToken);
                rowsByEntry[entry] = count;
            }
        }

        var hash = await ComputeSha256Async(path, cancellationToken);
        var artifact = new DurableStateTransferArtifact(
            path,
            hash,
            _options.ParseProvider().ToString(),
            schemaVersion,
            createdAt);

        var report = new SelectiveTransferReport(
            rowsByEntry,
            included.Select(c => c.ToString()).ToArray(),
            excluded.Select(c => c.ToString()).ToArray(),
            rowsByEntry.Values.Sum());

        return (artifact, report);
    }

    public async Task<SelectiveTransferReport> ImportAsync(
        SelectiveImportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.BackupJson))
        {
            throw new SelectiveTransferValidationException("BackupJson payload is required.");
        }

        var included = ResolveIncludedCategories(request);
        if (included.Count == 0)
        {
            throw new SelectiveTransferValidationException(
                "At least one category must be selected for import.");
        }

        var archivePath = await MaterializeArchiveAsync(request.BackupJson, cancellationToken);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var manifest = await ReadManifestAsync(archive, cancellationToken);

            if (manifest.FormatVersion != CurrentFormatVersion)
            {
                throw new SelectiveTransferSchemaMismatchException(
                    $"Selective transfer format version {manifest.FormatVersion} is not compatible with this build (expected {CurrentFormatVersion}).");
            }

            var archiveCategories = manifest.IsFullExport
                ? (IReadOnlyList<TransferCategory>)Enum.GetValues<TransferCategory>()
                : manifest.IncludedCategories
                    .Select(ParseCategory)
                    .ToArray();

            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var compatibility = await DurableSchemaCompatibility.InspectAsync(context, cancellationToken);
            if (!compatibility.IsCurrent)
            {
                throw new InvalidOperationException(
                    "Durable storage schema must match this Allstarr build before import.");
            }

            var targetIsEmpty = !await HasAnyDataAsync(context, cancellationToken);
            ValidateImportRequest(included, archiveCategories, targetIsEmpty);

            var rowsByEntry = new Dictionary<string, int>(StringComparer.Ordinal);
            var orderedEntries = included
                .SelectMany(category => CategoryEntries[category])
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            foreach (var entry in orderedEntries)
            {
                if (!archive.Entries.Any(e => e.FullName.Equals($"{entry}.json", StringComparison.Ordinal)))
                {
                    continue;
                }

                var count = await ReadCategoryEntryAsync(context, archive, entry, cancellationToken);
                rowsByEntry[entry] = count;
            }

            await context.SaveChangesAsync(cancellationToken);

            return new SelectiveTransferReport(
                rowsByEntry,
                included.Select(c => c.ToString()).ToArray(),
                Enum.GetValues<TransferCategory>()
                    .Where(c => !included.Contains(c))
                    .Select(c => c.ToString())
                    .ToArray(),
                rowsByEntry.Values.Sum());
        }
        finally
        {
            try { File.Delete(archivePath); } catch { }
        }
    }

    private static void ValidateImportRequest(
        IReadOnlyList<TransferCategory> requested,
        IReadOnlyList<TransferCategory> archiveCategories,
        bool targetIsEmpty)
    {
        var archiveSet = new HashSet<TransferCategory>(archiveCategories);
        var missing = new List<string>();
        foreach (var category in requested)
        {
            foreach (var dependency in CategoryDependencies[category])
            {
                var dependencySatisfied =
                    archiveSet.Contains(dependency) ||
                    (!targetIsEmpty && dependency == TransferCategory.Settings);
                if (!dependencySatisfied)
                {
                    missing.Add($"'{category}' requires '{dependency}'");
                }
            }
        }
        if (missing.Count > 0)
        {
            throw new SelectiveTransferValidationException(
                "Import dependencies are not satisfied: " + string.Join("; ", missing) +
                ". Re-export the archive with the required categories enabled, or import those categories first.");
        }
    }

    private static async Task<bool> HasAnyDataAsync(
        AllstarrDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.Tenants.AnyAsync(cancellationToken)
            || await context.Users.AnyAsync(cancellationToken)
            || await context.ProviderAccounts.AnyAsync(cancellationToken)
            || await context.PlaylistLinks.AnyAsync(cancellationToken)
            || await context.ExtensionRegistries.AnyAsync(cancellationToken)
            || await context.IntelligencePolicies.AnyAsync(cancellationToken);
    }

    private static TransferCategory ParseCategory(string value)
    {
        if (!Enum.TryParse<TransferCategory>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw new SelectiveTransferValidationException(
                $"Archive references an unknown transfer category '{value}'.");
        }
        return parsed;
    }

    private async Task<int> WriteCategoryEntryAsync(
        AllstarrDbContext context,
        ZipArchive archive,
        string entry,
        CancellationToken cancellationToken)
    {
        switch (entry)
        {
            case "tenants":
                {
                    var rows = await context.Tenants.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "tenant-runtime-settings":
                {
                    var rows = await context.TenantRuntimeSettings.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "users":
                {
                    var rows = await context.Users.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "backend-identities":
                {
                    var rows = await context.BackendIdentities.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "provider-accounts":
                {
                    var rows = await context.ProviderAccounts.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "secret-references":
                {
                    var rows = await context.SecretReferences.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "secret-versions":
                {
                    var rows = await context.SecretVersions.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "audit-events":
                {
                    var rows = await context.AuditEvents.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "library-tracks":
                {
                    var rows = await context.LibraryTracks.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "external-metadata-snapshots":
                {
                    var rows = await context.ExternalMetadataSnapshots.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "track-matches":
                {
                    var rows = await context.TrackMatches.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "manual-track-overrides":
                {
                    var rows = await context.ManualTrackOverrides.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "canonical-recordings":
                {
                    var rows = await context.CanonicalRecordings.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "provider-track-identities":
                {
                    var rows = await context.ProviderTrackIdentities.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "provider-route-decisions":
                {
                    var rows = await context.ProviderRouteDecisions.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "provider-route-outcomes":
                {
                    var rows = await context.ProviderRouteOutcomes.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "playlist-links":
                {
                    var rows = await context.PlaylistLinks.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "playlist-source-snapshots":
                {
                    var rows = await context.PlaylistSourceSnapshots.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "playlist-source-entries":
                {
                    var rows = await context.PlaylistSourceEntries.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "playlist-sync-runs":
                {
                    var rows = await context.PlaylistSyncRuns.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "playlist-sync-entry-results":
                {
                    var rows = await context.PlaylistSyncEntryResults.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "playlist-target-memberships":
                {
                    var rows = await context.PlaylistTargetMemberships.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "intelligence-policies":
                {
                    var rows = await context.IntelligencePolicies.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "listening-signals":
                {
                    var rows = await context.ListeningSignals.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "listening-profiles":
                {
                    var rows = await context.ListeningProfiles.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "recommendation-runs":
                {
                    var rows = await context.RecommendationRuns.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "recommendation-candidates":
                {
                    var rows = await context.RecommendationCandidates.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "generated-sets":
                {
                    var rows = await context.GeneratedSets.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "generated-set-entries":
                {
                    var rows = await context.GeneratedSetEntries.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "metadata-enrichment-plans":
                {
                    var rows = await context.MetadataEnrichmentPlans.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "metadata-enrichment-applications":
                {
                    var rows = await context.MetadataEnrichmentApplications.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "favorite-events":
                {
                    var rows = await context.FavoriteEvents.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "favorite-actions":
                {
                    var rows = await context.FavoriteActions.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "favorite-states":
                {
                    var rows = await context.FavoriteStates.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "favorite-action-policies":
                {
                    var rows = await context.FavoriteActionPolicies.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "managed-files":
                {
                    var rows = await context.ManagedFiles.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "managed-file-references":
                {
                    var rows = await context.ManagedFileReferences.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "provider-download-workspaces":
                {
                    var rows = await context.ProviderDownloadWorkspaces.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "provider-download-artifacts":
                {
                    var rows = await context.ProviderDownloadArtifacts.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "playback-delivery-checkpoints":
                {
                    var rows = await context.PlaybackDeliveryCheckpoints.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "jobs":
                {
                    var rows = await context.Jobs.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "job-attempts":
                {
                    var rows = await context.JobAttempts.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "job-schedules":
                {
                    var rows = await context.JobSchedules.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "outbox":
                {
                    var rows = await context.OutboxMessages.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "health-samples":
                {
                    var rows = await context.ProviderHealthSamples.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "health-rollups":
                {
                    var rows = await context.ProviderHealthRollups.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "circuits":
                {
                    var rows = await context.ProviderCircuits.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "extension-registries":
                {
                    var rows = await context.ExtensionRegistries.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "extension-packages":
                {
                    var rows = await context.ExtensionPackages.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "extension-permission-reviews":
                {
                    var rows = await context.ExtensionPermissionReviews.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            case "extension-logs":
                {
                    var rows = await context.ExtensionLogs.AsNoTracking().ToListAsync(cancellationToken);
                    await WriteJsonAsync(archive, entry, rows, cancellationToken);
                    return rows.Count;
                }
            default:
                throw new SelectiveTransferValidationException(
                    $"Selective export does not recognize entry '{entry}'.");
        }
    }

    private async Task<int> ReadCategoryEntryAsync(
        AllstarrDbContext context,
        ZipArchive archive,
        string entry,
        CancellationToken cancellationToken)
    {
        var archiveEntry = archive.Entries
            .FirstOrDefault(e => e.FullName.Equals($"{entry}.json", StringComparison.Ordinal));
        if (archiveEntry == null)
        {
            return 0;
        }

        switch (entry)
        {
            case "tenants":
                {
                    var rows = await ReadJsonAsync<TenantRecord>(archiveEntry, cancellationToken);
                    context.Tenants.AddRange(rows);
                    return rows.Count;
                }
            case "tenant-runtime-settings":
                {
                    var rows = await ReadJsonAsync<TenantRuntimeSettingRecord>(archiveEntry, cancellationToken);
                    context.TenantRuntimeSettings.AddRange(rows);
                    return rows.Count;
                }
            case "users":
                {
                    var rows = await ReadJsonAsync<PlatformUserRecord>(archiveEntry, cancellationToken);
                    context.Users.AddRange(rows);
                    return rows.Count;
                }
            case "backend-identities":
                {
                    var rows = await ReadJsonAsync<BackendIdentityRecord>(archiveEntry, cancellationToken);
                    context.BackendIdentities.AddRange(rows);
                    return rows.Count;
                }
            case "provider-accounts":
                {
                    var rows = await ReadJsonAsync<ProviderAccountRecord>(archiveEntry, cancellationToken);
                    context.ProviderAccounts.AddRange(rows);
                    return rows.Count;
                }
            case "secret-references":
                {
                    var rows = await ReadJsonAsync<SecretReferenceRecord>(archiveEntry, cancellationToken);
                    context.SecretReferences.AddRange(rows);
                    return rows.Count;
                }
            case "secret-versions":
                {
                    var rows = await ReadJsonAsync<SecretVersionRecord>(archiveEntry, cancellationToken);
                    context.SecretVersions.AddRange(rows);
                    return rows.Count;
                }
            case "audit-events":
                {
                    var rows = await ReadJsonAsync<AuditEventRecord>(archiveEntry, cancellationToken);
                    context.AuditEvents.AddRange(rows);
                    return rows.Count;
                }
            case "library-tracks":
                {
                    var rows = await ReadJsonAsync<LibraryTrackRecord>(archiveEntry, cancellationToken);
                    context.LibraryTracks.AddRange(rows);
                    return rows.Count;
                }
            case "external-metadata-snapshots":
                {
                    var rows = await ReadJsonAsync<ExternalMetadataSnapshotRecord>(archiveEntry, cancellationToken);
                    context.ExternalMetadataSnapshots.AddRange(rows);
                    return rows.Count;
                }
            case "track-matches":
                {
                    var rows = await ReadJsonAsync<TrackMatchRecord>(archiveEntry, cancellationToken);
                    context.TrackMatches.AddRange(rows);
                    return rows.Count;
                }
            case "manual-track-overrides":
                {
                    var rows = await ReadJsonAsync<ManualTrackOverrideRecord>(archiveEntry, cancellationToken);
                    context.ManualTrackOverrides.AddRange(rows);
                    return rows.Count;
                }
            case "canonical-recordings":
                {
                    var rows = await ReadJsonAsync<CanonicalRecordingRecord>(archiveEntry, cancellationToken);
                    context.CanonicalRecordings.AddRange(rows);
                    return rows.Count;
                }
            case "provider-track-identities":
                {
                    var rows = await ReadJsonAsync<ProviderTrackIdentityRecord>(archiveEntry, cancellationToken);
                    context.ProviderTrackIdentities.AddRange(rows);
                    return rows.Count;
                }
            case "provider-route-decisions":
                {
                    var rows = await ReadJsonAsync<ProviderRouteDecisionEntity>(archiveEntry, cancellationToken);
                    context.ProviderRouteDecisions.AddRange(rows);
                    return rows.Count;
                }
            case "provider-route-outcomes":
                {
                    var rows = await ReadJsonAsync<ProviderRouteOutcomeEntity>(archiveEntry, cancellationToken);
                    context.ProviderRouteOutcomes.AddRange(rows);
                    return rows.Count;
                }
            case "playlist-links":
                {
                    var rows = await ReadJsonAsync<PlaylistLinkRecord>(archiveEntry, cancellationToken);
                    context.PlaylistLinks.AddRange(rows);
                    return rows.Count;
                }
            case "playlist-source-snapshots":
                {
                    var rows = await ReadJsonAsync<PlaylistSourceSnapshotRecord>(archiveEntry, cancellationToken);
                    context.PlaylistSourceSnapshots.AddRange(rows);
                    return rows.Count;
                }
            case "playlist-source-entries":
                {
                    var rows = await ReadJsonAsync<PlaylistSourceEntryRecord>(archiveEntry, cancellationToken);
                    context.PlaylistSourceEntries.AddRange(rows);
                    return rows.Count;
                }
            case "playlist-sync-runs":
                {
                    var rows = await ReadJsonAsync<PlaylistSyncRunRecord>(archiveEntry, cancellationToken);
                    context.PlaylistSyncRuns.AddRange(rows);
                    return rows.Count;
                }
            case "playlist-sync-entry-results":
                {
                    var rows = await ReadJsonAsync<PlaylistSyncEntryResultRecord>(archiveEntry, cancellationToken);
                    context.PlaylistSyncEntryResults.AddRange(rows);
                    return rows.Count;
                }
            case "playlist-target-memberships":
                {
                    var rows = await ReadJsonAsync<PlaylistTargetMembershipRecord>(archiveEntry, cancellationToken);
                    context.PlaylistTargetMemberships.AddRange(rows);
                    return rows.Count;
                }
            case "intelligence-policies":
                {
                    var rows = await ReadJsonAsync<IntelligencePolicyRecord>(archiveEntry, cancellationToken);
                    context.IntelligencePolicies.AddRange(rows);
                    return rows.Count;
                }
            case "listening-signals":
                {
                    var rows = await ReadJsonAsync<ListeningSignalRecord>(archiveEntry, cancellationToken);
                    context.ListeningSignals.AddRange(rows);
                    return rows.Count;
                }
            case "listening-profiles":
                {
                    var rows = await ReadJsonAsync<ListeningProfileRecord>(archiveEntry, cancellationToken);
                    context.ListeningProfiles.AddRange(rows);
                    return rows.Count;
                }
            case "recommendation-runs":
                {
                    var rows = await ReadJsonAsync<RecommendationRunRecord>(archiveEntry, cancellationToken);
                    context.RecommendationRuns.AddRange(rows);
                    return rows.Count;
                }
            case "recommendation-candidates":
                {
                    var rows = await ReadJsonAsync<RecommendationCandidateRecord>(archiveEntry, cancellationToken);
                    context.RecommendationCandidates.AddRange(rows);
                    return rows.Count;
                }
            case "generated-sets":
                {
                    var rows = await ReadJsonAsync<GeneratedSetRecord>(archiveEntry, cancellationToken);
                    context.GeneratedSets.AddRange(rows);
                    return rows.Count;
                }
            case "generated-set-entries":
                {
                    var rows = await ReadJsonAsync<GeneratedSetEntryRecord>(archiveEntry, cancellationToken);
                    context.GeneratedSetEntries.AddRange(rows);
                    return rows.Count;
                }
            case "metadata-enrichment-plans":
                {
                    var rows = await ReadJsonAsync<MetadataEnrichmentPlanRecord>(archiveEntry, cancellationToken);
                    context.MetadataEnrichmentPlans.AddRange(rows);
                    return rows.Count;
                }
            case "metadata-enrichment-applications":
                {
                    var rows = await ReadJsonAsync<MetadataEnrichmentApplicationRecord>(archiveEntry, cancellationToken);
                    context.MetadataEnrichmentApplications.AddRange(rows);
                    return rows.Count;
                }
            case "favorite-events":
                {
                    var rows = await ReadJsonAsync<FavoriteEventRecord>(archiveEntry, cancellationToken);
                    context.FavoriteEvents.AddRange(rows);
                    return rows.Count;
                }
            case "favorite-actions":
                {
                    var rows = await ReadJsonAsync<FavoriteActionRecord>(archiveEntry, cancellationToken);
                    context.FavoriteActions.AddRange(rows);
                    return rows.Count;
                }
            case "favorite-states":
                {
                    var rows = await ReadJsonAsync<FavoriteStateRecord>(archiveEntry, cancellationToken);
                    context.FavoriteStates.AddRange(rows);
                    return rows.Count;
                }
            case "favorite-action-policies":
                {
                    var rows = await ReadJsonAsync<FavoriteActionPolicyRecord>(archiveEntry, cancellationToken);
                    context.FavoriteActionPolicies.AddRange(rows);
                    return rows.Count;
                }
            case "managed-files":
                {
                    var rows = await ReadJsonAsync<ManagedFileOwnershipEntity>(archiveEntry, cancellationToken);
                    context.ManagedFiles.AddRange(rows);
                    return rows.Count;
                }
            case "managed-file-references":
                {
                    var rows = await ReadJsonAsync<ManagedFileReferenceEntity>(archiveEntry, cancellationToken);
                    context.ManagedFileReferences.AddRange(rows);
                    return rows.Count;
                }
            case "provider-download-workspaces":
                {
                    var rows = await ReadJsonAsync<ProviderDownloadWorkspaceEntity>(archiveEntry, cancellationToken);
                    context.ProviderDownloadWorkspaces.AddRange(rows);
                    return rows.Count;
                }
            case "provider-download-artifacts":
                {
                    var rows = await ReadJsonAsync<ProviderDownloadArtifactEntity>(archiveEntry, cancellationToken);
                    context.ProviderDownloadArtifacts.AddRange(rows);
                    return rows.Count;
                }
            case "playback-delivery-checkpoints":
                {
                    var rows = await ReadJsonAsync<PlaybackDeliveryCheckpointEntity>(archiveEntry, cancellationToken);
                    context.PlaybackDeliveryCheckpoints.AddRange(rows);
                    return rows.Count;
                }
            case "jobs":
                {
                    var rows = await ReadJsonAsync<DurableJobRecord>(archiveEntry, cancellationToken);
                    context.Jobs.AddRange(rows);
                    return rows.Count;
                }
            case "job-attempts":
                {
                    var rows = await ReadJsonAsync<JobAttemptRecord>(archiveEntry, cancellationToken);
                    context.JobAttempts.AddRange(rows);
                    return rows.Count;
                }
            case "job-schedules":
                {
                    var rows = await ReadJsonAsync<JobScheduleRecord>(archiveEntry, cancellationToken);
                    context.JobSchedules.AddRange(rows);
                    return rows.Count;
                }
            case "outbox":
                {
                    var rows = await ReadJsonAsync<OutboxMessageRecord>(archiveEntry, cancellationToken);
                    context.OutboxMessages.AddRange(rows);
                    return rows.Count;
                }
            case "health-samples":
                {
                    var rows = await ReadJsonAsync<ProviderHealthSampleRecord>(archiveEntry, cancellationToken);
                    context.ProviderHealthSamples.AddRange(rows);
                    return rows.Count;
                }
            case "health-rollups":
                {
                    var rows = await ReadJsonAsync<ProviderHealthRollupRecord>(archiveEntry, cancellationToken);
                    context.ProviderHealthRollups.AddRange(rows);
                    return rows.Count;
                }
            case "circuits":
                {
                    var rows = await ReadJsonAsync<ProviderCircuitRecord>(archiveEntry, cancellationToken);
                    context.ProviderCircuits.AddRange(rows);
                    return rows.Count;
                }
            case "extension-registries":
                {
                    var rows = await ReadJsonAsync<ExtensionRegistryRecord>(archiveEntry, cancellationToken);
                    context.ExtensionRegistries.AddRange(rows);
                    return rows.Count;
                }
            case "extension-packages":
                {
                    var rows = await ReadJsonAsync<ExtensionPackageRecord>(archiveEntry, cancellationToken);
                    context.ExtensionPackages.AddRange(rows);
                    return rows.Count;
                }
            case "extension-permission-reviews":
                {
                    var rows = await ReadJsonAsync<ExtensionPermissionReviewRecord>(archiveEntry, cancellationToken);
                    context.ExtensionPermissionReviews.AddRange(rows);
                    return rows.Count;
                }
            case "extension-logs":
                {
                    var rows = await ReadJsonAsync<ExtensionLogRecord>(archiveEntry, cancellationToken);
                    context.ExtensionLogs.AddRange(rows);
                    return rows.Count;
                }
            default:
                throw new SelectiveTransferValidationException(
                    $"Selective import does not recognize entry '{entry}'.");
        }
    }

    private static async Task WriteJsonAsync<T>(
        ZipArchive archive,
        string entry,
        IReadOnlyCollection<T> values,
        CancellationToken cancellationToken)
    {
        var zipEntry = archive.CreateEntry($"{entry}.json", CompressionLevel.Optimal);
        await using var stream = zipEntry.Open();
        await JsonSerializer.SerializeAsync(stream, values, JsonOptions, cancellationToken);
    }

    private static async Task<List<T>> ReadJsonAsync<T>(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        try
        {
            var result = await JsonSerializer.DeserializeAsync<List<T>>(stream, JsonOptions, cancellationToken);
            return result ?? new List<T>();
        }
        catch (JsonException ex)
        {
            throw new SelectiveTransferValidationException(
                $"Selective archive entry '{entry.FullName}' contains invalid JSON: {ex.Message}");
        }
    }

    private static async Task<string> MaterializeArchiveAsync(
        string backupJson,
        CancellationToken cancellationToken)
    {
        if (TryDecodeBase64Archive(backupJson, out var base64Path))
        {
            return base64Path;
        }

        try
        {
            using var document = JsonDocument.Parse(backupJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("archiveBase64", out var base64Element) &&
                base64Element.ValueKind == JsonValueKind.String)
            {
                var bytes = Convert.FromBase64String(base64Element.GetString()!);
                var path = Path.Combine(
                    Path.GetTempPath(),
                    $"allstarr-selective-{Guid.NewGuid():N}.zip");
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                return path;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("path", out var pathElement) &&
                pathElement.ValueKind == JsonValueKind.String)
            {
                return pathElement.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        var rawPath = Path.Combine(
            Path.GetTempPath(),
            $"allstarr-selective-{Guid.NewGuid():N}.zip");
        await File.WriteAllTextAsync(rawPath, backupJson, cancellationToken);
        return rawPath;
    }

    private static bool TryDecodeBase64Archive(string value, out string path)
    {
        path = string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length < 64 || trimmed.Length % 4 != 0)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[trimmed.Length];
        if (!Convert.TryFromBase64String(trimmed, buffer, out var written))
        {
            return false;
        }

        if (written < 4 || buffer[0] != 0x50 || buffer[1] != 0x4B)
        {
            return false;
        }

        path = Path.Combine(Path.GetTempPath(), $"allstarr-selective-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, buffer[..written].ToArray());
        return true;
    }

    private static async Task WriteManifestAsync(
        ZipArchive archive,
        SelectiveManifest manifest,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, new[] { manifest }, JsonOptions, cancellationToken);
    }

    private static async Task<SelectiveManifest> ReadManifestAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals("manifest.json", StringComparison.Ordinal))
            ?? throw new SelectiveTransferValidationException("Archive is missing a manifest entry.");
        await using var stream = entry.Open();
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1)
            {
                throw new SelectiveTransferValidationException("Manifest must contain exactly one object.");
            }

            var value = root[0];
            return new SelectiveManifest
            {
                FormatVersion = value.GetProperty("formatVersion").GetInt32(),
                IsFullExport = value.TryGetProperty("isFullExport", out var fullElement) && fullElement.GetBoolean(),
                SourceProvider = value.GetProperty("sourceProvider").GetString() ?? string.Empty,
                SchemaVersion = value.GetProperty("schemaVersion").GetString() ?? string.Empty,
                ApplicationVersion = value.GetProperty("applicationVersion").GetString() ?? string.Empty,
                CreatedAt = value.GetProperty("createdAt").GetDateTimeOffset(),
                SecretKeyMaterialIncluded = value.TryGetProperty("secretKeyMaterialIncluded", out var sec) && sec.GetBoolean(),
                IncludedCategories = value.TryGetProperty("includedCategories", out var cat) && cat.ValueKind == JsonValueKind.Array
                    ? cat.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
                    : Array.Empty<string>()
            };
        }
        catch (JsonException ex)
        {
            throw new SelectiveTransferValidationException($"Manifest JSON is invalid: {ex.Message}");
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class SelectiveManifest
    {
        public int FormatVersion { get; set; }
        public bool IsFullExport { get; set; }
        public string SourceProvider { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public string ApplicationVersion { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public bool SecretKeyMaterialIncluded { get; set; }
        public IReadOnlyList<string> IncludedCategories { get; set; } = Array.Empty<string>();
    }
}
