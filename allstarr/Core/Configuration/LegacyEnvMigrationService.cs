using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Jobs;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Configuration;

public sealed record LegacyEnvMigrationActor(
    string SessionId,
    Guid? TenantId,
    Guid? ActorUserId,
    string CorrelationId);

public sealed record LegacyEnvPreviewItem(
    string Key,
    int SourceLine,
    string Classification,
    string Action,
    string Reason,
    bool Sensitive,
    string? ValuePreview,
    string? DurableKey,
    string? ProviderId,
    long? ExistingRevision,
    string? Warning = null);

public sealed record LegacyProviderAccountPreview(
    string ProviderId,
    string Action,
    IReadOnlyList<string> Fields,
    bool EnabledAfterImport,
    string Reason);

public sealed record LegacyEnvMigrationPreview(
    string PreviewToken,
    string SourceSha256,
    string ParserVersion,
    string Revision,
    DateTimeOffset ExpiresAt,
    bool CanApply,
    int ImportedSettingCount,
    int ProviderAccountCount,
    int ManualCount,
    IReadOnlyList<LegacyEnvPreviewItem> Items,
    IReadOnlyList<LegacyProviderAccountPreview> ProviderAccounts,
    IReadOnlyList<LegacyPlaylistHandoff> PlaylistHandoffs,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Warnings)
{
    public int BackendIdentityCount { get; init; }
    public int PlaylistLinkCount { get; init; }
    public int ScheduleCount { get; init; }
}

public sealed record LegacyEnvMigrationApplyResult(
    bool Success,
    bool AlreadyApplied,
    int SettingsImported,
    int ProviderAccountsCreated,
    int SettingsSkipped,
    int ProviderAccountsSkipped,
    int ManualChecklistItems,
    int PlaylistHandoffsPending,
    IReadOnlyList<string> CreatedProviders,
    string SourceFingerprint,
    DateTimeOffset AppliedAt)
{
    public int BackendIdentitiesCreated { get; init; }
    public int PlaylistLinksCreated { get; init; }
    public int SchedulesCreated { get; init; }
}

public sealed record LegacyEnvMigrationStatus(
    bool Available,
    bool Completed,
    bool SourcePresent,
    bool FirstRun,
    DateTimeOffset? LastAppliedAt);

public sealed class LegacyEnvMigrationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class LegacyEnvMigrationService
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);
    private const int MaximumPreviewCount = 64;
    internal const string MigrationSchemaVersion = "legacy-env-import-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<AllstarrDbContext> _factory;
    private readonly DurableRuntimeSettingsService _settings;
    private readonly EncryptedSecretStore _secrets;
    private readonly IPlatformClock _clock;
    private readonly ConcurrentDictionary<string, PreviewState> _previews = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);

    public LegacyEnvMigrationService(
        IDbContextFactory<AllstarrDbContext> factory,
        DurableRuntimeSettingsService settings,
        EncryptedSecretStore secrets,
        IPlatformClock clock) =>
        (_factory, _settings, _secrets, _clock) = (factory, settings, secrets, clock);

    public async Task<LegacyEnvMigrationStatus> GetStatusAsync(
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var completedAt = tenantId.HasValue
            ? await db.LegacyEnvImports.AsNoTracking()
            .Where(item => item.TenantId == tenantId.Value)
            .OrderByDescending(item => item.AppliedAt)
            .Select(item => (DateTimeOffset?)item.AppliedAt)
            .FirstOrDefaultAsync(cancellationToken)
            : null;
        return new(true, completedAt.HasValue, SourcePresent: false, FirstRun: !completedAt.HasValue, completedAt);
    }

    public async Task<LegacyEnvMigrationPreview> PreviewAsync(
        ReadOnlyMemory<byte> source,
        LegacyEnvMigrationActor actor,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        PurgeExpired();
        var document = LegacyEnvParser.Parse(source);
        var tenantId = actor.TenantId;
        var durableEntries = document.Entries
            .Where(item => item.Disposition == LegacyEnvDisposition.DurableSetting && item.Value.Length > 0)
            .ToArray();

        IReadOnlyDictionary<string, EffectiveRuntimeSetting> existingSettings =
            new Dictionary<string, EffectiveRuntimeSetting>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> existingProviders = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> existingUserProviders = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<BackendIdentityRecord> existingBackendIdentities = [];
        HashSet<string> existingPlaylistTargets = new(StringComparer.Ordinal);
        if (tenantId.HasValue)
        {
            existingSettings = await _settings.GetManyAsync(
                tenantId.Value,
                durableEntries.Select(item => item.DurableKey!),
                cancellationToken);
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            existingProviders = (await db.ProviderAccounts.AsNoTracking()
                    .Where(item => item.Scope == ProviderAccountScope.Global && item.TenantId == null)
                    .Select(item => item.ProviderId)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (actor.ActorUserId.HasValue)
            {
                existingUserProviders = (await db.ProviderAccounts.AsNoTracking()
                        .Where(item => item.Scope == ProviderAccountScope.User &&
                                       item.TenantId == tenantId.Value &&
                                       item.OwnerUserId == actor.ActorUserId.Value)
                        .Select(item => item.ProviderId)
                        .ToListAsync(cancellationToken))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                existingBackendIdentities = await db.BackendIdentities.AsNoTracking()
                    .Where(item => item.TenantId == tenantId.Value &&
                                   item.UserId == actor.ActorUserId.Value)
                    .ToListAsync(cancellationToken);
                existingPlaylistTargets = (await db.PlaylistLinks.AsNoTracking()
                        .Where(item => item.TenantId == tenantId.Value &&
                                       item.OwnerUserId == actor.ActorUserId.Value &&
                                       item.SourceProviderId == "spotify")
                        .Select(item => new
                        {
                            item.SourcePlaylistIdHash,
                            item.TargetProtocol,
                            item.TargetBackendInstanceId
                        })
                        .ToListAsync(cancellationToken))
                    .Select(item => PlaylistTargetKey(
                        item.SourcePlaylistIdHash,
                        item.TargetProtocol,
                        item.TargetBackendInstanceId))
                    .ToHashSet(StringComparer.Ordinal);
            }
        }

        var conflicts = new List<string>();
        if (!tenantId.HasValue)
        {
            conflicts.Add("The administrator session is not linked to an Allstarr tenant.");
        }

        var accountPreviews = BuildProviderPreviews(document, existingProviders, conflicts);
        var identityPlan = BuildBackendIdentityPlan(document, actor, existingBackendIdentities);
        document = document with
        {
            Playlists = PlanPlaylists(
                document.Playlists,
                identityPlan,
                existingProviders.Contains("spotify") ||
                accountPreviews.Any(item =>
                    item.ProviderId == "spotify" && item.Action == "create_disabled_if_missing"),
                existingPlaylistTargets)
        };
        var previewItems = new List<LegacyEnvPreviewItem>(document.Entries.Count);
        foreach (var entry in document.Entries)
        {
            var action = entry.Action;
            var reason = entry.Reason;
            long? existingRevision = null;
            if (entry.Key.Equals("JELLYFIN_USER_ID", StringComparison.OrdinalIgnoreCase) &&
                identityPlan.Create)
            {
                action = "import_backend_identity";
                reason = "Create the current administrator's durable Jellyfin backend identity.";
            }
            else if (entry.Disposition == LegacyEnvDisposition.PlaylistHandoff)
            {
                action = document.Playlists.All(item =>
                    item.Action is "import_playlist_link" or "conflict_existing")
                    ? "import_playlist_links"
                    : "requires_target_selection";
                reason = action == "import_playlist_links"
                    ? "Create durable disabled playlist links and schedules for administrator review."
                    : "At least one playlist still needs an explicit source account, backend target, or behavior review.";
            }
            if (entry.Disposition == LegacyEnvDisposition.DurableSetting)
            {
                if (entry.Value.Length == 0)
                {
                    action = "ignore_empty";
                    reason = "Empty values are not imported into durable settings.";
                }
                else if (!tenantId.HasValue)
                {
                    action = "conflict_missing_tenant";
                }
                else if (!IsValidRuntimeValue(entry.DurableKey!, entry.Value, out var validationError))
                {
                    action = "conflict_invalid_value";
                    reason = validationError;
                    conflicts.Add($"{entry.Key} has an invalid value and must be corrected before apply.");
                }
                else if (existingSettings.TryGetValue(entry.DurableKey!, out var current) &&
                         current.Origin == RuntimeSettingOrigin.Durable)
                {
                    action = "conflict_existing";
                    existingRevision = current.Revision;
                    conflicts.Add($"{entry.Key} already has a durable value and will not be overwritten.");
                }
            }
            else if (entry.Disposition == LegacyEnvDisposition.PerUserManual &&
                     PersonalProviderId(entry.Key) is { } personalProviderId)
            {
                if (entry.Value.Length == 0)
                {
                    action = "ignore_empty";
                    reason = "Empty personal credentials are not imported.";
                }
                else if (!tenantId.HasValue || !actor.ActorUserId.HasValue)
                {
                    action = "conflict_missing_user";
                    reason = "The administrator session is not linked to an Allstarr user.";
                    conflicts.Add($"{entry.Key} cannot be imported without a linked administrator user.");
                }
                else if (existingUserProviders.Contains(personalProviderId))
                {
                    action = "conflict_existing";
                    reason = $"Your {personalProviderId} account already exists and will not be overwritten.";
                }
                else
                {
                    action = "import_for_current_user";
                    reason = $"Import into your encrypted user-owned {personalProviderId} account.";
                }
            }
            else if (action == "conflict_invalid_value")
            {
                conflicts.Add($"{entry.Key} has an invalid value and must be corrected before apply.");
            }

            previewItems.Add(new(
                entry.Key,
                entry.LineNumber,
                ToWireName(entry.Disposition),
                action,
                reason,
                entry.Sensitive,
                entry.Sensitive ? null : entry.Value,
                entry.DurableKey,
                entry.ProviderId ?? PersonalProviderId(entry.Key),
                existingRevision,
                DuplicateScrobbleWarning(entry)));
        }

        var revision = await ComputeRevisionAsync(document.SourceSha256, tenantId, actor.ActorUserId, cancellationToken);
        var rawToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(rawToken);
        var expiresAt = _clock.UtcNow.Add(PreviewLifetime);
        var canApply = tenantId.HasValue &&
                       !previewItems.Any(item => item.Action is "conflict_missing_tenant" or "conflict_missing_user" or "conflict_invalid_value") &&
                       !accountPreviews.Any(item => item.Action is "conflict_incomplete" or "conflict_invalid_value");
        var state = new PreviewState(
            document,
            actor.SessionId,
            tenantId,
            actor.ActorUserId,
            actor.CorrelationId,
            revision,
            expiresAt,
            canApply,
            previewItems,
            accountPreviews);
        StorePreview(tokenHash, state);

        return new(
            rawToken,
            document.SourceSha256,
            LegacyEnvParser.ParserVersion,
            revision,
            expiresAt,
            canApply,
            previewItems.Count(item => item.Action == "import_if_absent"),
            accountPreviews.Count(item => item.Action == "create_disabled_if_missing") +
            previewItems.Where(item => item.Action == "import_for_current_user")
                .Select(item => item.ProviderId).Where(item => item != null)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            previewItems.Count(item => item.Action is "retain_in_deployment" or "per_user_manual" or
                "manual_review" or "deprecated_manual_review" or "requires_target_selection"),
            previewItems,
            accountPreviews,
            document.Playlists,
            conflicts,
            DuplicateAssignmentWarnings(document))
        {
            BackendIdentityCount = identityPlan.Create ? 1 : 0,
            PlaylistLinkCount = document.Playlists.Count(item => item.Action == "import_playlist_link"),
            ScheduleCount = document.Playlists.Count(item => item.Action == "import_playlist_link")
        };
    }

    public async Task<LegacyEnvMigrationApplyResult> ApplyAsync(
        string previewToken,
        string revision,
        bool confirmed,
        LegacyEnvMigrationActor actor,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        PurgeExpired();
        if (!confirmed)
        {
            throw new LegacyEnvMigrationException("confirmation_required", "Set confirmed=true after reviewing the preview.");
        }

        if (string.IsNullOrWhiteSpace(previewToken) || previewToken.Length > 200 ||
            revision.Length != 64 || revision.Any(character => !char.IsAsciiHexDigit(character)) ||
            !_previews.TryGetValue(HashToken(previewToken), out var state))
        {
            throw new LegacyEnvMigrationException("preview_invalid", "The migration preview token is invalid or expired.");
        }

        if (_clock.UtcNow > state.ExpiresAt)
        {
            state.ClearPlaintext();
            _previews.TryRemove(HashToken(previewToken), out _);
            throw new LegacyEnvMigrationException("preview_expired", "The migration preview has expired.");
        }

        if (!FixedEquals(state.SessionId, actor.SessionId) || state.TenantId != actor.TenantId ||
            state.ActorUserId != actor.ActorUserId)
        {
            throw new LegacyEnvMigrationException("preview_owner_mismatch", "The preview belongs to a different administrator session.");
        }

        if (!FixedEquals(state.Revision, revision))
        {
            throw new LegacyEnvMigrationException("revision_mismatch", "The submitted preview revision does not match.");
        }

        if (!state.TenantId.HasValue)
        {
            throw new LegacyEnvMigrationException("tenant_required", "The administrator session is not linked to an Allstarr tenant.");
        }

        if (!state.CanApply)
        {
            throw new LegacyEnvMigrationException(
                "preview_not_applicable",
                "Resolve the blocking preview conflicts before applying this migration.");
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Result != null)
            {
                return state.Result with { AlreadyApplied = true };
            }

            await ApplyGate.WaitAsync(cancellationToken);
            try
            {
                await using (var replayDb = await _factory.CreateDbContextAsync(cancellationToken))
                {
                    var replay = await FindPriorResultAsync(
                        replayDb,
                        state.Document.SourceSha256,
                        state.TenantId.Value,
                        cancellationToken);
                    if (replay != null)
                    {
                        state.Result = replay with { AlreadyApplied = true };
                        state.ClearPlaintext();
                        return state.Result;
                    }
                }

                var currentRevision = await ComputeRevisionAsync(
                    state.Document.SourceSha256,
                    state.TenantId,
                    state.ActorUserId,
                    cancellationToken);
                if (!FixedEquals(currentRevision, state.Revision))
                {
                    throw new LegacyEnvMigrationException(
                        "state_changed",
                        "Runtime settings or provider accounts changed after preview. Create a new preview.");
                }

                await using var db = await _factory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);

                var prior = await FindPriorResultAsync(
                    db,
                    state.Document.SourceSha256,
                    state.TenantId.Value,
                    cancellationToken);
                if (prior != null)
                {
                    state.Result = prior with { AlreadyApplied = true };
                    state.ClearPlaintext();
                    return state.Result;
                }

                var settingWrites = state.Items
                    .Where(item => item.Action == "import_if_absent" && item.DurableKey != null)
                    .Select(item =>
                    {
                        var sourceEntry = state.Document.Entries.Single(entry => entry.Key == item.Key);
                        return new RuntimeSettingWrite(item.DurableKey!, sourceEntry.Value, ExpectedRevision: null);
                    })
                    .ToArray();
                IReadOnlyList<StagedRuntimeSetting> stagedSettings = [];
                if (settingWrites.Length > 0)
                {
                    stagedSettings = await _settings.StageBatchAsync(
                        db,
                        state.TenantId.Value,
                        settingWrites,
                        "legacy-env-import",
                        state.ActorUserId,
                        cancellationToken);
                }

                var existingIdentities = state.ActorUserId.HasValue
                    ? await db.BackendIdentities.Where(item =>
                            item.TenantId == state.TenantId.Value &&
                            item.UserId == state.ActorUserId.Value)
                        .ToListAsync(cancellationToken)
                    : [];
                var identityPlan = BuildBackendIdentityPlan(state.Document, actor, existingIdentities);
                var createdIdentities = new List<BackendIdentityRecord>();
                if (identityPlan.Create)
                {
                    var identity = new BackendIdentityRecord
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = state.TenantId.Value,
                        UserId = state.ActorUserId!.Value,
                        BackendType = identityPlan.BackendType!,
                        BackendInstanceId = identityPlan.BackendInstanceId,
                        PrincipalId = identityPlan.PrincipalId!,
                        CreatedAt = _clock.UtcNow,
                        LastSeenAt = _clock.UtcNow
                    };
                    db.BackendIdentities.Add(identity);
                    createdIdentities.Add(identity);
                }

                var createdProviders = new List<string>();
                var createdProviderRecords = new List<ProviderAccountRecord>();
                foreach (var provider in state.ProviderAccounts.Where(item =>
                             item.Action == "create_disabled_if_missing"))
                {
                    if (await db.ProviderAccounts.AnyAsync(item =>
                            item.Scope == ProviderAccountScope.Global && item.TenantId == null &&
                            item.ProviderId == provider.ProviderId, cancellationToken))
                    {
                        throw new LegacyEnvMigrationException(
                            "provider_account_conflict",
                            $"A global {provider.ProviderId} account was created after preview.");
                    }

                    var account = new ProviderAccountRecord
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = null,
                        ProviderId = provider.ProviderId,
                        DisplayName = ImportedAccountName(provider.ProviderId, personal: false),
                        Scope = ProviderAccountScope.Global,
                        Enabled = false,
                        CreatedAt = _clock.UtcNow,
                        UpdatedAt = _clock.UtcNow
                    };
                    var secretBytes = BuildProviderSecret(state.Document, provider.ProviderId);
                    try
                    {
                        var stored = await _secrets.StoreWithinTransactionAsync(
                            db,
                            tenantId: null,
                            $"provider-account:{provider.ProviderId}:{account.Id:N}",
                            secretBytes,
                            cancellationToken: cancellationToken);
                        account.SecretReferenceId = stored.Id;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(secretBytes);
                    }

                    db.ProviderAccounts.Add(account);
                    createdProviders.Add(provider.ProviderId);
                    createdProviderRecords.Add(account);
                }

                var personalProviders = state.Items
                    .Where(item => item.Action == "import_for_current_user")
                    .Select(item => PersonalProviderId(item.Key))
                    .Where(item => item != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Cast<string>()
                    .ToArray();
                foreach (var providerId in personalProviders)
                {
                    if (!state.ActorUserId.HasValue)
                    {
                        throw new LegacyEnvMigrationException(
                            "user_required",
                            "The administrator session is not linked to an Allstarr user.");
                    }
                    if (await db.ProviderAccounts.AnyAsync(item =>
                            item.Scope == ProviderAccountScope.User &&
                            item.TenantId == state.TenantId.Value &&
                            item.OwnerUserId == state.ActorUserId.Value &&
                            item.ProviderId == providerId, cancellationToken))
                    {
                        throw new LegacyEnvMigrationException(
                            "provider_account_conflict",
                            $"Your {providerId} account was created after preview.");
                    }

                    var account = new ProviderAccountRecord
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = state.TenantId.Value,
                        OwnerUserId = state.ActorUserId.Value,
                        ProviderId = providerId,
                        DisplayName = ImportedAccountName(providerId, personal: true),
                        Scope = ProviderAccountScope.User,
                        Enabled = true,
                        CreatedAt = _clock.UtcNow,
                        UpdatedAt = _clock.UtcNow
                    };
                    var secretBytes = BuildPersonalProviderSecret(state.Document, providerId);
                    try
                    {
                        var stored = await _secrets.StoreWithinTransactionAsync(
                            db,
                            state.TenantId.Value,
                            $"provider-account:{providerId}:{account.Id:N}",
                            secretBytes,
                            cancellationToken: cancellationToken);
                        account.SecretReferenceId = stored.Id;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(secretBytes);
                    }

                    db.ProviderAccounts.Add(account);
                    createdProviders.Add(providerId);
                    createdProviderRecords.Add(account);
                }

                var spotifyAccount = createdProviderRecords.SingleOrDefault(item => item.ProviderId == "spotify") ??
                                     await db.ProviderAccounts.SingleOrDefaultAsync(item =>
                                         item.Scope == ProviderAccountScope.Global && item.TenantId == null &&
                                         item.ProviderId == "spotify", cancellationToken);
                var createdSchedules = new List<JobScheduleRecord>();
                var createdPlaylistLinks = new List<PlaylistLinkRecord>();
                foreach (var playlist in state.Document.Playlists.Where(item =>
                             item.Action == "import_playlist_link"))
                {
                    if (spotifyAccount == null || !state.ActorUserId.HasValue)
                    {
                        throw new LegacyEnvMigrationException(
                            "playlist_prerequisite_changed",
                            "The Spotify account or playlist owner changed after preview.");
                    }

                    var sourceHash = HashToken(playlist.SourcePlaylistId);
                    if (await db.PlaylistLinks.AnyAsync(item =>
                            item.TenantId == state.TenantId.Value &&
                            item.OwnerUserId == state.ActorUserId.Value &&
                            item.LibraryScopeId == playlist.LibraryScopeId &&
                            item.ProviderAccountId == spotifyAccount.Id &&
                            item.SourcePlaylistIdHash == sourceHash &&
                            item.TargetProtocol == playlist.TargetProtocol &&
                            item.TargetBackendInstanceId == playlist.TargetBackendInstanceId,
                            cancellationToken))
                    {
                        continue;
                    }

                    var schedule = new JobScheduleRecord
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = state.TenantId.Value,
                        OwnerUserId = state.ActorUserId.Value,
                        LibraryScopeId = playlist.LibraryScopeId,
                        JobType = DurableScheduleEngine.PlaylistSyncJobType,
                        CronExpression = playlist.SyncSchedule,
                        TimeZoneId = "UTC",
                        OverlapPolicy = ScheduleOverlapPolicy.Skip,
                        MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
                        RetryPolicyJson = """{"policy":"standard"}""",
                        PayloadTemplateJson = "{}",
                        Enabled = false,
                        CreatedAt = _clock.UtcNow,
                        UpdatedAt = _clock.UtcNow,
                        Revision = 1
                    };
                    var link = new PlaylistLinkRecord
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = state.TenantId.Value,
                        OwnerUserId = state.ActorUserId.Value,
                        ProviderAccountId = spotifyAccount.Id,
                        ScheduleId = schedule.Id,
                        Enabled = false,
                        LibraryScopeId = playlist.LibraryScopeId,
                        SourceProviderId = "spotify",
                        SourcePlaylistId = playlist.SourcePlaylistId,
                        SourcePlaylistIdHash = sourceHash,
                        TargetProtocol = playlist.TargetProtocol!,
                        TargetBackendInstanceId = playlist.TargetBackendInstanceId!,
                        TargetPlaylistId = string.IsNullOrWhiteSpace(playlist.JellyfinTargetPlaylistId)
                            ? null
                            : playlist.JellyfinTargetPlaylistId,
                        Mode = PlaylistLinkMode.Materialized,
                        MaterializationMode = PlaylistMaterializationMode.Reconcile,
                        PreserveManualEntries = true,
                        SyncName = true,
                        SyncDescription = true,
                        SyncArtwork = true,
                        RuleVersion = MigrationSchemaVersion,
                        PolicyVersion = MigrationSchemaVersion,
                        CreatedAt = _clock.UtcNow,
                        UpdatedAt = _clock.UtcNow,
                        Revision = 1
                    };
                    db.JobSchedules.Add(schedule);
                    db.PlaylistLinks.Add(link);
                    createdSchedules.Add(schedule);
                    createdPlaylistLinks.Add(link);
                }

                var appliedAt = _clock.UtcNow;
                var audit = new AuditEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = state.TenantId,
                    ActorUserId = state.ActorUserId,
                    Category = "configuration-migration",
                    Action = "legacy-env.apply",
                    Outcome = "succeeded",
                    CorrelationId = state.CorrelationId,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        sourceSha256 = state.Document.SourceSha256,
                        schemaVersion = MigrationSchemaVersion,
                        settingsImported = settingWrites.Length,
                        createdProviders,
                        backendIdentitiesCreated = createdIdentities.Count,
                        playlistLinksCreated = createdPlaylistLinks.Count,
                        schedulesCreated = createdSchedules.Count,
                        playlistHandoffsPending = state.Document.Playlists.Count(IsPlaylistHandoffPending)
                    }, JsonOptions),
                    CreatedAt = appliedAt
                };
                var appliedResult = new LegacyEnvMigrationApplyResult(
                    true,
                    false,
                    settingWrites.Length,
                    createdProviders.Count,
                    state.Items.Count(item => item.Action is "conflict_existing" or "ignore_empty"),
                    state.ProviderAccounts.Count(item => item.Action == "conflict_existing"),
                    state.Items.Count(item => item.Action is "retain_in_deployment" or "per_user_manual" or
                        "manual_review" or "deprecated_manual_review" or "requires_target_selection"),
                    state.Document.Playlists.Count(IsPlaylistHandoffPending),
                    createdProviders,
                    state.Document.SourceSha256,
                    appliedAt)
                {
                    BackendIdentitiesCreated = createdIdentities.Count,
                    PlaylistLinksCreated = createdPlaylistLinks.Count,
                    SchedulesCreated = createdSchedules.Count
                };
                db.AuditEvents.Add(audit);
                db.LegacyEnvImports.Add(new LegacyEnvImportRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = state.TenantId.Value,
                    SourceSha256 = state.Document.SourceSha256,
                    SchemaVersion = MigrationSchemaVersion,
                    ActorUserId = state.ActorUserId,
                    AuditEventId = audit.Id,
                    ResultJson = JsonSerializer.Serialize(appliedResult, JsonOptions),
                    ProvenanceJson = JsonSerializer.Serialize(new
                    {
                        settings = stagedSettings.Select(item => new
                        {
                            recordId = item.Record.Id,
                            item.Record.Key
                        }),
                        providerAccounts = createdProviderRecords.Select(item => new
                        {
                            recordId = item.Id,
                            item.ProviderId,
                            scope = item.Scope.ToString()
                        }),
                        backendIdentities = createdIdentities.Select(item => new
                        {
                            recordId = item.Id,
                            item.BackendType,
                            item.BackendInstanceId
                        }),
                        playlistLinks = createdPlaylistLinks.Select(item => new
                        {
                            recordId = item.Id,
                            item.SourceProviderId,
                            item.SourcePlaylistIdHash
                        }),
                        schedules = createdSchedules.Select(item => new
                        {
                            recordId = item.Id,
                            item.JobType
                        })
                    }, JsonOptions),
                    AppliedAt = appliedAt
                });

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                if (settingWrites.Length > 0)
                {
                    _settings.PublishExternalCommit();
                }

                state.Result = appliedResult;
                state.ClearPlaintext();
                return state.Result;
            }
            catch (Exception ex) when (IsPotentialIdempotencyRace(ex, cancellationToken))
            {
                var replay = await WaitForPriorResultAsync(
                    state.Document.SourceSha256,
                    state.TenantId.Value,
                    cancellationToken);
                if (replay != null)
                {
                    state.Result = replay with { AlreadyApplied = true };
                    state.ClearPlaintext();
                    return state.Result;
                }

                throw;
            }
            finally
            {
                ApplyGate.Release();
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static string ImportedAccountName(string providerId, bool personal)
    {
        var provider = providerId.ToLowerInvariant() switch
        {
            "lastfm" => "Last.fm",
            "listenbrainz" => "ListenBrainz",
            "qobuz" => "Qobuz",
            "deezer" => "Deezer",
            "spotify" => "Spotify",
            _ => providerId
        };
        return personal ? $"My {provider} account" : $"Shared {provider} account";
    }

    private async Task<string> ComputeRevisionAsync(
        string sourceSha256,
        Guid? tenantId,
        Guid? actorUserId,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(sourceSha256).Append('|').Append(tenantId?.ToString("N") ?? "none");
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (tenantId.HasValue)
        {
            var settings = await db.TenantRuntimeSettings.AsNoTracking()
                .Where(item => item.TenantId == tenantId.Value)
                .OrderBy(item => item.Key)
                .Select(item => new { item.Key, item.Revision })
                .ToListAsync(cancellationToken);
            foreach (var setting in settings)
            {
                builder.Append('|').Append(setting.Key).Append(':').Append(setting.Revision);
            }
        }

        var accounts = await db.ProviderAccounts.AsNoTracking()
            .Where(item =>
                item.Scope == ProviderAccountScope.Global && item.TenantId == null &&
                (item.ProviderId == "deezer" || item.ProviderId == "qobuz" || item.ProviderId == "spotify") ||
                tenantId.HasValue && actorUserId.HasValue && item.Scope == ProviderAccountScope.User &&
                item.TenantId == tenantId.Value && item.OwnerUserId == actorUserId.Value &&
                (item.ProviderId == "lastfm" || item.ProviderId == "listenbrainz"))
            .OrderBy(item => item.ProviderId).ThenBy(item => item.Id)
            .Select(item => new { item.ProviderId, item.Id, item.Revision })
            .ToListAsync(cancellationToken);
        foreach (var account in accounts)
        {
            builder.Append('|').Append(account.ProviderId).Append(':').Append(account.Id.ToString("N"))
                .Append(':').Append(account.Revision);
        }
        if (tenantId.HasValue && actorUserId.HasValue)
        {
            var identities = await db.BackendIdentities.AsNoTracking()
                .Where(item => item.TenantId == tenantId.Value && item.UserId == actorUserId.Value)
                .OrderBy(item => item.BackendType).ThenBy(item => item.BackendInstanceId)
                .Select(item => new { item.Id, item.BackendType, item.BackendInstanceId })
                .ToListAsync(cancellationToken);
            foreach (var identity in identities)
            {
                builder.Append("|identity:").Append(identity.Id.ToString("N")).Append(':')
                    .Append(identity.BackendType).Append(':').Append(identity.BackendInstanceId);
            }

            var links = await db.PlaylistLinks.AsNoTracking()
                .Where(item => item.TenantId == tenantId.Value && item.OwnerUserId == actorUserId.Value)
                .OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Revision, item.ScheduleId })
                .ToListAsync(cancellationToken);
            foreach (var link in links)
            {
                builder.Append("|playlist:").Append(link.Id.ToString("N")).Append(':')
                    .Append(link.Revision).Append(':').Append(link.ScheduleId?.ToString("N") ?? "none");
            }

            var scheduleIds = links.Where(item => item.ScheduleId.HasValue)
                .Select(item => item.ScheduleId!.Value).ToArray();
            var schedules = await db.JobSchedules.AsNoTracking()
                .Where(item => scheduleIds.Contains(item.Id))
                .OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Revision })
                .ToListAsync(cancellationToken);
            foreach (var schedule in schedules)
            {
                builder.Append("|schedule:").Append(schedule.Id.ToString("N")).Append(':')
                    .Append(schedule.Revision);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static IReadOnlyList<LegacyProviderAccountPreview> BuildProviderPreviews(
        LegacyEnvDocument document,
        IReadOnlySet<string> existingProviders,
        ICollection<string> conflicts)
    {
        var result = new List<LegacyProviderAccountPreview>();
        foreach (var group in document.Entries
                     .Where(item => item.Disposition == LegacyEnvDisposition.ProviderAccount)
                     .GroupBy(item => item.ProviderId!, StringComparer.OrdinalIgnoreCase))
        {
            var populated = group.Where(item => item.Value.Length > 0).ToArray();
            if (populated.Length == 0)
            {
                continue;
            }

            var invalid = populated.Any(item => item.Action == "conflict_invalid_value");
            var incomplete = populated.Any(item => item.Action == "conflict_incomplete");
            var action = invalid
                ? "conflict_invalid_value"
                : incomplete
                ? "conflict_incomplete"
                : existingProviders.Contains(group.Key)
                    ? "conflict_existing"
                    : "create_disabled_if_missing";
            var reason = action switch
            {
                "conflict_existing" => "A global account already exists; migration will not overwrite or duplicate it.",
                "conflict_invalid_value" => populated.First(item => item.Action == "conflict_invalid_value").Reason,
                "conflict_incomplete" => populated[0].Reason,
                _ => "Create a disabled global account with encrypted credentials for administrator review."
            };
            if (action.StartsWith("conflict_", StringComparison.Ordinal))
            {
                conflicts.Add($"{group.Key} provider credentials cannot be imported: {reason}");
            }

            result.Add(new(group.Key, action, populated.Select(item => item.Key).Order().ToArray(), false, reason));
        }

        return result;
    }

    private static LegacyBackendIdentityPlan BuildBackendIdentityPlan(
        LegacyEnvDocument document,
        LegacyEnvMigrationActor actor,
        IReadOnlyList<BackendIdentityRecord> existing)
    {
        string? Value(params string[] keys) => document.Entries
            .Where(item => keys.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(item => item.LineNumber)
            .Select(item => item.Value.Trim())
            .FirstOrDefault(item => item.Length > 0);

        var backendType = Value("BACKEND_TYPE", "Backend__Type")?.ToLowerInvariant();
        var instanceId = Value("ALLSTARR_BACKEND_INSTANCE_ID") ?? "primary";
        if (backendType == null && existing.Count == 1)
        {
            backendType = existing[0].BackendType;
            instanceId = existing[0].BackendInstanceId;
        }

        var matching = backendType == null
            ? null
            : existing.SingleOrDefault(item =>
                item.BackendType.Equals(backendType, StringComparison.OrdinalIgnoreCase) &&
                item.BackendInstanceId.Equals(instanceId, StringComparison.Ordinal));
        var principalId = matching?.PrincipalId ??
                          (backendType == "jellyfin" ? Value("JELLYFIN_USER_ID") : null);
        var create = matching == null && actor.TenantId.HasValue && actor.ActorUserId.HasValue &&
                     backendType == "jellyfin" && principalId != null;
        return new(backendType, instanceId, principalId, create, matching != null || create);
    }

    private static IReadOnlyList<LegacyPlaylistHandoff> PlanPlaylists(
        IReadOnlyList<LegacyPlaylistHandoff> playlists,
        LegacyBackendIdentityPlan identity,
        bool spotifyAccountReady,
        IReadOnlySet<string> existingTargets) =>
        playlists.Select(item =>
        {
            if (!spotifyAccountReady)
            {
                return item with
                {
                    Action = "requires_source_account",
                    Reason = "A Spotify provider account must be imported or selected before this playlist can become a durable link."
                };
            }
            if (!identity.Ready)
            {
                return item with
                {
                    Action = "requires_target_selection",
                    Reason = "An explicit durable backend identity is required before this playlist can become a durable link."
                };
            }
            if (item.LocalTracksPosition == "last")
            {
                return item with
                {
                    Action = "requires_behavior_review",
                    Reason = "The current playlist model preserves manual entries but cannot safely infer the legacy 'local tracks last' ordering rule."
                };
            }
            if (existingTargets.Contains(PlaylistTargetKey(
                    HashToken(item.SourcePlaylistId),
                    identity.BackendType!,
                    identity.BackendInstanceId)))
            {
                return item with
                {
                    Action = "conflict_existing",
                    Reason = "The matching durable playlist link already exists and will not be duplicated.",
                    TargetProtocol = identity.BackendType,
                    TargetBackendInstanceId = identity.BackendInstanceId
                };
            }
            return item with
            {
                Action = "import_playlist_link",
                Reason = "Create a disabled durable playlist link and schedule for administrator review.",
                TargetProtocol = identity.BackendType,
                TargetBackendInstanceId = identity.BackendInstanceId
            };
        }).ToArray();

    private static string PlaylistTargetKey(string sourceHash, string protocol, string backendInstanceId) =>
        $"{sourceHash}|{protocol.ToLowerInvariant()}|{backendInstanceId}";

    private static bool IsPlaylistHandoffPending(LegacyPlaylistHandoff playlist) =>
        playlist.Action.StartsWith("requires_", StringComparison.Ordinal);

    private static byte[] BuildProviderSecret(LegacyEnvDocument document, string providerId)
    {
        string? Value(string key) => document.Entries.SingleOrDefault(item =>
            item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
        object payload = providerId switch
        {
            "deezer" => new { arl = Value("DEEZER_ARL"), arlFallback = Value("DEEZER_ARL_FALLBACK") },
            "qobuz" => new { userAuthToken = Value("QOBUZ_USER_AUTH_TOKEN"), userId = Value("QOBUZ_USER_ID") },
            "spotify" => new
            {
                sessionCookie = Value("SPOTIFY_API_SESSION_COOKIE"),
                sessionCookieSetDate = Value("SPOTIFY_API_SESSION_COOKIE_SET_DATE")
            },
            _ => throw new InvalidOperationException("Unsupported legacy provider account.")
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
    }

    private static string? PersonalProviderId(string key) => key.ToUpperInvariant() switch
    {
        "SCROBBLING_LASTFM_API_KEY" or
        "SCROBBLING_LASTFM_SHARED_SECRET" or
        "SCROBBLING_LASTFM_USERNAME" or
        "SCROBBLING_LASTFM_PASSWORD" or
        "SCROBBLING_LASTFM_SESSION_KEY" => "lastfm",
        "SCROBBLING_LISTENBRAINZ_USER_TOKEN" => "listenbrainz",
        _ => null
    };

    private static byte[] BuildPersonalProviderSecret(LegacyEnvDocument document, string providerId)
    {
        string? Value(string key) => document.Entries.SingleOrDefault(item =>
            item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
        object payload = providerId switch
        {
            "lastfm" => new
            {
                apiKey = Value("SCROBBLING_LASTFM_API_KEY"),
                sharedSecret = Value("SCROBBLING_LASTFM_SHARED_SECRET"),
                username = Value("SCROBBLING_LASTFM_USERNAME"),
                password = Value("SCROBBLING_LASTFM_PASSWORD"),
                sessionKey = Value("SCROBBLING_LASTFM_SESSION_KEY")
            },
            "listenbrainz" => new { token = Value("SCROBBLING_LISTENBRAINZ_USER_TOKEN") },
            _ => throw new InvalidOperationException("Unsupported personal legacy provider account.")
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
    }

    private static async Task<LegacyEnvMigrationApplyResult?> FindPriorResultAsync(
        AllstarrDbContext db,
        string sourceSha256,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var receipt = await db.LegacyEnvImports.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.SourceSha256 == sourceSha256,
                cancellationToken);
        if (receipt == null)
        {
            return null;
        }

        try
        {
            if (!receipt.SchemaVersion.Equals(MigrationSchemaVersion, StringComparison.Ordinal))
            {
                throw new JsonException();
            }
            using var provenance = JsonDocument.Parse(receipt.ProvenanceJson);
            if (provenance.RootElement.ValueKind != JsonValueKind.Object ||
                !provenance.RootElement.TryGetProperty("settings", out var settings) ||
                settings.ValueKind != JsonValueKind.Array ||
                !provenance.RootElement.TryGetProperty("providerAccounts", out var accounts) ||
                accounts.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException();
            }
            var result = JsonSerializer.Deserialize<LegacyEnvMigrationApplyResult>(receipt.ResultJson, JsonOptions)
                         ?? throw new JsonException();
            if (!result.Success || !FixedEquals(result.SourceFingerprint, sourceSha256))
            {
                throw new JsonException();
            }

            return result with { AlreadyApplied = true };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The durable legacy import receipt is invalid.", ex);
        }
    }

    private async Task<LegacyEnvMigrationApplyResult?> WaitForPriorResultAsync(
        string sourceSha256,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var prior = await FindPriorResultAsync(db, sourceSha256, tenantId, cancellationToken);
            if (prior != null)
            {
                return prior;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        return null;
    }

    private static bool IsPotentialIdempotencyRace(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        (exception is DbUpdateException or DbException or RuntimeSettingConflictException ||
         exception is LegacyEnvMigrationException migration &&
         migration.Code is "provider_account_conflict" or "state_changed");

    private void PurgeExpired()
    {
        var now = _clock.UtcNow;
        foreach (var item in _previews.Where(item => item.Value.ExpiresAt < now).ToArray())
        {
            item.Value.ClearPlaintext();
            _previews.TryRemove(item.Key, out _);
        }
    }

    private void StorePreview(string tokenHash, PreviewState state)
    {
        _previews[tokenHash] = state;
        var overflow = _previews.Count - MaximumPreviewCount;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var item in _previews
                     .OrderBy(item => item.Value.ExpiresAt)
                     .Take(overflow)
                     .ToArray())
        {
            if (_previews.TryRemove(item.Key, out var removed))
            {
                removed.ClearPlaintext();
            }
        }
    }

    private static void ValidateActor(LegacyEnvMigrationActor actor)
    {
        if (string.IsNullOrWhiteSpace(actor.SessionId))
        {
            throw new LegacyEnvMigrationException("admin_session_required", "An administrator session is required.");
        }
    }

    private static string ToWireName(LegacyEnvDisposition disposition) => disposition switch
    {
        LegacyEnvDisposition.DurableSetting => "durable_setting",
        LegacyEnvDisposition.ProviderAccount => "provider_account",
        LegacyEnvDisposition.DeploymentChecklist => "deployment_checklist",
        LegacyEnvDisposition.PerUserManual => "per_user_manual",
        LegacyEnvDisposition.PlaylistHandoff => "playlist_handoff",
        LegacyEnvDisposition.IgnoredDeprecated => "ignored_deprecated",
        _ => "unknown"
    };

    private static string? DuplicateScrobbleWarning(LegacyEnvEntry entry) =>
        entry.Key.Equals("SCROBBLING_LOCAL_TRACKS_ENABLED", StringComparison.OrdinalIgnoreCase) &&
        bool.TryParse(entry.Value, out var enabled) && enabled
            ? "Local scrobbling can duplicate plays if the backend or another client also scrobbles them. Review per-user scrobbling targets before enabling it."
            : null;

    private static IReadOnlyList<string> DuplicateAssignmentWarnings(LegacyEnvDocument document) =>
        document.Entries
            .Where(entry => entry.OverriddenLineNumbers is { Count: > 0 })
            .Select(entry =>
                $"{entry.Key} is assigned more than once. The last active assignment on line {entry.LineNumber} is used; " +
                $"earlier active assignment{(entry.OverriddenLineNumbers!.Count == 1 ? string.Empty : "s")} on " +
                $"line{(entry.OverriddenLineNumbers.Count == 1 ? string.Empty : "s")} " +
                $"{string.Join(", ", entry.OverriddenLineNumbers)} {(entry.OverriddenLineNumbers.Count == 1 ? "is" : "are")} ignored.")
            .ToArray();

    private static bool IsValidRuntimeValue(string key, string raw, out string error)
    {
        var definition = RuntimeSettingCatalog.Require(key);
        var value = raw.Trim();
        var valid = definition.ValueType switch
        {
            RuntimeSettingValueType.Boolean => bool.TryParse(value, out _),
            RuntimeSettingValueType.Integer => int.TryParse(value, out var parsed) &&
                                               parsed >= definition.Minimum && parsed <= definition.Maximum,
            RuntimeSettingValueType.String => value.Length > 0 && value.Length <= definition.MaximumLength &&
                                              (definition.Choices == null || definition.Choices.Contains(value)),
            RuntimeSettingValueType.StringList => ValidateProviderList(value),
            _ => false
        };
        error = valid
            ? string.Empty
            : $"The value is not valid for durable setting {definition.Key}.";
        return valid;
    }

    private static bool ValidateProviderList(string raw)
    {
        string[] values;
        try
        {
            values = raw.TrimStart().StartsWith('[')
                ? JsonSerializer.Deserialize<string[]>(raw, JsonOptions) ?? []
                : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        catch (JsonException)
        {
            return false;
        }

        return values.Length <= 64 &&
               values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Length &&
               values.All(value => value.Length is > 0 and <= 100 &&
                                   value.All(character => char.IsAsciiLetterOrDigit(character) ||
                                                          character is '-' or '_' or '.'));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static bool FixedEquals(string? left, string? right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record LegacyBackendIdentityPlan(
        string? BackendType,
        string BackendInstanceId,
        string? PrincipalId,
        bool Create,
        bool Ready);

    private sealed class PreviewState(
        LegacyEnvDocument document,
        string sessionId,
        Guid? tenantId,
        Guid? actorUserId,
        string correlationId,
        string revision,
        DateTimeOffset expiresAt,
        bool canApply,
        IReadOnlyList<LegacyEnvPreviewItem> items,
        IReadOnlyList<LegacyProviderAccountPreview> providerAccounts)
    {
        public LegacyEnvDocument Document { get; private set; } = document;
        public string SessionId { get; } = sessionId;
        public Guid? TenantId { get; } = tenantId;
        public Guid? ActorUserId { get; } = actorUserId;
        public string CorrelationId { get; } = correlationId;
        public string Revision { get; } = revision;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool CanApply { get; } = canApply;
        public IReadOnlyList<LegacyEnvPreviewItem> Items { get; } = items;
        public IReadOnlyList<LegacyProviderAccountPreview> ProviderAccounts { get; } = providerAccounts;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public LegacyEnvMigrationApplyResult? Result { get; set; }

        public void ClearPlaintext() =>
            Document = new LegacyEnvDocument(Document.SourceSha256, [], []);
    }
}
