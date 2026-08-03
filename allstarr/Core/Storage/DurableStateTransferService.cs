using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Configuration;
using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Downloads;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Playback;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed record DurableStateTransferArtifact(
    string Path,
    string Sha256,
    string SourceProvider,
    string SchemaVersion,
    DateTimeOffset CreatedAt);

public sealed class DurableStateTransferService
{
    private const int CurrentFormatVersion = 7;
    private const long MaximumManifestBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly HashSet<string> ExpectedEntryNames = new(StringComparer.Ordinal)
    {
        "manifest.json",
        "tenants.json",
        "tenant-runtime-settings.json",
        "users.json",
        "backend-identities.json",
        "onboarding-states.json",
        "secret-references.json",
        "secret-versions.json",
        "provider-accounts.json",
        "canonical-recordings.json",
        "provider-track-identities.json",
        "provider-route-decisions.json",
        "provider-route-outcomes.json",
        "extension-registries.json",
        "extension-packages.json",
        "extension-permission-reviews.json",
        "extension-logs.json",
        "favorite-events.json",
        "favorite-actions.json",
        "favorite-states.json",
        "favorite-action-policies.json",
        "managed-files.json",
        "managed-file-references.json",
        "provider-download-workspaces.json",
        "provider-download-artifacts.json",
        "metadata-enrichment-plans.json",
        "metadata-enrichment-applications.json",
        "intelligence-policies.json",
        "listening-intake-tokens.json",
        "listening-events.json",
        "listening-history-imports.json",
        "listening-signals.json",
        "playback-delivery-checkpoints.json",
        "listening-profiles.json",
        "recommendation-runs.json",
        "recommendation-candidates.json",
        "recommendation-feedback.json",
        "generated-sets.json",
        "generated-set-entries.json",
        "library-tracks.json",
        "external-metadata-snapshots.json",
        "track-matches.json",
        "manual-track-overrides.json",
        "job-schedules.json",
        "playlist-links.json",
        "playlist-source-snapshots.json",
        "playlist-source-entries.json",
        "playlist-sync-runs.json",
        "playlist-sync-entry-results.json",
        "playlist-target-memberships.json",
        "jobs.json",
        "job-attempts.json",
        "outbox.json",
        "health-samples.json",
        "health-rollups.json",
        "circuits.json",
        "audit-events.json",
        "legacy-env-imports.json",
        "backups.json"
    };

    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableStorageOptions _options;
    private readonly DurableStorageState _storageState;

    public DurableStateTransferService(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageOptions options,
        DurableStorageState storageState)
    {
        _contextFactory = contextFactory;
        _options = options;
        _storageState = storageState;
    }

    public async Task<DurableStateTransferArtifact> ExportAsync(
        string destinationDirectory,
        bool writesQuiesced,
        CancellationToken cancellationToken = default)
    {
        if (!writesQuiesced)
        {
            throw new InvalidOperationException(
                "A provider migration export requires confirmed write quiescence.");
        }

        var snapshot = _storageState.GetSnapshot();
        if (snapshot.Readiness != DurableStorageReadiness.Ready)
        {
            throw new InvalidOperationException("Durable storage must be ready before export.");
        }

        var directory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(directory);
        var createdAt = DateTimeOffset.UtcNow;
        var path = Path.Combine(
            directory,
            $"allstarr-state-{createdAt:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}.zip");
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var compatibility = await DurableSchemaCompatibility.InspectAsync(context, cancellationToken);
        if (!compatibility.IsCurrent)
        {
            throw new InvalidOperationException(
                "Durable storage schema must match this Allstarr build before export.");
        }

        var schemaVersion = compatibility.CurrentSchemaVersion;
        await using (var stream = new FileStream(
                         path,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None,
                         81920,
                         FileOptions.Asynchronous))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifest = new TransferManifest
            {
                FormatVersion = CurrentFormatVersion,
                SourceProvider = _options.ParseProvider().ToString(),
                SchemaVersion = schemaVersion,
                ApplicationVersion = AppVersion.Version,
                CreatedAt = createdAt,
                SecretKeyMaterialIncluded = false
            };
            await WriteEntryAsync(archive, "manifest.json", [manifest], cancellationToken);
            await WriteEntryAsync(archive, "tenants.json", await context.Tenants.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "tenant-runtime-settings.json", await context.TenantRuntimeSettings.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "users.json", await context.Users.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "backend-identities.json", await context.BackendIdentities.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "onboarding-states.json", await context.OnboardingStates.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "secret-references.json", await context.SecretReferences.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "secret-versions.json", await context.SecretVersions.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "provider-accounts.json", await context.ProviderAccounts.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "canonical-recordings.json", await context.CanonicalRecordings.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "provider-track-identities.json", await context.ProviderTrackIdentities.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "provider-route-decisions.json", await context.ProviderRouteDecisions.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "provider-route-outcomes.json", await context.ProviderRouteOutcomes.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "extension-registries.json", await context.ExtensionRegistries.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "extension-packages.json", await context.ExtensionPackages.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "extension-permission-reviews.json", await context.ExtensionPermissionReviews.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "extension-logs.json", await context.ExtensionLogs.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "favorite-events.json", await context.FavoriteEvents.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "favorite-actions.json", await context.FavoriteActions.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "favorite-states.json", await context.FavoriteStates.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "favorite-action-policies.json", await context.FavoriteActionPolicies.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "managed-files.json", await context.ManagedFiles.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "managed-file-references.json", await context.ManagedFileReferences.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "provider-download-workspaces.json", await context.ProviderDownloadWorkspaces.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "provider-download-artifacts.json", await context.ProviderDownloadArtifacts.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "metadata-enrichment-plans.json", await context.MetadataEnrichmentPlans.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "metadata-enrichment-applications.json", await context.MetadataEnrichmentApplications.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "intelligence-policies.json", await context.IntelligencePolicies.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "listening-intake-tokens.json", await context.ListeningIntakeTokens.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "listening-events.json", await context.ListeningEvents.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "listening-history-imports.json", await context.ListeningHistoryImports.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "listening-signals.json", await context.ListeningSignals.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "playback-delivery-checkpoints.json", await context.PlaybackDeliveryCheckpoints.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "listening-profiles.json", await context.ListeningProfiles.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "recommendation-runs.json", await context.RecommendationRuns.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "recommendation-candidates.json", await context.RecommendationCandidates.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "recommendation-feedback.json", await context.RecommendationFeedback.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "generated-sets.json", await context.GeneratedSets.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "generated-set-entries.json", await context.GeneratedSetEntries.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "library-tracks.json", await context.LibraryTracks.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "external-metadata-snapshots.json", await context.ExternalMetadataSnapshots.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "track-matches.json", await context.TrackMatches.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "manual-track-overrides.json", await context.ManualTrackOverrides.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "job-schedules.json", await context.JobSchedules.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "playlist-links.json", await context.PlaylistLinks.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "playlist-source-snapshots.json", await context.PlaylistSourceSnapshots.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "playlist-source-entries.json", await context.PlaylistSourceEntries.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "playlist-sync-runs.json", await context.PlaylistSyncRuns.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "playlist-sync-entry-results.json", await context.PlaylistSyncEntryResults.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "playlist-target-memberships.json", await context.PlaylistTargetMemberships.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "jobs.json", await context.Jobs.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "job-attempts.json", await context.JobAttempts.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "outbox.json", await context.OutboxMessages.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "health-samples.json", await context.ProviderHealthSamples.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "health-rollups.json", await context.ProviderHealthRollups.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "circuits.json", await context.ProviderCircuits.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "audit-events.json", await context.AuditEvents.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "legacy-env-imports.json", await context.LegacyEnvImports.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
            await WriteEntryAsync(archive, "backups.json", await context.Backups.AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
        }

        var hash = await ComputeSha256Async(path, cancellationToken);
        return new DurableStateTransferArtifact(
            path,
            hash,
            _options.ParseProvider().ToString(),
            schemaVersion,
            createdAt);
    }

    public static async Task<DurableStateTransferArtifact> LoadArtifactAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("State transfer artifact is missing.", fullPath);
        }

        var normalizedHash = NormalizeSha256(expectedSha256);
        var actualHash = await ComputeSha256Async(fullPath, cancellationToken);
        if (!actualHash.Equals(normalizedHash, StringComparison.Ordinal))
        {
            throw new BackupVerificationException("State transfer checksum verification failed.");
        }

        using var archive = ZipFile.OpenRead(fullPath);
        ValidateArchiveEntries(archive);
        var manifest = await ReadManifestAsync(archive, cancellationToken);
        return new DurableStateTransferArtifact(
            fullPath,
            normalizedHash,
            manifest.SourceProvider,
            manifest.SchemaVersion,
            manifest.CreatedAt);
    }

    public static async Task ImportAsync(
        DurableStateTransferArtifact artifact,
        IDbContextFactory<AllstarrDbContext> targetFactory,
        bool targetConfirmedEmpty,
        CancellationToken cancellationToken = default)
    {
        if (!targetConfirmedEmpty)
        {
            throw new InvalidOperationException(
                "State import requires explicit confirmation that the migrated target is empty.");
        }

        var verifiedArtifact = await LoadArtifactAsync(
            artifact.Path,
            artifact.Sha256,
            cancellationToken);
        ValidateManifestMatchesArtifact(verifiedArtifact, artifact);

        using var archive = ZipFile.OpenRead(verifiedArtifact.Path);
        ValidateArchiveEntries(archive);
        var manifest = await ReadManifestAsync(archive, cancellationToken);

        await using var context = await targetFactory.CreateDbContextAsync(cancellationToken);
        var knownMigrations = context.Database.GetMigrations().ToArray();
        if (knownMigrations.Length == 0 ||
            !manifest.SchemaVersion.Equals(knownMigrations[^1], StringComparison.Ordinal))
        {
            throw new BackupVerificationException(
                "State transfer schema does not match this Allstarr build.");
        }

        var beforeMigration = await DurableSchemaCompatibility.InspectAsync(context, cancellationToken);
        if (beforeMigration.Status == DurableSchemaCompatibilityStatus.UnsupportedVersion)
        {
            throw new BackupVerificationException(
                "State transfer target contains a schema unknown to this Allstarr build.");
        }

        await context.Database.MigrateAsync(cancellationToken);
        var afterMigration = await DurableSchemaCompatibility.InspectAsync(context, cancellationToken);
        if (!afterMigration.IsCurrent)
        {
            throw new BackupVerificationException(
                "State transfer target schema could not be verified after migration.");
        }

        if (await context.Tenants.AnyAsync(cancellationToken) ||
            await context.TenantRuntimeSettings.AnyAsync(cancellationToken) ||
            await context.Users.AnyAsync(cancellationToken) ||
            await context.BackendIdentities.AnyAsync(cancellationToken) ||
            await context.OnboardingStates.AnyAsync(cancellationToken) ||
            await context.ProviderAccounts.AnyAsync(cancellationToken) ||
            await context.CanonicalRecordings.AnyAsync(cancellationToken) ||
            await context.ProviderTrackIdentities.AnyAsync(cancellationToken) ||
            await context.ProviderRouteDecisions.AnyAsync(cancellationToken) ||
            await context.ProviderRouteOutcomes.AnyAsync(cancellationToken) ||
            await context.ExtensionRegistries.AnyAsync(cancellationToken) ||
            await context.ExtensionPackages.AnyAsync(cancellationToken) ||
            await context.ExtensionPermissionReviews.AnyAsync(cancellationToken) ||
            await context.ExtensionLogs.AnyAsync(cancellationToken) ||
            await context.FavoriteEvents.AnyAsync(cancellationToken) ||
            await context.FavoriteActions.AnyAsync(cancellationToken) ||
            await context.FavoriteStates.AnyAsync(cancellationToken) ||
            await context.FavoriteActionPolicies.AnyAsync(cancellationToken) ||
            await context.ManagedFiles.AnyAsync(cancellationToken) ||
            await context.ManagedFileReferences.AnyAsync(cancellationToken) ||
            await context.ProviderDownloadWorkspaces.AnyAsync(cancellationToken) ||
            await context.ProviderDownloadArtifacts.AnyAsync(cancellationToken) ||
            await context.MetadataEnrichmentPlans.AnyAsync(cancellationToken) ||
            await context.MetadataEnrichmentApplications.AnyAsync(cancellationToken) ||
            await context.IntelligencePolicies.AnyAsync(cancellationToken) ||
            await context.ListeningIntakeTokens.AnyAsync(cancellationToken) ||
            await context.ListeningEvents.AnyAsync(cancellationToken) ||
            await context.ListeningHistoryImports.AnyAsync(cancellationToken) ||
            await context.ListeningSignals.AnyAsync(cancellationToken) ||
            await context.PlaybackDeliveryCheckpoints.AnyAsync(cancellationToken) ||
            await context.ListeningProfiles.AnyAsync(cancellationToken) ||
            await context.RecommendationRuns.AnyAsync(cancellationToken) ||
            await context.RecommendationCandidates.AnyAsync(cancellationToken) ||
            await context.RecommendationFeedback.AnyAsync(cancellationToken) ||
            await context.GeneratedSets.AnyAsync(cancellationToken) ||
            await context.GeneratedSetEntries.AnyAsync(cancellationToken) ||
            await context.LibraryTracks.AnyAsync(cancellationToken) ||
            await context.ExternalMetadataSnapshots.AnyAsync(cancellationToken) ||
            await context.TrackMatches.AnyAsync(cancellationToken) ||
            await context.ManualTrackOverrides.AnyAsync(cancellationToken) ||
            await context.JobSchedules.AnyAsync(cancellationToken) ||
            await context.PlaylistLinks.AnyAsync(cancellationToken) ||
            await context.PlaylistSourceSnapshots.AnyAsync(cancellationToken) ||
            await context.PlaylistSourceEntries.AnyAsync(cancellationToken) ||
            await context.PlaylistSyncRuns.AnyAsync(cancellationToken) ||
            await context.PlaylistSyncEntryResults.AnyAsync(cancellationToken) ||
            await context.PlaylistTargetMemberships.AnyAsync(cancellationToken) ||
            await context.Jobs.AnyAsync(cancellationToken) ||
            await context.JobAttempts.AnyAsync(cancellationToken) ||
            await context.OutboxMessages.AnyAsync(cancellationToken) ||
            await context.SecretReferences.AnyAsync(cancellationToken) ||
            await context.SecretVersions.AnyAsync(cancellationToken) ||
            await context.ProviderHealthSamples.AnyAsync(cancellationToken) ||
            await context.ProviderHealthRollups.AnyAsync(cancellationToken) ||
            await context.ProviderCircuits.AnyAsync(cancellationToken) ||
            await context.AuditEvents.AnyAsync(cancellationToken) ||
            await context.LegacyEnvImports.AnyAsync(cancellationToken) ||
            await context.Backups.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException("State transfer target is not empty.");
        }

        var tenants = await ReadEntryAsync<TenantRecord>(archive, "tenants.json", cancellationToken);
        var runtimeSettings = await ReadEntryAsync<TenantRuntimeSettingRecord>(archive, "tenant-runtime-settings.json", cancellationToken);
        var users = await ReadEntryAsync<PlatformUserRecord>(archive, "users.json", cancellationToken);
        var onboardingStates = await ReadEntryAsync<OnboardingStateRecord>(archive, "onboarding-states.json", cancellationToken);
        var providerAccounts = await ReadEntryAsync<ProviderAccountRecord>(archive, "provider-accounts.json", cancellationToken);
        var secretReferences = await ReadEntryAsync<SecretReferenceRecord>(archive, "secret-references.json", cancellationToken);
        var canonicalRecordings = await ReadEntryAsync<CanonicalRecordingRecord>(archive, "canonical-recordings.json", cancellationToken);
        var providerTrackIdentities = await ReadEntryAsync<ProviderTrackIdentityRecord>(archive, "provider-track-identities.json", cancellationToken);
        var providerRouteDecisions = await ReadEntryAsync<ProviderRouteDecisionEntity>(archive, "provider-route-decisions.json", cancellationToken);
        var providerRouteOutcomes = await ReadEntryAsync<ProviderRouteOutcomeEntity>(archive, "provider-route-outcomes.json", cancellationToken);
        var jobs = await ReadEntryAsync<DurableJobRecord>(archive, "jobs.json", cancellationToken);
        var jobAttempts = await ReadEntryAsync<JobAttemptRecord>(archive, "job-attempts.json", cancellationToken);
        var jobSchedules = await ReadEntryAsync<JobScheduleRecord>(archive, "job-schedules.json", cancellationToken);
        var backendIdentities = await ReadEntryAsync<BackendIdentityRecord>(archive, "backend-identities.json", cancellationToken);
        var favoriteEvents = await ReadEntryAsync<FavoriteEventRecord>(archive, "favorite-events.json", cancellationToken);
        var favoriteActions = await ReadEntryAsync<FavoriteActionRecord>(archive, "favorite-actions.json", cancellationToken);
        var favoriteStates = await ReadEntryAsync<FavoriteStateRecord>(archive, "favorite-states.json", cancellationToken);
        var favoritePolicies = await ReadEntryAsync<FavoriteActionPolicyRecord>(archive, "favorite-action-policies.json", cancellationToken);
        var managedFiles = await ReadEntryAsync<ManagedFileOwnershipEntity>(archive, "managed-files.json", cancellationToken);
        var managedFileReferences = await ReadEntryAsync<ManagedFileReferenceEntity>(archive, "managed-file-references.json", cancellationToken);
        var downloadWorkspaces = await ReadEntryAsync<ProviderDownloadWorkspaceEntity>(archive, "provider-download-workspaces.json", cancellationToken);
        var downloadArtifacts = await ReadEntryAsync<ProviderDownloadArtifactEntity>(archive, "provider-download-artifacts.json", cancellationToken);
        var enrichmentPlans = await ReadEntryAsync<MetadataEnrichmentPlanRecord>(archive, "metadata-enrichment-plans.json", cancellationToken);
        var enrichmentApplications = await ReadEntryAsync<MetadataEnrichmentApplicationRecord>(archive, "metadata-enrichment-applications.json", cancellationToken);
        var intelligencePolicies = await ReadEntryAsync<IntelligencePolicyRecord>(archive, "intelligence-policies.json", cancellationToken);
        var listeningIntakeTokens = await ReadEntryAsync<ListeningIntakeTokenRecord>(archive, "listening-intake-tokens.json", cancellationToken);
        var listeningEvents = await ReadEntryAsync<ListeningEventRecord>(archive, "listening-events.json", cancellationToken);
        var listeningHistoryImports = await ReadEntryAsync<ListeningHistoryImportRecord>(archive, "listening-history-imports.json", cancellationToken);
        var listeningSignals = await ReadEntryAsync<ListeningSignalRecord>(archive, "listening-signals.json", cancellationToken);
        var playbackDeliveryCheckpoints = await ReadEntryAsync<PlaybackDeliveryCheckpointEntity>(archive, "playback-delivery-checkpoints.json", cancellationToken);
        var listeningProfiles = await ReadEntryAsync<ListeningProfileRecord>(archive, "listening-profiles.json", cancellationToken);
        var recommendationRuns = await ReadEntryAsync<RecommendationRunRecord>(archive, "recommendation-runs.json", cancellationToken);
        var recommendationCandidates = await ReadEntryAsync<RecommendationCandidateRecord>(archive, "recommendation-candidates.json", cancellationToken);
        var recommendationFeedback = await ReadEntryAsync<RecommendationFeedbackRecord>(archive, "recommendation-feedback.json", cancellationToken);
        var generatedSets = await ReadEntryAsync<GeneratedSetRecord>(archive, "generated-sets.json", cancellationToken);
        var generatedSetEntries = await ReadEntryAsync<GeneratedSetEntryRecord>(archive, "generated-set-entries.json", cancellationToken);
        var libraryTracks = await ReadEntryAsync<LibraryTrackRecord>(archive, "library-tracks.json", cancellationToken);
        var auditEvents = await ReadEntryAsync<AuditEventRecord>(archive, "audit-events.json", cancellationToken);
        var legacyEnvImports = await ReadEntryAsync<LegacyEnvImportRecord>(archive, "legacy-env-imports.json", cancellationToken);
        ValidateTrackIdentityArchive(
            tenants,
            users,
            providerAccounts,
            canonicalRecordings,
            providerTrackIdentities);
        ValidateRuntimeSettingsArchive(tenants, users, runtimeSettings);
        ValidateOnboardingArchive(tenants, users, onboardingStates);
        ValidateProviderRouteArchive(
            tenants,
            users,
            jobs,
            providerAccounts,
            providerRouteDecisions,
            providerRouteOutcomes);
        ValidateLegacyEnvImportsArchive(tenants, users, auditEvents, legacyEnvImports);
        ValidatePhase6Archive(tenants, users, backendIdentities, jobs, secretReferences, favoriteEvents, favoriteActions, favoriteStates,
            favoritePolicies, managedFiles, managedFileReferences, enrichmentPlans, enrichmentApplications);
        ValidateDownloadArtifactArchive(tenants, users, jobs, providerAccounts, managedFiles, downloadWorkspaces, downloadArtifacts);
        ValidateIntelligenceArchive(tenants, users, backendIdentities, jobs, jobSchedules, secretReferences,
            providerAccounts, providerTrackIdentities, canonicalRecordings, libraryTracks, intelligencePolicies,
            listeningIntakeTokens, listeningEvents, listeningHistoryImports, listeningSignals,
            playbackDeliveryCheckpoints,
            listeningProfiles, recommendationRuns, recommendationCandidates, recommendationFeedback,
            generatedSets, generatedSetEntries);
        var unrestorableImportJobs = ListeningHistoryImportStateTransfer.ExpireActiveImports(listeningHistoryImports);
        var restoredAt = DateTimeOffset.UtcNow;
        ListeningHistoryImportStateTransfer.CancelJobs(jobs, unrestorableImportJobs, restoredAt);
        ListeningHistoryImportStateTransfer.CancelAttempts(jobAttempts, unrestorableImportJobs, restoredAt);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        context.Tenants.AddRange(tenants);
        context.Users.AddRange(users);
        context.OnboardingStates.AddRange(onboardingStates);
        context.TenantRuntimeSettings.AddRange(runtimeSettings);
        context.SecretReferences.AddRange(secretReferences);
        context.SecretVersions.AddRange(await ReadEntryAsync<SecretVersionRecord>(archive, "secret-versions.json", cancellationToken));
        context.ProviderAccounts.AddRange(providerAccounts);
        context.CanonicalRecordings.AddRange(canonicalRecordings);
        context.ProviderTrackIdentities.AddRange(providerTrackIdentities);
        context.ProviderRouteDecisions.AddRange(providerRouteDecisions);
        context.ProviderRouteOutcomes.AddRange(providerRouteOutcomes);
        context.ExtensionRegistries.AddRange(await ReadEntryAsync<ExtensionRegistryRecord>(archive, "extension-registries.json", cancellationToken));
        context.ExtensionPackages.AddRange(await ReadEntryAsync<ExtensionPackageRecord>(archive, "extension-packages.json", cancellationToken));
        context.ExtensionPermissionReviews.AddRange(await ReadEntryAsync<ExtensionPermissionReviewRecord>(archive, "extension-permission-reviews.json", cancellationToken));
        context.ExtensionLogs.AddRange(await ReadEntryAsync<ExtensionLogRecord>(archive, "extension-logs.json", cancellationToken));
        context.FavoriteEvents.AddRange(favoriteEvents);
        context.FavoriteActions.AddRange(favoriteActions);
        context.FavoriteStates.AddRange(favoriteStates);
        context.FavoriteActionPolicies.AddRange(favoritePolicies);
        context.ManagedFiles.AddRange(managedFiles);
        context.ManagedFileReferences.AddRange(managedFileReferences);
        context.ProviderDownloadWorkspaces.AddRange(downloadWorkspaces);
        context.ProviderDownloadArtifacts.AddRange(downloadArtifacts);
        context.MetadataEnrichmentPlans.AddRange(enrichmentPlans);
        context.MetadataEnrichmentApplications.AddRange(enrichmentApplications);
        context.IntelligencePolicies.AddRange(intelligencePolicies);
        context.ListeningIntakeTokens.AddRange(listeningIntakeTokens);
        context.ListeningEvents.AddRange(listeningEvents);
        context.ListeningHistoryImports.AddRange(listeningHistoryImports);
        context.ListeningSignals.AddRange(listeningSignals);
        context.PlaybackDeliveryCheckpoints.AddRange(playbackDeliveryCheckpoints);
        context.ListeningProfiles.AddRange(listeningProfiles);
        context.RecommendationRuns.AddRange(recommendationRuns);
        context.RecommendationCandidates.AddRange(recommendationCandidates);
        context.RecommendationFeedback.AddRange(recommendationFeedback);
        context.GeneratedSets.AddRange(generatedSets);
        context.GeneratedSetEntries.AddRange(generatedSetEntries);
        context.BackendIdentities.AddRange(backendIdentities);
        context.Jobs.AddRange(jobs);
        context.JobAttempts.AddRange(jobAttempts);
        context.OutboxMessages.AddRange(await ReadEntryAsync<OutboxMessageRecord>(archive, "outbox.json", cancellationToken));
        context.ProviderHealthSamples.AddRange(await ReadEntryAsync<ProviderHealthSampleRecord>(archive, "health-samples.json", cancellationToken));
        context.ProviderHealthRollups.AddRange(await ReadEntryAsync<ProviderHealthRollupRecord>(archive, "health-rollups.json", cancellationToken));
        context.ProviderCircuits.AddRange(await ReadEntryAsync<ProviderCircuitRecord>(archive, "circuits.json", cancellationToken));
        context.AuditEvents.AddRange(auditEvents);
        context.LegacyEnvImports.AddRange(legacyEnvImports);
        context.Backups.AddRange(await ReadEntryAsync<BackupRecord>(archive, "backups.json", cancellationToken));
        context.LibraryTracks.AddRange(libraryTracks);
        context.ExternalMetadataSnapshots.AddRange(await ReadEntryAsync<ExternalMetadataSnapshotRecord>(archive, "external-metadata-snapshots.json", cancellationToken));
        context.TrackMatches.AddRange(await ReadEntryAsync<TrackMatchRecord>(archive, "track-matches.json", cancellationToken));
        context.ManualTrackOverrides.AddRange(await ReadEntryAsync<ManualTrackOverrideRecord>(archive, "manual-track-overrides.json", cancellationToken));
        context.JobSchedules.AddRange(jobSchedules);
        context.PlaylistLinks.AddRange(await ReadEntryAsync<PlaylistLinkRecord>(archive, "playlist-links.json", cancellationToken));
        context.PlaylistSourceSnapshots.AddRange(await ReadEntryAsync<PlaylistSourceSnapshotRecord>(archive, "playlist-source-snapshots.json", cancellationToken));
        context.PlaylistSourceEntries.AddRange(await ReadEntryAsync<PlaylistSourceEntryRecord>(archive, "playlist-source-entries.json", cancellationToken));
        context.PlaylistSyncRuns.AddRange(await ReadEntryAsync<PlaylistSyncRunRecord>(archive, "playlist-sync-runs.json", cancellationToken));
        context.PlaylistSyncEntryResults.AddRange(await ReadEntryAsync<PlaylistSyncEntryResultRecord>(archive, "playlist-sync-entry-results.json", cancellationToken));
        context.PlaylistTargetMemberships.AddRange(await ReadEntryAsync<PlaylistTargetMembershipRecord>(archive, "playlist-target-memberships.json", cancellationToken));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void ValidateOnboardingArchive(
        IReadOnlyCollection<TenantRecord> tenants,
        IReadOnlyCollection<PlatformUserRecord> users,
        IReadOnlyCollection<OnboardingStateRecord> states)
    {
        var tenantIds = tenants.Select(item => item.Id).ToHashSet();
        var usersById = users.ToDictionary(item => item.Id);
        var ids = new HashSet<Guid>();
        var scopes = new HashSet<(Guid TenantId, Guid UserId)>();
        foreach (var state in states)
        {
            string[]? steps = null;
            try
            {
                steps = JsonSerializer.Deserialize<string[]>(
                    state.CompletedStepsJson,
                    JsonOptions);
            }
            catch (JsonException)
            {
            }

            if (state.Id == Guid.Empty ||
                !ids.Add(state.Id) ||
                !tenantIds.Contains(state.TenantId) ||
                !usersById.TryGetValue(state.UserId, out var user) ||
                user.TenantId != state.TenantId ||
                !scopes.Add((state.TenantId, state.UserId)) ||
                state.SchemaVersion != OnboardingStateService.SchemaVersion ||
                !IsRequiredText(state.CompletionSource, 100) ||
                steps == null ||
                steps.Length != steps.Distinct(StringComparer.Ordinal).Count() ||
                steps.Any(step => step is not OnboardingStateService.BackendIdentityStep and
                    not OnboardingStateService.LegacyEnvironmentStep) ||
                state.CreatedAt == default ||
                state.UpdatedAt < state.CreatedAt ||
                state.Revision < 1)
            {
                throw new BackupVerificationException(
                    "State transfer contains malformed or cross-tenant onboarding state.");
            }
        }
    }

    private static void ValidateRuntimeSettingsArchive(
        IReadOnlyCollection<TenantRecord> tenants,
        IReadOnlyCollection<PlatformUserRecord> users,
        IReadOnlyCollection<TenantRuntimeSettingRecord> settings)
    {
        var tenantIds = tenants.Select(item => item.Id).ToHashSet();
        var usersById = users.ToDictionary(item => item.Id);
        var keys = new HashSet<(Guid TenantId, string Key)>();
        foreach (var setting in settings)
        {
            if (!tenantIds.Contains(setting.TenantId) || setting.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(setting.Source) || setting.Source.Length > 100 ||
                setting.Revision <= 0 || setting.CreatedAt == default || setting.UpdatedAt < setting.CreatedAt ||
                !keys.Add((setting.TenantId, setting.Key.ToUpperInvariant())) ||
                setting.UpdatedByUserId is { } actorId &&
                (!usersById.TryGetValue(actorId, out var actor) || actor.TenantId != setting.TenantId))
            {
                throw new BackupVerificationException(
                    "State transfer contains a malformed or cross-tenant runtime setting.");
            }

            try { DurableRuntimeSettingsService.ValidateStoredRecord(setting); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
            {
                throw new BackupVerificationException(
                    $"State transfer contains an unknown or invalid runtime setting: {ex.Message}");
            }
        }
    }

    private static void ValidateProviderRouteArchive(
        IReadOnlyCollection<TenantRecord> tenants,
        IReadOnlyCollection<PlatformUserRecord> users,
        IReadOnlyCollection<DurableJobRecord> jobs,
        IReadOnlyCollection<ProviderAccountRecord> accounts,
        IReadOnlyCollection<ProviderRouteDecisionEntity> decisions,
        IReadOnlyCollection<ProviderRouteOutcomeEntity> outcomes)
    {
        var tenantIds = tenants.Select(item => item.Id).ToHashSet();
        var usersById = users.ToDictionary(item => item.Id);
        var jobsById = jobs.ToDictionary(item => item.Id);
        var accountsById = accounts.ToDictionary(item => item.Id);
        var decisionsById = IndexUnique(decisions, item => item.Id, "provider route decision");
        IndexUnique(outcomes, item => item.Id, "provider route outcome");
        var routeKeys = new HashSet<(Guid TenantId, string RouteKey)>();

        bool ValidAccount(
            Guid tenantId,
            Guid? actorUserId,
            string providerId,
            string? libraryScopeId,
            Guid? accountId)
        {
            if (accountId == null) return true;
            if (!accountsById.TryGetValue(accountId.Value, out var account) || account.ProviderId != providerId)
                return false;
            return account.Scope switch
            {
                ProviderAccountScope.Global =>
                    account.TenantId == null && account.OwnerUserId == null && account.LibraryScopeId == null,
                ProviderAccountScope.User => actorUserId != null && account.TenantId == tenantId &&
                                             account.OwnerUserId == actorUserId,
                ProviderAccountScope.Library =>
                    account.TenantId == tenantId && account.OwnerUserId == null &&
                    account.LibraryScopeId == libraryScopeId,
                _ => false
            };
        }

        foreach (var decision in decisions)
        {
            ProviderRouteCandidateDecision[]? candidates;
            try
            {
                candidates = JsonSerializer.Deserialize<ProviderRouteCandidateDecision[]>(
                    decision.CandidateDecisionsJson,
                    JsonOptions);
            }
            catch (JsonException)
            {
                candidates = null;
            }

            var actorValid = decision.ActorUserId == null ||
                usersById.TryGetValue(decision.ActorUserId.Value, out var actor) && actor.TenantId == decision.TenantId;
            var jobValid = decision.DurableJobId == null ||
                jobsById.TryGetValue(decision.DurableJobId.Value, out var job) &&
                job.TenantId == decision.TenantId && job.OwnerUserId == decision.ActorUserId;
            var selectedShapeValid = decision.SelectedProviderId == null
                ? decision.SelectedProviderAccountId == null
                : IsNormalizedProviderId(decision.SelectedProviderId) &&
                  ValidAccount(
                      decision.TenantId,
                      decision.ActorUserId,
                      decision.SelectedProviderId,
                      decision.LibraryScopeId,
                      decision.SelectedProviderAccountId);
            var candidatesValid = candidates is { Length: <= 256 } && candidates.All(candidate =>
                IsNormalizedProviderId(candidate.ProviderId) &&
                Enum.IsDefined(candidate.Status) &&
                IsRouteCode(candidate.ReasonCode) &&
                candidate.Priority >= 0 &&
                ValidAccount(
                    decision.TenantId,
                    decision.ActorUserId,
                    candidate.ProviderId,
                    decision.LibraryScopeId,
                    candidate.ProviderAccountId));
            var selectedCandidateValid = decision.SelectedProviderId == null
                ? candidates?.All(item => item.Status == ProviderRouteDecisionStatus.Rejected) == true
                : candidates?.Any(item =>
                    item.ProviderId == decision.SelectedProviderId &&
                    item.ProviderAccountId == decision.SelectedProviderAccountId &&
                    item.Status == ProviderRouteDecisionStatus.Accepted) == true;

            if (!tenantIds.Contains(decision.TenantId) || !actorValid || !jobValid || !selectedShapeValid ||
                !candidatesValid || !selectedCandidateValid ||
                !IsNormalizedSha256(decision.RouteKey) ||
                !routeKeys.Add((decision.TenantId, decision.RouteKey)) ||
                !IsRequiredText(decision.OperationId, 100) ||
                !IsRequiredText(decision.CorrelationId, 100) ||
                !Enum.IsDefined(decision.Capability) ||
                !IsOptionalText(decision.LibraryScopeId, 300) ||
                decision.CreatedAt == default)
                RejectProviderRouteArchive("a route decision is malformed, repeated, or crosses its tenant, actor, job, library, or provider-account scope");
        }

        var outcomeKeys = new HashSet<(Guid RouteDecisionId, string OutcomeKey)>();
        foreach (var outcome in outcomes)
        {
            var routeValid = decisionsById.TryGetValue(outcome.RouteDecisionId, out var route) &&
                route.TenantId == outcome.TenantId;
            var accountValid = outcome.ProviderId == null
                ? outcome.ProviderAccountId == null
                : IsNormalizedProviderId(outcome.ProviderId) && route != null &&
                  ValidAccount(
                      outcome.TenantId,
                      route.ActorUserId,
                      outcome.ProviderId,
                      route.LibraryScopeId,
                      outcome.ProviderAccountId);
            var lifecycleValid = outcome.Status switch
            {
                ProviderRouteOutcomeStatus.FallbackAdvanced =>
                    outcome.ProviderId != null && outcome.NextProviderId != null &&
                    outcome.ProviderId != outcome.NextProviderId,
                ProviderRouteOutcomeStatus.Stopped or ProviderRouteOutcomeStatus.Succeeded =>
                    outcome.NextProviderId == null,
                _ => false
            };
            if (!routeValid || !accountValid || !lifecycleValid ||
                !IsNormalizedSha256(outcome.OutcomeKey) ||
                !outcomeKeys.Add((outcome.RouteDecisionId, outcome.OutcomeKey)) ||
                outcome.Sequence < 0 ||
                !IsRouteStage(outcome.Stage) ||
                !IsRouteCode(outcome.ReasonCode) ||
                !IsOptionalNormalizedProviderId(outcome.NextProviderId) ||
                outcome.CreatedAt == default || outcome.CreatedAt < route!.CreatedAt)
                RejectProviderRouteArchive("a route outcome is malformed, repeated, or crosses its route, tenant, provider-account, or fallback scope");
        }
    }

    private static bool IsRouteStage(string value) =>
        IsRequiredText(value, 50) && value.All(ch => ch is >= 'a' and <= 'z' || char.IsAsciiDigit(ch) || ch == '-');

    private static bool IsRouteCode(string value) =>
        IsRequiredText(value, 100) && value.All(ch => ch is >= 'a' and <= 'z' || char.IsAsciiDigit(ch) || ch is '-' or '_' or '.');

    private static bool IsOptionalNormalizedProviderId(string? value) =>
        value == null || IsNormalizedProviderId(value);

    private static void RejectProviderRouteArchive(string reason) =>
        throw new BackupVerificationException($"State transfer provider route data is invalid because {reason}.");

    private static void ValidateLegacyEnvImportsArchive(
        IReadOnlyCollection<TenantRecord> tenants,
        IReadOnlyCollection<PlatformUserRecord> users,
        IReadOnlyCollection<AuditEventRecord> audits,
        IReadOnlyCollection<LegacyEnvImportRecord> imports)
    {
        var tenantIds = tenants.Select(item => item.Id).ToHashSet();
        var usersById = users.ToDictionary(item => item.Id);
        var auditsById = audits.ToDictionary(item => item.Id);
        var sources = new HashSet<(Guid TenantId, string SourceSha256, string SchemaVersion)>();
        foreach (var receipt in imports)
        {
            LegacyEnvMigrationApplyResult? result = null;
            var validProvenance = false;
            try { result = JsonSerializer.Deserialize<LegacyEnvMigrationApplyResult>(receipt.ResultJson, JsonOptions); }
            catch (JsonException) { }
            try
            {
                using var provenance = JsonDocument.Parse(receipt.ProvenanceJson);
                validProvenance = provenance.RootElement.ValueKind == JsonValueKind.Object &&
                                  provenance.RootElement.TryGetProperty("settings", out var settings) &&
                                  settings.ValueKind == JsonValueKind.Array &&
                                  provenance.RootElement.TryGetProperty("providerAccounts", out var accounts) &&
                                  accounts.ValueKind == JsonValueKind.Array;
            }
            catch (JsonException) { }
            var validAudit = auditsById.TryGetValue(receipt.AuditEventId, out var audit) &&
                             audit.TenantId == receipt.TenantId &&
                             audit.ActorUserId == receipt.ActorUserId &&
                             audit.Category == "configuration-migration" &&
                             audit.Action == "legacy-env.apply" && audit.Outcome == "succeeded";
            if (receipt.Id == Guid.Empty || !tenantIds.Contains(receipt.TenantId) ||
                !IsNormalizedSha256(receipt.SourceSha256) ||
                receipt.SchemaVersion is not ("legacy-env-import-v1" or LegacyEnvMigrationService.MigrationSchemaVersion) ||
                !validProvenance ||
                !sources.Add((receipt.TenantId, receipt.SourceSha256, receipt.SchemaVersion)) ||
                receipt.ActorUserId is { } actorId &&
                (!usersById.TryGetValue(actorId, out var actor) || actor.TenantId != receipt.TenantId) ||
                !validAudit || receipt.AppliedAt == default || result == null || !result.Success ||
                !string.Equals(result.SourceFingerprint, receipt.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new BackupVerificationException(
                    "State transfer contains a malformed or cross-tenant legacy environment import receipt.");
            }
        }
    }

    private static void ValidateTrackIdentityArchive(
        IReadOnlyCollection<TenantRecord> tenants,
        IReadOnlyCollection<PlatformUserRecord> users,
        IReadOnlyCollection<ProviderAccountRecord> providerAccounts,
        IReadOnlyCollection<CanonicalRecordingRecord> canonicalRecordings,
        IReadOnlyCollection<ProviderTrackIdentityRecord> providerTrackIdentities)
    {
        var tenantById = IndexUnique(tenants, item => item.Id, "tenant");
        var userById = IndexUnique(users, item => item.Id, "user");
        var accountById = IndexUnique(providerAccounts, item => item.Id, "provider account");
        var canonicalById = IndexUnique(canonicalRecordings, item => item.Id, "canonical recording");
        IndexUnique(providerTrackIdentities, item => item.Id, "provider track identity");

        foreach (var user in users)
        {
            if (!tenantById.ContainsKey(user.TenantId))
            {
                RejectIdentityArchive("a user references a missing tenant");
            }
        }

        foreach (var account in providerAccounts)
        {
            ValidateProviderAccount(account, tenantById, userById);
        }

        var isrcKeys = new HashSet<(Guid TenantId, string Value)>();
        var musicBrainzKeys = new HashSet<(Guid TenantId, string Value)>();
        foreach (var recording in canonicalRecordings)
        {
            if (!tenantById.ContainsKey(recording.TenantId) ||
                !userById.TryGetValue(recording.CreatedByUserId, out var creator) ||
                creator.TenantId != recording.TenantId)
            {
                RejectIdentityArchive("a canonical recording crosses its tenant or creator boundary");
            }

            if (recording.Isrc != null &&
                (!IsNormalizedIsrc(recording.Isrc) ||
                 !isrcKeys.Add((recording.TenantId, recording.Isrc))))
            {
                RejectIdentityArchive("a canonical recording contains a malformed or repeated ISRC");
            }

            if (recording.MusicBrainzRecordingId != null &&
                (!IsNormalizedMusicBrainzId(recording.MusicBrainzRecordingId) ||
                 !musicBrainzKeys.Add((recording.TenantId, recording.MusicBrainzRecordingId))))
            {
                RejectIdentityArchive(
                    "a canonical recording contains a malformed or repeated MusicBrainz recording ID");
            }
        }

        var catalogKeys = new HashSet<(Guid, string, ProviderResourceKind, string, string)>();
        var accountKeys = new HashSet<(Guid, string, ProviderResourceKind, string, Guid, string)>();
        foreach (var identity in providerTrackIdentities)
        {
            if (!tenantById.ContainsKey(identity.TenantId) ||
                !canonicalById.TryGetValue(identity.CanonicalRecordingId, out var canonical) ||
                canonical.TenantId != identity.TenantId)
            {
                RejectIdentityArchive(
                    "a provider track identity crosses its tenant or canonical recording boundary");
            }

            if (identity.ResourceKind != ProviderResourceKind.Track ||
                identity.Scope is not (ProviderIdentityScope.Catalog or ProviderIdentityScope.Account) ||
                identity.Verification is not
                    (ProviderIdentityVerification.Verified or ProviderIdentityVerification.Pinned) ||
                identity.DecisionVersion <= 0)
            {
                RejectIdentityArchive(
                    "a provider track identity contains an unsafe kind, scope, verification, or decision version");
            }

            if (!IsNormalizedProviderId(identity.ProviderId) ||
                !IsNormalizedCatalog(identity.CatalogNamespace) ||
                !IsRequiredText(identity.ExternalId, 500) ||
                !IsRequiredText(identity.VerificationMethod, 50))
            {
                RejectIdentityArchive("a provider track identity contains malformed text fields");
            }

            var expectedHash = HashExternalId(identity.ExternalId);
            if (!IsNormalizedSha256(identity.ExternalIdHash) ||
                !identity.ExternalIdHash.Equals(expectedHash, StringComparison.Ordinal))
            {
                RejectIdentityArchive(
                    "a provider track identity contains a malformed or mismatched external ID hash");
            }

            if (identity.Scope == ProviderIdentityScope.Catalog)
            {
                if (identity.ProviderAccountId != null ||
                    !catalogKeys.Add((
                        identity.TenantId,
                        identity.ProviderId,
                        identity.ResourceKind,
                        identity.CatalogNamespace,
                        identity.ExternalIdHash)))
                {
                    RejectIdentityArchive(
                        "a catalog identity has an account or repeats an exact catalog mapping");
                }

                continue;
            }

            if (identity.ProviderAccountId is not { } accountId ||
                !accountById.TryGetValue(accountId, out var account) ||
                !account.ProviderId.Equals(identity.ProviderId, StringComparison.Ordinal) ||
                (account.Scope != ProviderAccountScope.Global &&
                 account.TenantId != identity.TenantId) ||
                !accountKeys.Add((
                    identity.TenantId,
                    identity.ProviderId,
                    identity.ResourceKind,
                    identity.CatalogNamespace,
                    accountId,
                    identity.ExternalIdHash)))
            {
                RejectIdentityArchive(
                    "an account identity has an invalid tenant, provider, account, or exact mapping");
            }
        }
    }

    private static void ValidateProviderAccount(
        ProviderAccountRecord account,
        IReadOnlyDictionary<Guid, TenantRecord> tenantById,
        IReadOnlyDictionary<Guid, PlatformUserRecord> userById)
    {
        if (!IsNormalizedProviderId(account.ProviderId) ||
            !Enum.IsDefined(account.Scope))
        {
            RejectIdentityArchive("a provider account has an invalid provider or scope");
        }

        var validShape = account.Scope switch
        {
            ProviderAccountScope.Global =>
                account.TenantId == null &&
                account.OwnerUserId == null &&
                account.LibraryScopeId == null,
            ProviderAccountScope.User =>
                account.TenantId is { } tenantId &&
                account.OwnerUserId is { } ownerUserId &&
                account.LibraryScopeId == null &&
                tenantById.ContainsKey(tenantId) &&
                userById.TryGetValue(ownerUserId, out var owner) &&
                owner.TenantId == tenantId,
            ProviderAccountScope.Library =>
                account.TenantId is { } libraryTenantId &&
                account.OwnerUserId == null &&
                IsRequiredText(account.LibraryScopeId, 300) &&
                tenantById.ContainsKey(libraryTenantId),
            _ => false
        };
        if (!validShape)
        {
            RejectIdentityArchive("a provider account has an invalid owner or tenant shape");
        }
    }

    private static void ValidatePhase6Archive(
        IReadOnlyCollection<TenantRecord> tenants,
        IReadOnlyCollection<PlatformUserRecord> users,
        IReadOnlyCollection<BackendIdentityRecord> backendIdentities,
        IReadOnlyCollection<DurableJobRecord> jobs,
        IReadOnlyCollection<SecretReferenceRecord> secretReferences,
        IReadOnlyCollection<FavoriteEventRecord> favoriteEvents,
        IReadOnlyCollection<FavoriteActionRecord> favoriteActions,
        IReadOnlyCollection<FavoriteStateRecord> favoriteStates,
        IReadOnlyCollection<FavoriteActionPolicyRecord> favoritePolicies,
        IReadOnlyCollection<ManagedFileOwnershipEntity> managedFiles,
        IReadOnlyCollection<ManagedFileReferenceEntity> managedFileReferences,
        IReadOnlyCollection<MetadataEnrichmentPlanRecord> enrichmentPlans,
        IReadOnlyCollection<MetadataEnrichmentApplicationRecord> enrichmentApplications)
    {
        var tenantById = IndexUnique(tenants, item => item.Id, "tenant");
        var userById = IndexUnique(users, item => item.Id, "user");
        var jobById = IndexUnique(jobs, item => item.Id, "durable job");
        var secretById = IndexUnique(secretReferences, item => item.Id, "secret reference");
        var eventById = IndexUnique(favoriteEvents, item => item.Id, "favorite event");
        var fileById = IndexUnique(managedFiles, item => item.Id, "managed file");
        IndexUnique(managedFileReferences, item => item.Id, "managed file reference");
        var planById = IndexUnique(enrichmentPlans, item => item.Id, "metadata enrichment plan");
        IndexUnique(favoriteActions, item => item.Id, "favorite action");
        IndexUnique(favoriteStates, item => item.Id, "favorite state");
        IndexUnique(favoritePolicies, item => item.Id, "favorite action policy");
        IndexUnique(enrichmentApplications, item => item.Id, "metadata enrichment application");

        bool ValidOwner(Guid tenantId, Guid ownerUserId) => tenantById.ContainsKey(tenantId) &&
            userById.TryGetValue(ownerUserId, out var user) && user.TenantId == tenantId;
        bool ValidJob(Guid jobId, Guid tenantId, Guid? ownerUserId) => jobById.TryGetValue(jobId, out var job) &&
            job.TenantId == tenantId && job.OwnerUserId == ownerUserId;

        foreach (var favoriteEvent in favoriteEvents)
        {
            if (!ValidOwner(favoriteEvent.TenantId, favoriteEvent.OwnerUserId) ||
                !ValidJob(favoriteEvent.JobId, favoriteEvent.TenantId, favoriteEvent.OwnerUserId) ||
                favoriteEvent.Protocol is not ("jellyfin" or "subsonic") ||
                !Enum.IsDefined(favoriteEvent.Operation) || !Enum.IsDefined(favoriteEvent.State) ||
                !IsRequiredText(favoriteEvent.BackendInstanceId, 200) ||
                !IsRequiredText(favoriteEvent.BackendPrincipalId, 300) ||
                !IsOptionalText(favoriteEvent.LibraryScopeId, 300) ||
                !IsRequiredText(favoriteEvent.ItemId, 500) || !IsRequiredText(favoriteEvent.SourceRevision, 300) ||
                favoriteEvent.TargetCredentialReferenceId is { } eventCredential &&
                (!secretById.TryGetValue(eventCredential, out var eventSecret) || eventSecret.TenantId != favoriteEvent.TenantId || eventSecret.RevokedAt != null) ||
                !IsRequiredText(favoriteEvent.EventKey, 64) || !IsRequiredText(favoriteEvent.CorrelationId, 100) ||
                !IsJsonObject(favoriteEvent.PolicySnapshotJson, 64 * 1024))
                RejectPhase6Archive("a favorite event is malformed or crosses its tenant, owner, or job boundary");
        }

        foreach (var action in favoriteActions)
        {
            if (!eventById.TryGetValue(action.EventId, out var favoriteEvent) ||
                favoriteEvent.TenantId != action.TenantId || favoriteEvent.OwnerUserId != action.OwnerUserId ||
                !ValidOwner(action.TenantId, action.OwnerUserId) || !Enum.IsDefined(action.State) ||
                !IsRequiredText(action.ActionType, 100) || !IsRequiredText(action.IdempotencyKey, 300) || action.AttemptCount < 0)
                RejectPhase6Archive("a favorite action is malformed or crosses its event scope");
        }

        foreach (var state in favoriteStates)
        {
            if (!eventById.TryGetValue(state.LastEventId, out var favoriteEvent) ||
                favoriteEvent.TenantId != state.TenantId || favoriteEvent.OwnerUserId != state.OwnerUserId ||
                !ValidOwner(state.TenantId, state.OwnerUserId) || state.Protocol is not ("jellyfin" or "subsonic") ||
                !IsRequiredText(state.BackendInstanceId, 200) || !IsRequiredText(state.ItemId, 500))
                RejectPhase6Archive("a favorite state is malformed or crosses its last-event scope");
        }

        var policyKeys = new HashSet<(Guid, Guid?, FavoriteActionPolicyScope, string, string, string?)>();
        foreach (var policy in favoritePolicies)
        {
            var validShape = policy.Scope switch
            {
                FavoriteActionPolicyScope.Global => policy.OwnerUserId == null && AllPolicyValuesPresent(policy),
                FavoriteActionPolicyScope.User => policy.OwnerUserId is { } owner && ValidOwner(policy.TenantId, owner) && AnyPolicyValuePresent(policy),
                _ => false
            };
            var backendExists = policy.Scope == FavoriteActionPolicyScope.User
                ? backendIdentities.Any(item => item.TenantId == policy.TenantId && item.UserId == policy.OwnerUserId &&
                    item.BackendType == policy.Protocol && item.BackendInstanceId == policy.BackendInstanceId)
                : backendIdentities.Any(item => item.TenantId == policy.TenantId && item.BackendType == policy.Protocol &&
                    item.BackendInstanceId == policy.BackendInstanceId);
            if (!tenantById.ContainsKey(policy.TenantId) || !validShape || !backendExists ||
                !userById.TryGetValue(policy.UpdatedByUserId, out var actor) || actor.TenantId != policy.TenantId ||
                policy.Protocol is not ("jellyfin" or "subsonic") || !IsRequiredText(policy.BackendInstanceId, 200) ||
                policy.TargetCredentialReferenceId is { } policyCredential &&
                (!secretById.TryGetValue(policyCredential, out var policySecret) || policySecret.TenantId != policy.TenantId || policySecret.RevokedAt != null) ||
                !IsOptionalText(policy.LibraryScopeId, 300) || policy.CreatedAt == default || policy.UpdatedAt < policy.CreatedAt ||
                policy.Revision <= 0 || !policyKeys.Add((policy.TenantId, policy.OwnerUserId, policy.Scope,
                    policy.Protocol, policy.BackendInstanceId, policy.LibraryScopeId)))
                RejectPhase6Archive("a favorite action policy is malformed, duplicated, or crosses its tenant, user, actor, or backend scope");
        }

        foreach (var file in managedFiles)
        {
            var ownerValid = tenantById.ContainsKey(file.TenantId) &&
                (file.OwnerUserId == null || ValidOwner(file.TenantId, file.OwnerUserId.Value));
            var identityValid = (string.IsNullOrWhiteSpace(file.FileSystemDeviceId) &&
                                 string.IsNullOrWhiteSpace(file.FileSystemFileId) &&
                                 file.FileSystemLinkCount == null) ||
                                (IsRequiredText(file.FileSystemDeviceId, 64) &&
                                 IsRequiredText(file.FileSystemFileId, 64) &&
                                 file.FileSystemLinkCount > 0);
            if (!ownerValid || file.RootId == Guid.Empty || !file.IsManaged || file.Length < 0 || file.ReferenceCount < 0 ||
                !identityValid || file.RemovedAt != null && file.ReferenceCount != 0 ||
                !Enum.IsDefined(file.PlacementMethod) || !IsNormalizedSha256(file.ContentSha256) ||
                !IsRequiredText(file.ScopeKey, 1000) || !IsOptionalText(file.LibraryScopeId, 300) ||
                !IsSafeManagedPath(file.TargetRootPath, file.CanonicalPath) ||
                file.SourceJobId is { } jobId && !ValidJob(jobId, file.TenantId, file.OwnerUserId))
                RejectPhase6Archive("a managed file is malformed, unsafe, or crosses its tenant, owner, or job boundary");
        }

        var referenceKeys = new HashSet<(Guid ManagedFileId, string ReferenceKey)>();
        var activeReferenceCounts = new Dictionary<Guid, int>();
        foreach (var reference in managedFileReferences)
        {
            var fileValid = fileById.TryGetValue(reference.ManagedFileId, out var file) &&
                file.TenantId == reference.TenantId && file.OwnerUserId == reference.OwnerUserId &&
                StringComparer.Ordinal.Equals(file.ScopeKey, reference.ScopeKey);
            if (!fileValid || !IsRequiredText(reference.ScopeKey, 1000) ||
                !IsRequiredText(reference.ReferenceKey, 1000) || reference.CreatedAt == default ||
                reference.CreatedAt < file!.CreatedAt ||
                (reference.ReleasedAt is { } releasedAt && releasedAt < reference.CreatedAt) || reference.Revision <= 0 ||
                !referenceKeys.Add((reference.ManagedFileId, reference.ReferenceKey)))
                RejectPhase6Archive("a managed file reference is malformed, repeated, or crosses its file ownership scope");
            if (reference.ReleasedAt is null)
                activeReferenceCounts[reference.ManagedFileId] =
                    activeReferenceCounts.GetValueOrDefault(reference.ManagedFileId) + 1;
        }

        foreach (var file in managedFiles)
        {
            if (file.ReferenceCount != activeReferenceCounts.GetValueOrDefault(file.Id))
                RejectPhase6Archive("a managed file reference count does not match its durable active references");
        }

        foreach (var plan in enrichmentPlans)
        {
            if (!ValidOwner(plan.TenantId, plan.OwnerUserId) ||
                !ValidJob(plan.LineageJobId, plan.TenantId, plan.OwnerUserId) ||
                !fileById.TryGetValue(plan.ManagedArtifactId, out var file) || file.TenantId != plan.TenantId ||
                file.OwnerUserId != null && file.OwnerUserId != plan.OwnerUserId || plan.PlanVersion <= 0 ||
                !IsNormalizedSha256(plan.Fingerprint) || !IsJsonArray(plan.SourceRevisionsJson, 1024 * 1024) ||
                !IsJsonArray(plan.DecisionsJson, 1024 * 1024) || !IsJsonObject(plan.TagsJson, 1024 * 1024) ||
                !IsJsonObject(plan.PathValuesJson, 1024 * 1024))
                RejectPhase6Archive("a metadata enrichment plan is malformed or crosses its file, tenant, owner, or job boundary");
        }

        var applicationKeys = new HashSet<(Guid, Guid, Guid, string)>();
        foreach (var application in enrichmentApplications)
        {
            if (!planById.TryGetValue(application.PlanId, out var plan) || plan.TenantId != application.TenantId ||
                plan.OwnerUserId != application.OwnerUserId || plan.ManagedArtifactId != application.ManagedArtifactId ||
                plan.LineageJobId != application.LineageJobId || !Enum.IsDefined(application.State) ||
                !IsNormalizedSha256(application.ArtifactContentSha256) ||
                !applicationKeys.Add((application.TenantId, application.PlanId, application.ManagedArtifactId, application.ArtifactContentSha256)) ||
                application.State == MetadataEnrichmentApplicationState.Failed &&
                (!IsRequiredText(application.ErrorCode, 100) || !IsRequiredText(application.SafeErrorMessage, 1000)) ||
                application.State != MetadataEnrichmentApplicationState.Failed &&
                (application.ErrorCode != null || application.SafeErrorMessage != null))
                RejectPhase6Archive("a metadata enrichment application is malformed or crosses its plan scope");
        }
    }

    private static bool IsOptionalText(string? value, int maximumLength) =>
        value == null || IsRequiredText(value, maximumLength);

    private static bool AllPolicyValuesPresent(FavoriteActionPolicyRecord item) => item.AddToVirtualLiked.HasValue &&
        item.MatchLocalLibrary.HasValue && item.AutoDownload.HasValue && item.EnrichMetadata.HasValue &&
        item.PlaceManagedFile.HasValue && item.RefreshBackendLibrary.HasValue;
    private static bool AnyPolicyValuePresent(FavoriteActionPolicyRecord item) => item.AddToVirtualLiked.HasValue ||
        item.MatchLocalLibrary.HasValue || item.AutoDownload.HasValue || item.EnrichMetadata.HasValue ||
        item.PlaceManagedFile.HasValue || item.RefreshBackendLibrary.HasValue || item.TargetCredentialReferenceId.HasValue;

    private static void ValidateIntelligenceArchive(
        IReadOnlyCollection<TenantRecord> tenants, IReadOnlyCollection<PlatformUserRecord> users,
        IReadOnlyCollection<BackendIdentityRecord> backendIdentities, IReadOnlyCollection<DurableJobRecord> jobs,
        IReadOnlyCollection<JobScheduleRecord> schedules,
        IReadOnlyCollection<SecretReferenceRecord> secretReferences,
        IReadOnlyCollection<ProviderAccountRecord> providerAccounts,
        IReadOnlyCollection<ProviderTrackIdentityRecord> providerTrackIdentities,
        IReadOnlyCollection<CanonicalRecordingRecord> canonicalRecordings,
        IReadOnlyCollection<LibraryTrackRecord> libraryTracks, IReadOnlyCollection<IntelligencePolicyRecord> policies,
        IReadOnlyCollection<ListeningIntakeTokenRecord> intakeTokens,
        IReadOnlyCollection<ListeningEventRecord> events, IReadOnlyCollection<ListeningHistoryImportRecord> imports,
        IReadOnlyCollection<ListeningSignalRecord> signals,
        IReadOnlyCollection<PlaybackDeliveryCheckpointEntity> playbackCheckpoints,
        IReadOnlyCollection<ListeningProfileRecord> profiles,
        IReadOnlyCollection<RecommendationRunRecord> runs, IReadOnlyCollection<RecommendationCandidateRecord> candidates,
        IReadOnlyCollection<RecommendationFeedbackRecord> feedback,
        IReadOnlyCollection<GeneratedSetRecord> sets, IReadOnlyCollection<GeneratedSetEntryRecord> entries)
    {
        var tenantIds = IndexUnique(tenants, x => x.Id, "tenant"); var userById = IndexUnique(users, x => x.Id, "user");
        var jobById = IndexUnique(jobs, x => x.Id, "durable job"); var runById = IndexUnique(runs, x => x.Id, "recommendation run");
        var secretById = IndexUnique(secretReferences, x => x.Id, "secret reference");
        var setById = IndexUnique(sets, x => x.Id, "generated set");
        var policyById = IndexUnique(policies, x => x.Id, "intelligence policy");
        var scheduleById = IndexUnique(schedules, x => x.Id, "job schedule");
        IndexUnique(intakeTokens, x => x.Id, "listening intake token");
        IndexUnique(events, x => x.Id, "listening event"); IndexUnique(signals, x => x.Id, "listening signal");
        IndexUnique(imports, x => x.Id, "listening history import");
        IndexUnique(profiles, x => x.Id, "listening profile");
        IndexUnique(playbackCheckpoints, x => x.Id, "playback delivery checkpoint");
        IndexUnique(candidates, x => x.Id, "recommendation candidate"); IndexUnique(entries, x => x.Id, "generated set entry");
        bool Owner(Guid tenant, Guid user) => tenantIds.ContainsKey(tenant) && userById.TryGetValue(user, out var value) && value.TenantId == tenant;
        static (Guid, Guid, string, string, string) Scope(Guid tenant, Guid user, string protocol, string backend, string library) =>
            (tenant, user, protocol, backend, library);
        bool Backend(Guid tenant, Guid user, string protocol, string backend) => backendIdentities.Any(x =>
            x.TenantId == tenant && x.UserId == user && x.BackendType == protocol && x.BackendInstanceId == backend);
        bool Credential(Guid tenant, Guid? id) => id is { } value && secretById.TryGetValue(value, out var secret) &&
            secret.TenantId == tenant && secret.Purpose == IntelligencePolicyService.SubsonicCredentialPurpose &&
            secret.RevokedAt == null;
        var policyByScope = new Dictionary<(Guid, Guid, string, string, string), IntelligencePolicyRecord>();
        var recommendationSchedulePolicies = new Dictionary<Guid, IntelligencePolicyRecord>();
        foreach (var policy in policies)
        {
            if (!Owner(policy.TenantId, policy.OwnerUserId) || policy.Protocol is not ("jellyfin" or "subsonic") ||
                !Backend(policy.TenantId, policy.OwnerUserId, policy.Protocol, policy.BackendInstanceId) ||
                !IsRequiredText(policy.BackendInstanceId, 200) || !IsRequiredText(policy.LibraryScopeId, 300) ||
                policy.RetentionDays is < 1 or > 3650 || policy.CreatedAt == default || policy.UpdatedAt < policy.CreatedAt ||
                policy.Revision <= 0 || !TryCatalog(policy.AllowedSignalTypesJson, 32, out var signalCatalog) ||
                signalCatalog.Any(x => x is not ("play" or "skip" or "complete" or "favorite" or "playlist")) ||
                !TryCatalog(policy.EnabledProvidersJson, 100, out var providers) || policy.Enabled && providers.Length == 0 ||
                policy.Protocol == "jellyfin" && policy.TargetCredentialReferenceId.HasValue ||
                policy.Protocol == "subsonic" && policy.Enabled && !Credential(policy.TenantId, policy.TargetCredentialReferenceId) ||
                policy.TargetCredentialReferenceId.HasValue && !Credential(policy.TenantId, policy.TargetCredentialReferenceId) ||
                !policyByScope.TryAdd(Scope(policy.TenantId, policy.OwnerUserId, policy.Protocol, policy.BackendInstanceId, policy.LibraryScopeId), policy))
                RejectIntelligenceArchive("an intelligence policy is malformed, duplicated, or crosses its exact scope");
        }
        var intakeSecretIds = new HashSet<Guid>();
        foreach (var token in intakeTokens)
        {
            var key = Scope(token.TenantId, token.OwnerUserId, token.Protocol, token.BackendInstanceId, token.LibraryScopeId);
            if (!policyByScope.ContainsKey(key) || !Owner(token.TenantId, token.OwnerUserId) ||
                token.Protocol is not ("jellyfin" or "subsonic") ||
                !Backend(token.TenantId, token.OwnerUserId, token.Protocol, token.BackendInstanceId) ||
                !IsRequiredText(token.BackendInstanceId, 200) || !IsRequiredText(token.LibraryScopeId, 300) ||
                token.CreatedAt == default || token.RevokedAt < token.CreatedAt ||
                !intakeSecretIds.Add(token.SecretReferenceId) ||
                !secretById.TryGetValue(token.SecretReferenceId, out var secret) ||
                secret.TenantId != token.TenantId || secret.Purpose != "listening-intake-token" ||
                token.RevokedAt.HasValue != secret.RevokedAt.HasValue)
                RejectIntelligenceArchive("a listening-app token is malformed or crosses its exact scope");
        }
        foreach (var import in imports)
        {
            ListeningHistoryImportPreview? preview = null;
            var previewJsonValid = IsJsonObject(import.PreviewJson, 64 * 1024);
            if (previewJsonValid)
            {
                try { preview = JsonSerializer.Deserialize<ListeningHistoryImportPreview>(import.PreviewJson); }
                catch (JsonException) { }
            }
            if (!Owner(import.TenantId, import.OwnerUserId) || import.Protocol is not ("jellyfin" or "subsonic") ||
                !Backend(import.TenantId, import.OwnerUserId, import.Protocol, import.BackendInstanceId) ||
                !IsRequiredText(import.BackendInstanceId, 200) || !IsRequiredText(import.LibraryScopeId, 300) ||
                !IsRequiredText(import.DisplayFileName, 255) || import.DisplayFileName.IndexOfAny(['/', '\\']) >= 0 ||
                import.Format != "spotify-extended-streaming-history" || !IsNormalizedSha256(import.ContentSha256) ||
                !IsNormalizedSha256(import.PreviewRevision) || !previewJsonValid || preview == null ||
                import.SizeBytes <= 0 || !Enum.IsDefined(import.State) || import.ApplyGeneration < 0 ||
                import.NextSequence < 0 || import.ImportedRows < 0 || import.DuplicateRows < 0 ||
                import.ResolvedRows < 0 || import.UnresolvedRows < 0 || import.CreatedAt == default ||
                import.UpdatedAt < import.CreatedAt || import.ExpiresAt < import.CreatedAt || import.Revision <= 0 ||
                import.State == ListeningHistoryImportState.Completed && import.CompletedAt == null)
                RejectIntelligenceArchive("a listening-history import is malformed or crosses its exact scope");
        }
        foreach (var schedule in schedules.Where(item => item.JobType == DurableScheduleEngine.RecommendationJobType))
        {
            RecommendationScheduleTemplate? template = null;
            try { template = JsonSerializer.Deserialize<RecommendationScheduleTemplate>(schedule.PayloadTemplateJson); }
            catch (JsonException) { }
            IntelligencePolicyRecord? schedulePolicy = null;
            if (template != null)
                policyById.TryGetValue(template.IntelligencePolicyId, out schedulePolicy);
            var scheduleValid = true;
            try { DurableScheduleEngine.Validate(schedule.CronExpression, schedule.TimeZoneId); }
            catch (ArgumentException) { scheduleValid = false; }
            if (!scheduleValid || !Owner(schedule.TenantId, schedule.OwnerUserId) ||
                !IsRequiredText(schedule.LibraryScopeId, 300) || !Enum.IsDefined(schedule.OverlapPolicy) ||
                !Enum.IsDefined(schedule.MisfirePolicy) || !IsJsonObject(schedule.RetryPolicyJson, 1024 * 1024) ||
                template == null || template.Version != 1 || template.Limit is < 1 or > 500 ||
                !IsRequiredText(template.GeneratedSetName, 200) || schedulePolicy == null ||
                schedulePolicy.TenantId != schedule.TenantId || schedulePolicy.OwnerUserId != schedule.OwnerUserId ||
                schedulePolicy.LibraryScopeId != schedule.LibraryScopeId || schedule.CreatedAt == default ||
                schedule.UpdatedAt < schedule.CreatedAt || schedule.Revision < 0 ||
                schedule.Enabled != schedule.NextRunAt.HasValue || schedule.Enabled && !schedulePolicy.Enabled)
                RejectIntelligenceArchive("a recommendation schedule is malformed or crosses its exact policy, owner, or library scope");
            recommendationSchedulePolicies.Add(schedule.Id, schedulePolicy!);
        }
        var trackByReference = libraryTracks.Where(x => Owner(x.TenantId, x.OwnerUserId)).ToDictionary(x => $"library:{x.Id:N}", StringComparer.Ordinal);
        var libraryTrackById = libraryTracks.ToDictionary(x => x.Id);
        var canonicalIds = canonicalRecordings.Select(x => (x.TenantId, x.Id)).ToHashSet();
        var accountById = providerAccounts.ToDictionary(x => x.Id);
        var providerIdentityById = providerTrackIdentities.ToDictionary(x => x.Id);
        var occurrenceKeys = new HashSet<(Guid, Guid, string)>();
        foreach (var occurrence in events)
        {
            var validLibraryTrack = occurrence.LibraryTrackId is not { } libraryTrackId ||
                libraryTrackById.TryGetValue(libraryTrackId, out var libraryTrack) &&
                libraryTrack.TenantId == occurrence.TenantId && libraryTrack.OwnerUserId == occurrence.OwnerUserId &&
                libraryTrack.Protocol == occurrence.Protocol && libraryTrack.BackendInstanceId == occurrence.BackendInstanceId &&
                libraryTrack.LibraryScopeId == occurrence.LibraryScopeId;
            var validAccount = occurrence.ProviderAccountId is not { } accountId ||
                accountById.TryGetValue(accountId, out var account) &&
                occurrence.ProviderId == account.ProviderId &&
                (account.TenantId == null || account.TenantId == occurrence.TenantId) &&
                (account.OwnerUserId == null || account.OwnerUserId == occurrence.OwnerUserId) &&
                (account.LibraryScopeId == null || account.LibraryScopeId == occurrence.LibraryScopeId);
            var validProviderIdentity = occurrence.ProviderTrackIdentityId is not { } identityId ||
                providerIdentityById.TryGetValue(identityId, out var identity) &&
                identity.TenantId == occurrence.TenantId && identity.ProviderId == occurrence.ProviderId &&
                identity.ProviderAccountId == occurrence.ProviderAccountId &&
                (occurrence.CanonicalRecordingId == null || identity.CanonicalRecordingId == occurrence.CanonicalRecordingId);
            if (!Owner(occurrence.TenantId, occurrence.OwnerUserId) ||
                occurrence.Protocol is not ("jellyfin" or "subsonic") ||
                !Backend(occurrence.TenantId, occurrence.OwnerUserId, occurrence.Protocol, occurrence.BackendInstanceId) ||
                !IsRequiredText(occurrence.BackendInstanceId, 200) || !IsRequiredText(occurrence.LibraryScopeId, 300) ||
                !IsNormalizedSha256(occurrence.OccurrenceKey) ||
                !occurrenceKeys.Add((occurrence.TenantId, occurrence.OwnerUserId, occurrence.OccurrenceKey)) ||
                !Enum.IsDefined(occurrence.State) || occurrence.UpdatedAt == default ||
                occurrence.StartedAt > occurrence.UpdatedAt || occurrence.ListenedAt > occurrence.UpdatedAt ||
                occurrence.State == ListeningEventState.Completed && occurrence.ListenedAt == null ||
                occurrence.State != ListeningEventState.Completed && occurrence.ListenedAt != null ||
                occurrence.PositionTicks is < 0 || occurrence.DurationMilliseconds is <= 0 ||
                !IsOptionalText(occurrence.ClientClass, 200) || !IsOptionalText(occurrence.DeviceClass, 200) ||
                occurrence.SourceKind is not ("protocol" or "import" or "listenbrainz-api") ||
                occurrence.SourceKind != "import" && occurrence.ImportProvenance != null ||
                occurrence.SourceKind == "import" && !IsRequiredText(occurrence.ImportProvenance, 500) ||
                !IsRequiredText(occurrence.TrackReference, 500) || occurrence.TrackReference.Contains("://", StringComparison.Ordinal) ||
                !IsOptionalText(occurrence.Title, 500) || !IsOptionalText(occurrence.Artist, 500) ||
                !IsOptionalText(occurrence.Album, 500) || !IsOptionalText(occurrence.AlbumArtist, 500) ||
                occurrence.RecordingMusicBrainzId != null && !IsNormalizedMusicBrainzId(occurrence.RecordingMusicBrainzId) ||
                occurrence.Isrc != null && !IsNormalizedIsrc(occurrence.Isrc) ||
                !Enum.IsDefined(occurrence.MusicBrainzEnrichmentState) ||
                !IsOptionalText(occurrence.MusicBrainzSourceRevision, 100) ||
                occurrence.MusicBrainzFactsJson != null && !IsJsonObject(occurrence.MusicBrainzFactsJson, 1024 * 1024) ||
                !ValidMusicBrainzEnrichment(occurrence) ||
                occurrence.TrackNumber is <= 0 || !IsOptionalText(occurrence.ProviderId, 100) ||
                !IsOptionalText(occurrence.ProviderTrackReference, 500) || !validLibraryTrack ||
                occurrence.CanonicalRecordingId is { } canonicalId && !canonicalIds.Contains((occurrence.TenantId, canonicalId)) ||
                !validAccount || !validProviderIdentity)
                RejectIntelligenceArchive("a listening event is malformed, duplicated, or crosses its exact playback scope");
        }
        foreach (var signal in signals)
        {
            var key = Scope(signal.TenantId, signal.OwnerUserId, signal.Protocol, signal.BackendInstanceId, signal.LibraryScopeId);
            if (!policyByScope.TryGetValue(key, out var policy) || !policy.Enabled || !Owner(signal.TenantId, signal.OwnerUserId) ||
                !IsNormalizedSha256(signal.TrackKeyHash) || !trackByReference.TryGetValue(signal.TrackReference, out var track) ||
                track.TenantId != signal.TenantId || track.OwnerUserId != signal.OwnerUserId || track.Protocol != signal.Protocol ||
                track.BackendInstanceId != signal.BackendInstanceId || track.LibraryScopeId != signal.LibraryScopeId ||
                !TryCatalog(policy.AllowedSignalTypesJson, 32, out var allowed) || !allowed.Contains(signal.SignalType) ||
                !double.IsFinite(signal.Value) || signal.ObservedAt == default || signal.ExpiresAt <= signal.ObservedAt ||
                signal.ExpiresAt > signal.ObservedAt.AddDays(3650) || !IsOptionalText(signal.SignalKey, 64) ||
                signal.SignalKey != null && (!jobById.TryGetValue(signal.SourceJobId ?? Guid.Empty, out var sourceJob) ||
                    sourceJob.TenantId != signal.TenantId || sourceJob.OwnerUserId != signal.OwnerUserId ||
                    sourceJob.LibraryScopeId != signal.LibraryScopeId || sourceJob.Type != "playback.signal"))
                RejectIntelligenceArchive("a listening signal is malformed, expired incorrectly, disallowed, or crosses its track scope");
        }
        var signalsByDeliveryKey = signals.Where(x => x.SignalKey != null).ToDictionary(
            x => (x.TenantId, x.OwnerUserId, x.SignalKey!), x => x);
        var checkpointKeys = new HashSet<(Guid, Guid, string, string)>();
        foreach (var checkpoint in playbackCheckpoints)
        {
            var signalLineage = signalsByDeliveryKey.TryGetValue(
                    (checkpoint.TenantId, checkpoint.OwnerUserId, checkpoint.SignalKey), out var signal) &&
                signal.SourceJobId is { } sourceJobId && jobById.TryGetValue(sourceJobId, out var sourceJob) &&
                sourceJob.TenantId == checkpoint.TenantId && sourceJob.OwnerUserId == checkpoint.OwnerUserId &&
                sourceJob.LibraryScopeId == signal.LibraryScopeId && sourceJob.Type is "playback.signal" or "playback.signal.process";
            var occurrenceLineage = checkpoint.OccurrenceKey != null && occurrenceKeys.Contains(
                (checkpoint.TenantId, checkpoint.OwnerUserId, checkpoint.OccurrenceKey));
            if (!Owner(checkpoint.TenantId, checkpoint.OwnerUserId) || checkpoint.SignalKey.Length != 64 ||
                !IsOptionalText(checkpoint.OccurrenceKey, 64) ||
                checkpoint.OccurrenceKey != null && !IsNormalizedSha256(checkpoint.OccurrenceKey) ||
                checkpoint.TargetId is not ("lastfm" or "listenbrainz") || checkpoint.UpdatedAt == default ||
                !Enum.IsDefined(checkpoint.Kind) || !Enum.IsDefined(checkpoint.State) ||
                !IsOptionalText(checkpoint.ProviderCode, 100) || !IsOptionalText(checkpoint.SafeMessage, 500) ||
                !IsJsonObject(checkpoint.DetailsJson, 64 * 1024) ||
                checkpoint.State != ScopedPlaybackScrobbleOutcome.Retrying && checkpoint.RetryAfter.HasValue ||
                checkpoint.RetryAfter < checkpoint.UpdatedAt ||
                checkpoint.RequiresReauthentication && checkpoint.State != ScopedPlaybackScrobbleOutcome.PermanentFailure ||
                !checkpointKeys.Add((checkpoint.TenantId, checkpoint.OwnerUserId, checkpoint.SignalKey, checkpoint.TargetId)) ||
                !occurrenceLineage && !signalLineage)
                RejectIntelligenceArchive("a playback delivery checkpoint is malformed, duplicated, or crosses its listening-signal and job scope");
        }
        foreach (var profile in profiles)
        {
            var key = Scope(profile.TenantId, profile.OwnerUserId, profile.Protocol, profile.BackendInstanceId, profile.LibraryScopeId);
            ListeningProfile? value = null; try { value = JsonSerializer.Deserialize<ListeningProfile>(profile.ProfileJson); } catch (JsonException) { }
            if (!policyByScope.ContainsKey(key) || value == null || value.TenantId != profile.TenantId || value.OwnerUserId != profile.OwnerUserId ||
                value.BackendInstanceId != profile.BackendInstanceId || value.LibraryScopeId != profile.LibraryScopeId ||
                profile.WindowStart > profile.WindowEnd || profile.CreatedAt < profile.WindowEnd || value.TopTrackKeys.Count > 100 ||
                value.TopTrackKeys.Any(reference => !trackByReference.TryGetValue(reference, out var track) || track.TenantId != profile.TenantId || track.OwnerUserId != profile.OwnerUserId))
                RejectIntelligenceArchive("a listening profile is malformed or crosses its policy and track scope");
        }
        var runKeys = new HashSet<(Guid, Guid, string)>();
        var scheduledOccurrences = new HashSet<(Guid, DateTimeOffset)>();
        foreach (var run in runs)
        {
            var key = Scope(run.TenantId, run.OwnerUserId, run.Protocol, run.BackendInstanceId, run.LibraryScopeId);
            RecommendationPolicySnapshot? snapshot = null; try { snapshot = JsonSerializer.Deserialize<RecommendationPolicySnapshot>(run.PolicySnapshotJson); } catch (JsonException) { }
            var scheduled = run.ScheduleId.HasValue || run.ScheduledFor.HasValue || snapshot?.Automation != null;
            var validScheduleLineage = !scheduled || run.ScheduleId is { } scheduleId && run.ScheduledFor is { } scheduledFor &&
                scheduleById.TryGetValue(scheduleId, out var schedule) && schedule.JobType == DurableScheduleEngine.RecommendationJobType &&
                schedule.TenantId == run.TenantId && schedule.OwnerUserId == run.OwnerUserId && schedule.LibraryScopeId == run.LibraryScopeId &&
                recommendationSchedulePolicies.TryGetValue(scheduleId, out var schedulePolicy) &&
                schedulePolicy.Protocol == run.Protocol && schedulePolicy.BackendInstanceId == run.BackendInstanceId &&
                snapshot?.Automation is { } automation && automation.ScheduleId == scheduleId && automation.ScheduledFor == scheduledFor &&
                IsRequiredText(automation.GeneratedSetName, 200) && run.IdempotencyKey == $"schedule:{scheduleId:N}:{scheduledFor.UtcTicks}";
            if (!policyByScope.ContainsKey(key) || !jobById.TryGetValue(run.JobId, out var job) || job.TenantId != run.TenantId ||
                job.OwnerUserId != run.OwnerUserId || job.Type != "recommendation.generate" || job.LibraryScopeId != run.LibraryScopeId ||
                job.IdempotencyKey != run.IdempotencyKey || !ValidRecommendationPayload(job.PayloadJson, run.Id) ||
                !IsRequiredText(run.IdempotencyKey, 300) || !runKeys.Add((run.TenantId, run.OwnerUserId, run.IdempotencyKey)) ||
                snapshot == null || snapshot.Revision <= 0 || snapshot.RetentionDays is < 1 or > 3650 || snapshot.EnabledProviders.Count is < 1 or > 100 ||
                snapshot.TargetCredentialReferenceId != run.TargetCredentialReferenceId ||
                !validScheduleLineage || scheduled && !scheduledOccurrences.Add((run.ScheduleId!.Value, run.ScheduledFor!.Value)) ||
                run.Protocol == "jellyfin" && run.TargetCredentialReferenceId.HasValue ||
                run.Protocol == "subsonic" && !Credential(run.TenantId, run.TargetCredentialReferenceId) ||
                snapshot.EnabledProviders.Any(x => !IsIntelligenceCatalog(x, 100)) || !TryBoundedStrings(run.SeedTrackKeysJson, 100, 500) ||
                run.Limit is < 1 or > 500 || !Enum.IsDefined(run.State) || run.CreatedAt == default || run.UpdatedAt < run.CreatedAt ||
                run.State is RecommendationRunState.Succeeded or RecommendationRunState.Failed or RecommendationRunState.Cancelled && run.CompletedAt == null ||
                run.State == RecommendationRunState.Succeeded && job.State != DurableJobState.Succeeded ||
                run.State == RecommendationRunState.Failed && job.State != DurableJobState.Failed ||
                run.State == RecommendationRunState.Cancelled && job.State != DurableJobState.Cancelled && job.CancellationRequestedAt == null)
                RejectIntelligenceArchive("a recommendation run is malformed or crosses its immutable policy, owner, library, or job scope");
        }
        ValidateRecommendationChildren(runById, setById, policyByScope, secretReferences,
            providerAccounts, canonicalRecordings, candidates, feedback, sets, entries);
    }

    private static void ValidateRecommendationChildren(IReadOnlyDictionary<Guid, RecommendationRunRecord> runById,
        IReadOnlyDictionary<Guid, GeneratedSetRecord> setById,
        IReadOnlyDictionary<(Guid, Guid, string, string, string), IntelligencePolicyRecord> policyByScope,
        IReadOnlyCollection<SecretReferenceRecord> secretReferences,
        IReadOnlyCollection<ProviderAccountRecord> providerAccounts,
        IReadOnlyCollection<CanonicalRecordingRecord> canonicalRecordings,
        IReadOnlyCollection<RecommendationCandidateRecord> candidates,
        IReadOnlyCollection<RecommendationFeedbackRecord> feedback,
        IReadOnlyCollection<GeneratedSetRecord> sets, IReadOnlyCollection<GeneratedSetEntryRecord> entries)
    {
        var candidateById = candidates.ToDictionary(item => item.Id);
        var accountById = providerAccounts.ToDictionary(item => item.Id);
        var canonicalIds = canonicalRecordings.Select(item => (item.TenantId, item.Id)).ToHashSet();
        foreach (var group in candidates.GroupBy(x => x.RunId))
        {
            if (!runById.TryGetValue(group.Key, out var run) || !Contiguous(group.Select(x => x.Position)) || group.Select(x => x.TrackKey).Distinct(StringComparer.Ordinal).Count() != group.Count()) RejectIntelligenceArchive("recommendation candidate order, uniqueness, or run lineage is invalid");
            if (run == null) continue;
            foreach (var item in group) if (item.TenantId != run.TenantId || item.OwnerUserId != run.OwnerUserId || !IsRequiredText(item.TrackKey, 500) ||
                item.Score is < 0 or > 1 || !double.IsFinite(item.Score) || !IsRequiredText(item.Source, 100) ||
                !ValidSignals(item.SignalsJson) || !ValidIdentity(item.IdentityJson) ||
                !IsRequiredText(item.SourceRevision, 300) || !TryCatalog(item.ExclusionsJson, 100, out var exclusions) ||
                exclusions.Length > 16 || item.Revision < 0 ||
                item.CanonicalRecordingId is { } canonicalId && !canonicalIds.Contains((item.TenantId, canonicalId)) ||
                item.ProviderAccountId is { } accountId &&
                (!accountById.TryGetValue(accountId, out var account) || account.ProviderId != item.Source ||
                 account.TenantId != item.TenantId ||
                 account.Scope == ProviderAccountScope.User && account.OwnerUserId != item.OwnerUserId ||
                 account.Scope == ProviderAccountScope.Library && account.LibraryScopeId != run.LibraryScopeId))
                RejectIntelligenceArchive("a recommendation candidate is malformed or crosses its run, canonical recording, or provider-account scope");
        }
        var feedbackCandidates = new HashSet<Guid>();
        foreach (var item in feedback)
        {
            var valid = candidateById.TryGetValue(item.CandidateId, out var candidate) &&
                        runById.TryGetValue(candidate.RunId, out var run) &&
                        item.TenantId == candidate.TenantId && item.OwnerUserId == candidate.OwnerUserId &&
                        item.Protocol == run.Protocol && item.BackendInstanceId == run.BackendInstanceId &&
                        item.LibraryScopeId == run.LibraryScopeId && item.TrackKey == candidate.TrackKey;
            if (!valid || !feedbackCandidates.Add(item.CandidateId) ||
                item.Kind is not ("like" or "dislike" or "dismiss") ||
                !IsOptionalText(item.ReasonCode, 100) || item.CreatedAt == default ||
                item.UpdatedAt < item.CreatedAt || item.Revision <= 0)
                RejectIntelligenceArchive("recommendation feedback is malformed, duplicated, or crosses its candidate scope");
        }
        var runSets = new HashSet<Guid>(); foreach (var set in sets)
        {
            var scopeKey = (set.TenantId, set.OwnerUserId, set.Protocol, set.BackendInstanceId, set.LibraryScopeId);
            var fromRun = set.RunId is { } runId && runById.TryGetValue(runId, out var run) &&
                run.State == RecommendationRunState.Succeeded && runSets.Add(runId) &&
                set.TenantId == run.TenantId && set.OwnerUserId == run.OwnerUserId && set.Protocol == run.Protocol &&
                set.BackendInstanceId == run.BackendInstanceId && set.LibraryScopeId == run.LibraryScopeId &&
                set.TargetCredentialReferenceId == run.TargetCredentialReferenceId && set.ScheduleId == run.ScheduleId;
            var fromPreview = set.RunId == null && set.ScheduleId == null &&
                policyByScope.ContainsKey(scopeKey) &&
                (set.Protocol == "jellyfin" && set.TargetCredentialReferenceId == null ||
                 set.Protocol == "subsonic" && set.TargetCredentialReferenceId is { } credentialId &&
                 secretReferences.Any(item => item.Id == credentialId && item.TenantId == set.TenantId &&
                     item.Purpose == IntelligencePolicyService.SubsonicCredentialPurpose && item.RevokedAt == null));
            if ((!fromRun && !fromPreview) || !IsRequiredText(set.Name, 200) || !Enum.IsDefined(set.MaterializationState) ||
                set.CreatedAt == default || set.UpdatedAt < set.CreatedAt || set.Revision <= 0 ||
                !IsOptionalText(set.BackendPlaylistId, 500) || !IsOptionalText(set.TargetRevision, 300) || !IsOptionalText(set.LastErrorCode, 100) ||
                !ValidMaterializationLifecycle(set))
                RejectIntelligenceArchive("a generated set is malformed, duplicated, or crosses its run or preview scope");
        }
        foreach (var group in entries.GroupBy(x => x.GeneratedSetId))
        {
            if (!setById.TryGetValue(group.Key, out var set) || !Contiguous(group.Select(x => x.Position)) || group.Select(x => x.TrackKey).Distinct(StringComparer.Ordinal).Count() != group.Count()) RejectIntelligenceArchive("generated set entry order, uniqueness, or lineage is invalid");
            if (set == null) continue;
            foreach (var item in group) if (item.TenantId != set.TenantId || item.OwnerUserId != set.OwnerUserId || !IsRequiredText(item.TrackKey, 500) ||
                item.Score is < 0 or > 1 || !double.IsFinite(item.Score) || !IsRequiredText(item.Source, 100) || !ValidSignals(item.ExplanationJson) || !ValidIdentity(item.IdentityJson))
                RejectIntelligenceArchive("a generated set entry is malformed or crosses its set scope");
        }
    }
    private static bool ValidMaterializationLifecycle(GeneratedSetRecord set) => set.MaterializationState switch
    {
        GeneratedSetMaterializationState.Pending or GeneratedSetMaterializationState.Running =>
            set.BackendPlaylistId == null && set.TargetRevision == null && set.LastErrorCode == null && set.MaterializedAt == null,
        GeneratedSetMaterializationState.Succeeded => IsRequiredText(set.BackendPlaylistId, 500) &&
            set.LastErrorCode == null && set.MaterializedAt is { } at && at >= set.CreatedAt && at <= set.UpdatedAt,
        GeneratedSetMaterializationState.Failed or GeneratedSetMaterializationState.Unsupported =>
            IsRequiredText(set.LastErrorCode, 100) && set.BackendPlaylistId == null && set.TargetRevision == null && set.MaterializedAt == null,
        GeneratedSetMaterializationState.Cancelled => set.BackendPlaylistId == null && set.TargetRevision == null && set.MaterializedAt == null,
        _ => false
    };
    private static bool TryCatalog(string json, int max, out string[] values)
    { try { values = JsonSerializer.Deserialize<string[]>(json) ?? []; return values.Length <= 100 && values.Distinct(StringComparer.Ordinal).Count() == values.Length && values.All(x => IsIntelligenceCatalog(x, max)); } catch (JsonException) { values = []; return false; } }
    private static bool IsIntelligenceCatalog(string value, int max) => IsRequiredText(value, max) && value.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_');
    private static bool TryBoundedStrings(string json, int count, int max)
    { try { var values = JsonSerializer.Deserialize<string[]>(json); return values != null && values.Length <= count && values.All(x => IsRequiredText(x, max)); } catch (JsonException) { return false; } }
    private static bool Contiguous(IEnumerable<int> positions) { var values = positions.Order().ToArray(); return values.SequenceEqual(Enumerable.Range(0, values.Length)); }
    private static bool ValidSignals(string json) { try { var values = JsonSerializer.Deserialize<RecommendationSignal[]>(json); return values is { Length: > 0 and <= 32 } && values.All(x => IsRequiredText(x.Code, 100) && IsRequiredText(x.Explanation, 1000) && double.IsFinite(x.Weight)); } catch (JsonException) { return false; } }
    private static bool ValidIdentity(string json) { try { if (json == "null") return true; var x = JsonSerializer.Deserialize<RecommendationTrackIdentity>(json); return x != null && IsOptionalText(x.ProviderId, 100) && IsOptionalText(x.ProviderTrackId, 500) && IsOptionalText(x.Title, 500) && IsOptionalText(x.Artist, 500) && IsOptionalText(x.Album, 500) && IsOptionalText(x.Isrc, 20) && (x.MusicBrainzRecordingId == null || Guid.TryParse(x.MusicBrainzRecordingId, out _)); } catch (JsonException) { return false; } }
    private static bool ValidRecommendationPayload(string json, Guid runId)
    { try { return JsonSerializer.Deserialize<RecommendationRunPayload>(json)?.RunId == runId; } catch (JsonException) { return false; } }
    private static void RejectIntelligenceArchive(string reason) => throw new BackupVerificationException($"State transfer intelligence data is invalid because {reason}.");

    private static void ValidateDownloadArtifactArchive(
        IReadOnlyCollection<TenantRecord> tenants,
        IReadOnlyCollection<PlatformUserRecord> users,
        IReadOnlyCollection<DurableJobRecord> jobs,
        IReadOnlyCollection<ProviderAccountRecord> providerAccounts,
        IReadOnlyCollection<ManagedFileOwnershipEntity> managedFiles,
        IReadOnlyCollection<ProviderDownloadWorkspaceEntity> workspaces,
        IReadOnlyCollection<ProviderDownloadArtifactEntity> artifacts)
    {
        var tenantById = tenants.ToDictionary(item => item.Id);
        var userById = users.ToDictionary(item => item.Id);
        var jobById = jobs.ToDictionary(item => item.Id);
        var accountById = providerAccounts.ToDictionary(item => item.Id);
        var managedById = managedFiles.ToDictionary(item => item.Id);
        var workspaceById = IndexUnique(workspaces, item => item.Id, "provider download workspace");
        IndexUnique(artifacts, item => item.Id, "provider download artifact");
        var publicWorkspaceIds = new HashSet<string>(StringComparer.Ordinal);
        var workspaceKeys = new HashSet<(Guid, Guid, string, Guid?, string)>();

        bool ValidOwner(Guid tenantId, Guid? ownerId) => tenantById.ContainsKey(tenantId) &&
            (ownerId == null || userById.TryGetValue(ownerId.Value, out var user) && user.TenantId == tenantId);
        bool ValidAccount(ProviderDownloadWorkspaceEntity workspace)
        {
            if (workspace.ProviderAccountId is not { } accountId) return true;
            if (!accountById.TryGetValue(accountId, out var account) || account.ProviderId != workspace.ProviderId) return false;
            return account.Scope switch
            {
                ProviderAccountScope.Global => account.TenantId == null && account.OwnerUserId == null && account.LibraryScopeId == null,
                ProviderAccountScope.User => account.TenantId == workspace.TenantId &&
                                             account.OwnerUserId == workspace.OwnerUserId,
                ProviderAccountScope.Library => account.TenantId == workspace.TenantId && account.OwnerUserId == null && account.LibraryScopeId == workspace.LibraryScopeId,
                _ => false
            };
        }

        foreach (var workspace in workspaces)
        {
            var validJob = jobById.TryGetValue(workspace.DurableJobId, out var job) &&
                job.TenantId == workspace.TenantId && job.OwnerUserId == workspace.OwnerUserId &&
                job.LibraryScopeId == workspace.LibraryScopeId;
            if (!ValidOwner(workspace.TenantId, workspace.OwnerUserId) || !validJob || !ValidAccount(workspace) ||
                !IsNormalizedProviderId(workspace.ProviderId) || !IsNormalizedSha256(workspace.WorkspaceId) ||
                !IsOptionalText(workspace.LibraryScopeId, 300) || !IsRequiredText(workspace.IdempotencyKey, 300) ||
                workspace.CreatedAt == default || workspace.Revision < 0 ||
                !publicWorkspaceIds.Add(workspace.WorkspaceId) ||
                !workspaceKeys.Add((workspace.TenantId, workspace.DurableJobId, workspace.ProviderId, workspace.ProviderAccountId, workspace.IdempotencyKey)))
                RejectPhase6Archive("a provider download workspace is malformed, repeated, or crosses its tenant, owner, job, provider-account, or library scope");
        }

        var artifactIdentities = new HashSet<(Guid, string)>();
        var jobProviderKeys = new HashSet<(Guid, Guid, string)>();
        foreach (var artifact in artifacts)
        {
            var workspaceValid = workspaceById.TryGetValue(artifact.WorkspaceRecordId, out var workspace) &&
                artifact.WorkspaceId == workspace.WorkspaceId && artifact.TenantId == workspace.TenantId &&
                artifact.OwnerUserId == workspace.OwnerUserId && artifact.LibraryScopeId == workspace.LibraryScopeId &&
                artifact.DurableJobId == workspace.DurableJobId && artifact.ProviderId == workspace.ProviderId &&
                artifact.ProviderAccountId == workspace.ProviderAccountId;
            var managedValid = artifact.ManagedFileId is not { } managedId ||
                managedById.TryGetValue(managedId, out var managed) && managed.TenantId == artifact.TenantId &&
                (managed.OwnerUserId == null || managed.OwnerUserId == artifact.OwnerUserId) && managed.SourceJobId == artifact.DurableJobId;
            var lifecycleValid = artifact.State switch
            {
                ProviderDownloadArtifactState.Placed => artifact.ManagedFileId != null && artifact.PlacedAt != null,
                ProviderDownloadArtifactState.Verified => artifact.ManagedFileId == null && artifact.PlacedAt == null,
                ProviderDownloadArtifactState.Pending or ProviderDownloadArtifactState.Failed => artifact.ManagedFileId == null && artifact.PlacedAt == null,
                _ => false
            };
            if (!workspaceValid || !managedValid || !lifecycleValid || !IsNormalizedSha256(artifact.ContentSha256) ||
                artifact.Length <= 0 || !IsSafeWorkspaceRelativePath(artifact.RelativePath) ||
                !IsRequiredText(artifact.ProviderArtifactId, 500) || artifact.ProviderArtifactId != artifact.RelativePath ||
                artifact.CreatedAt == default || artifact.VerifiedAt < artifact.CreatedAt || artifact.Revision < 0 ||
                !artifactIdentities.Add((artifact.WorkspaceRecordId, artifact.ProviderArtifactId)) ||
                !jobProviderKeys.Add((artifact.TenantId, artifact.DurableJobId, artifact.ProviderId)))
                RejectPhase6Archive("a provider download artifact is malformed, repeated, unsafe, or crosses its workspace, tenant, owner, job, provider-account, or managed-file scope");
        }
    }

    private static bool IsSafeWorkspaceRelativePath(string value)
    {
        if (!IsRequiredText(value, 1000) || Path.IsPathRooted(value) || value.Contains('\\') || value.Contains('\0')) return false;
        var parts = value.Split('/', StringSplitOptions.None);
        return parts.Length > 0 && parts.All(part => IsRequiredText(part, 255) && part is not "." and not "..");
    }

    private static bool IsSafeManagedPath(string root, string path)
    {
        if (!IsRequiredText(root, 2000) || !IsRequiredText(path, 2000) ||
            !Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedPath = Path.GetFullPath(path);
            if (!normalizedRoot.Equals(root, StringComparison.Ordinal) || !normalizedPath.Equals(path, StringComparison.Ordinal)) return false;
            return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsJsonArray(string value, int maximumLength) => IsJsonKind(value, maximumLength, JsonValueKind.Array);
    private static bool IsJsonObject(string value, int maximumLength) => IsJsonKind(value, maximumLength, JsonValueKind.Object);
    private static bool IsJsonKind(string value, int maximumLength, JsonValueKind kind)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength) return false;
        try { using var document = JsonDocument.Parse(value); return document.RootElement.ValueKind == kind; }
        catch (JsonException) { return false; }
    }

    private static void RejectPhase6Archive(string reason) => throw new BackupVerificationException(
        $"State transfer Phase 6 data is invalid because {reason}.");

    private static Dictionary<Guid, T> IndexUnique<T>(
        IEnumerable<T> values,
        Func<T, Guid> keySelector,
        string recordName)
    {
        var result = new Dictionary<Guid, T>();
        foreach (var value in values)
        {
            var key = keySelector(value);
            if (key == Guid.Empty || !result.TryAdd(key, value))
            {
                RejectIdentityArchive($"the {recordName} set contains an empty or repeated ID");
            }
        }

        return result;
    }

    private static bool IsNormalizedProviderId(string value) =>
        IsRequiredText(value, 100) &&
        Regex.IsMatch(
            value,
            "^[a-z0-9]+(?:-[a-z0-9]+)*$",
            RegexOptions.CultureInvariant);

    private static bool IsNormalizedCatalog(string value) =>
        IsRequiredText(value, 100) &&
        Regex.IsMatch(
            value,
            "^[a-z0-9][a-z0-9._-]*$",
            RegexOptions.CultureInvariant);

    private static bool IsRequiredText(string? value, int maximumLength) =>
        value != null &&
        value.Length > 0 &&
        value.Length <= maximumLength &&
        value.Equals(value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool IsNormalizedIsrc(string value) =>
        Regex.IsMatch(
            value,
            "^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}$",
            RegexOptions.CultureInvariant);

    private static bool IsNormalizedMusicBrainzId(string value) =>
        Guid.TryParseExact(value, "D", out var id) &&
        id != Guid.Empty &&
        id.ToString("D").Equals(value, StringComparison.Ordinal);

    private static bool ValidMusicBrainzEnrichment(ListeningEventRecord occurrence) =>
        occurrence.MusicBrainzEnrichmentState switch
        {
            MusicBrainzEnrichmentState.NotRequested or MusicBrainzEnrichmentState.Pending =>
                occurrence.MusicBrainzEnrichmentConfidence == null &&
                occurrence.MusicBrainzSourceRevision == null &&
                occurrence.MusicBrainzFactsJson == null &&
                occurrence.MusicBrainzEnrichedAt == null,
            MusicBrainzEnrichmentState.Resolved =>
                occurrence.MusicBrainzEnrichmentConfidence is >= 0 and <= 1 &&
                occurrence.RecordingMusicBrainzId != null &&
                IsRequiredText(occurrence.MusicBrainzSourceRevision, 100) &&
                occurrence.MusicBrainzFactsJson != null &&
                occurrence.MusicBrainzEnrichedAt != null,
            MusicBrainzEnrichmentState.Unresolved or MusicBrainzEnrichmentState.Failed =>
                occurrence.MusicBrainzEnrichmentConfidence == null &&
                IsRequiredText(occurrence.MusicBrainzSourceRevision, 100) &&
                occurrence.MusicBrainzFactsJson == null &&
                occurrence.MusicBrainzEnrichedAt != null,
            _ => false
        };

    private static bool IsNormalizedSha256(string? value) =>
        value?.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string HashExternalId(string externalId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(externalId)))
            .ToLowerInvariant();

    private static void RejectIdentityArchive(string reason) =>
        throw new BackupVerificationException(
            $"State transfer identity data is invalid because {reason}.");

    private static async Task WriteEntryAsync<T>(
        ZipArchive archive,
        string name,
        IReadOnlyCollection<T> values,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, values, JsonOptions, cancellationToken);
    }

    private static async Task<List<T>> ReadEntryAsync<T>(
        ZipArchive archive,
        string name,
        CancellationToken cancellationToken)
    {
        var entry = SingleEntry(archive, name);
        await using var stream = entry.Open();
        try
        {
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, JsonOptions, cancellationToken)
                   ?? throw new BackupVerificationException(
                       $"State transfer entry '{name}' does not contain a JSON array.");
        }
        catch (JsonException)
        {
            throw new BackupVerificationException(
                $"State transfer entry '{name}' contains invalid JSON.");
        }
    }

    private static async Task<TransferManifest> ReadManifestAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entry = SingleEntry(archive, "manifest.json");
        if (entry.Length is <= 0 or > MaximumManifestBytes)
        {
            throw new BackupVerificationException("State transfer manifest size is invalid.");
        }

        try
        {
            await using var stream = entry.Open();
            using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                },
                cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1)
            {
                throw new BackupVerificationException(
                    "State transfer manifest must contain exactly one manifest object.");
            }

            var value = root[0];
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new BackupVerificationException(
                    "State transfer manifest must contain exactly one manifest object.");
            }

            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "formatVersion",
                "sourceProvider",
                "schemaVersion",
                "applicationVersion",
                "createdAt",
                "secretKeyMaterialIncluded"
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!expected.Contains(property.Name) || !seen.Add(property.Name))
                {
                    throw new BackupVerificationException(
                        "State transfer manifest contains an unknown or repeated field.");
                }
            }

            if (seen.Count != expected.Count ||
                !value.GetProperty("formatVersion").TryGetInt32(out var formatVersion) ||
                value.GetProperty("sourceProvider").ValueKind != JsonValueKind.String ||
                value.GetProperty("schemaVersion").ValueKind != JsonValueKind.String ||
                value.GetProperty("applicationVersion").ValueKind != JsonValueKind.String ||
                value.GetProperty("createdAt").ValueKind != JsonValueKind.String ||
                !value.GetProperty("createdAt").TryGetDateTimeOffset(out var createdAt) ||
                value.GetProperty("secretKeyMaterialIncluded").ValueKind is not
                    (JsonValueKind.True or JsonValueKind.False))
            {
                throw new BackupVerificationException(
                    "State transfer manifest is missing a required field or contains an invalid field type.");
            }

            var sourceProvider = value.GetProperty("sourceProvider").GetString()!;
            var schemaVersion = value.GetProperty("schemaVersion").GetString()!;
            var applicationVersion = value.GetProperty("applicationVersion").GetString()!;
            var includesSecretMaterial = value.GetProperty("secretKeyMaterialIncluded").GetBoolean();
            if (formatVersion != CurrentFormatVersion ||
                !Enum.TryParse<DurableStorageProvider>(
                    sourceProvider,
                    ignoreCase: false,
                    out var parsedProvider) ||
                !Enum.IsDefined(parsedProvider) ||
                !parsedProvider.ToString().Equals(sourceProvider, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(schemaVersion) || schemaVersion.Length > 200 ||
                schemaVersion.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(applicationVersion) || applicationVersion.Length > 50 ||
                applicationVersion.Any(char.IsControl) ||
                !applicationVersion.Equals(AppVersion.Version, StringComparison.Ordinal) ||
                createdAt.Offset != TimeSpan.Zero ||
                includesSecretMaterial)
            {
                throw new BackupVerificationException(
                    "State transfer format, provider, schema, application, or secret-key policy is incompatible.");
            }

            return new TransferManifest
            {
                FormatVersion = formatVersion,
                SourceProvider = sourceProvider,
                SchemaVersion = schemaVersion,
                ApplicationVersion = applicationVersion,
                CreatedAt = createdAt,
                SecretKeyMaterialIncluded = includesSecretMaterial
            };
        }
        catch (JsonException)
        {
            throw new BackupVerificationException("State transfer manifest JSON is invalid.");
        }
    }

    private static void ValidateArchiveEntries(ZipArchive archive)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!ExpectedEntryNames.Contains(entry.FullName) || !seen.Add(entry.FullName))
            {
                throw new BackupVerificationException(
                    "State transfer archive contains an unknown or repeated entry.");
            }
        }

        if (seen.Count != ExpectedEntryNames.Count)
        {
            throw new BackupVerificationException(
                "State transfer archive is missing a required entry.");
        }
    }

    private static ZipArchiveEntry SingleEntry(ZipArchive archive, string name)
    {
        var matches = archive.Entries
            .Where(entry => entry.FullName.Equals(name, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new BackupVerificationException(
                $"State transfer entry '{name}' is missing or repeated.");
    }

    private static void ValidateManifestMatchesArtifact(
        DurableStateTransferArtifact manifestArtifact,
        DurableStateTransferArtifact requestedArtifact)
    {
        if (!manifestArtifact.SourceProvider.Equals(
                requestedArtifact.SourceProvider,
                StringComparison.Ordinal) ||
            !manifestArtifact.SchemaVersion.Equals(
                requestedArtifact.SchemaVersion,
                StringComparison.Ordinal) ||
            manifestArtifact.CreatedAt != requestedArtifact.CreatedAt)
        {
            throw new BackupVerificationException(
                "State transfer manifest metadata does not match the requested artifact.");
        }
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new BackupVerificationException(
                "State transfer checksum must be a 64-character SHA-256 value.");
        }

        return normalized;
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
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private sealed class TransferManifest
    {
        public int FormatVersion { get; set; }
        public string SourceProvider { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public string ApplicationVersion { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public bool SecretKeyMaterialIncluded { get; set; }
    }
}
