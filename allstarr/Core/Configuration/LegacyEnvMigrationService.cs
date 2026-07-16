using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
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
    IReadOnlyList<string> Warnings);

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
    DateTimeOffset AppliedAt);

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
    private const string ImportedAccountName = "Legacy .env import";
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);
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
        }

        var conflicts = new List<string>();
        if (!tenantId.HasValue)
        {
            conflicts.Add("The administrator session is not linked to an Allstarr tenant.");
        }

        var previewItems = new List<LegacyEnvPreviewItem>(document.Entries.Count);
        foreach (var entry in document.Entries)
        {
            var action = entry.Action;
            var reason = entry.Reason;
            long? existingRevision = null;
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

            previewItems.Add(new(
                entry.Key,
                entry.LineNumber,
                ToWireName(entry.Disposition),
                action,
                reason,
                entry.Sensitive,
                entry.Sensitive ? "configured" : entry.Value,
                entry.DurableKey,
                entry.ProviderId,
                existingRevision,
                DuplicateScrobbleWarning(entry)));
        }

        var accountPreviews = BuildProviderPreviews(document, existingProviders, conflicts);
        var revision = await ComputeRevisionAsync(document.SourceSha256, tenantId, cancellationToken);
        var rawToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(rawToken);
        var expiresAt = _clock.UtcNow.Add(PreviewLifetime);
        var canApply = tenantId.HasValue &&
                       !previewItems.Any(item => item.Action is "conflict_missing_tenant" or "conflict_invalid_value") &&
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
        _previews[tokenHash] = state;

        return new(
            rawToken,
            document.SourceSha256,
            LegacyEnvParser.ParserVersion,
            revision,
            expiresAt,
            canApply,
            previewItems.Count(item => item.Action == "import_if_absent"),
            accountPreviews.Count(item => item.Action == "create_disabled_if_missing"),
            previewItems.Count(item => item.Action is "retain_in_deployment" or "per_user_manual" or
                "manual_review" or "deprecated_manual_review" or "requires_target_selection"),
            previewItems,
            accountPreviews,
            document.Playlists,
            conflicts,
            DuplicateAssignmentWarnings(document));
    }

    public async Task<LegacyEnvMigrationApplyResult> ApplyAsync(
        string previewToken,
        string revision,
        bool confirmed,
        LegacyEnvMigrationActor actor,
        CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
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
                if (settingWrites.Length > 0)
                {
                    await _settings.StageBatchAsync(
                        db,
                        state.TenantId.Value,
                        settingWrites,
                        "legacy-env-import",
                        state.ActorUserId,
                        cancellationToken);
                }

                var createdProviders = new List<string>();
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
                        DisplayName = ImportedAccountName,
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
                        settingsImported = settingWrites.Length,
                        createdProviders,
                        playlistHandoffsPending = state.Document.Playlists.Count
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
                    state.Document.Playlists.Count,
                    createdProviders,
                    state.Document.SourceSha256,
                    appliedAt);
                db.AuditEvents.Add(audit);
                db.LegacyEnvImports.Add(new LegacyEnvImportRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = state.TenantId.Value,
                    SourceSha256 = state.Document.SourceSha256,
                    ActorUserId = state.ActorUserId,
                    AuditEventId = audit.Id,
                    ResultJson = JsonSerializer.Serialize(appliedResult, JsonOptions),
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

    private async Task<string> ComputeRevisionAsync(
        string sourceSha256,
        Guid? tenantId,
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
            .Where(item => item.Scope == ProviderAccountScope.Global && item.TenantId == null &&
                           (item.ProviderId == "deezer" || item.ProviderId == "qobuz" || item.ProviderId == "spotify"))
            .OrderBy(item => item.ProviderId).ThenBy(item => item.Id)
            .Select(item => new { item.ProviderId, item.Id, item.Revision })
            .ToListAsync(cancellationToken);
        foreach (var account in accounts)
        {
            builder.Append('|').Append(account.ProviderId).Append(':').Append(account.Id.ToString("N"))
                .Append(':').Append(account.Revision);
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
