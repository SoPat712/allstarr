using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Settings;

public enum RuntimeSettingValueType { Boolean, Integer, String, StringList }
public enum RuntimeSettingOrigin { Bootstrap, Durable }

public sealed class RuntimeSettingRecord
{
    public Guid Id { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string Key { get; set; } = string.Empty;
    public RuntimeSettingValueType ValueType { get; set; }
    public string ValueJson { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed record RuntimeSettingWrite(string Key, string RawValue, long? ExpectedRevision = null);

public sealed record EffectiveRuntimeSetting(
    string Key,
    RuntimeSettingValueType ValueType,
    object Value,
    string NormalizedValue,
    RuntimeSettingOrigin Origin,
    long? Revision,
    string? Source,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record RuntimeSettingBatchResult(
    IReadOnlyList<EffectiveRuntimeSetting> Settings,
    long ChangeVersion);

public interface IDurableRuntimeSettings
{
    Task<EffectiveRuntimeSetting> GetAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, EffectiveRuntimeSetting>> GetManyAsync(
        IEnumerable<string> keys, CancellationToken cancellationToken = default);
    Task<RuntimeSettingBatchResult> ApplyBatchAsync(
        IReadOnlyList<RuntimeSettingWrite> writes, string source,
        Guid? actorUserId = null, CancellationToken cancellationToken = default);
    Task<PersonalListeningPreferences> GetPreferencesAsync(
        Guid? userId, CancellationToken cancellationToken = default);
    Task<PersonalListeningPreferences> UpdatePreferencesAsync(
        Guid userId, ListeningPreferences? overrides, string expectedRevision,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeSettingsChangeSignal
{
    long Version { get; }
    event Action<long>? Changed;
}

public sealed class RuntimeSettingsChangeSignal : IRuntimeSettingsChangeSignal
{
    private long _version;
    public long Version => Interlocked.Read(ref _version);
    public event Action<long>? Changed;

    public long Publish()
    {
        var version = Interlocked.Increment(ref _version);
        if (Changed != null)
        {
            foreach (Action<long> subscriber in Changed.GetInvocationList())
            {
                try { subscriber(version); }
                catch { /* A refresh observer cannot roll back an already committed setting. */ }
            }
        }
        return version;
    }
}

public sealed record RuntimeSettingDefinition(
    string Key, RuntimeSettingValueType ValueType, string BootstrapKey,
    int? Minimum = null, int? Maximum = null, IReadOnlySet<string>? Choices = null,
    bool AllowEmpty = false, int MaximumLength = 500, string? DefaultValue = null);

public static class RuntimeSettingCatalog
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Dictionary<string, RuntimeSettingDefinition> DefinitionsInternal = Build();
    public static IReadOnlyDictionary<string, RuntimeSettingDefinition> Definitions { get; } =
        new ReadOnlyDictionary<string, RuntimeSettingDefinition>(DefinitionsInternal);

    public static RuntimeSettingDefinition Require(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !DefinitionsInternal.TryGetValue(key.Trim(), out var definition))
            throw new ArgumentException($"Runtime setting '{key}' is not supported.", nameof(key));
        return definition;
    }

    private static Dictionary<string, RuntimeSettingDefinition> Build()
    {
        var items = new List<RuntimeSettingDefinition>();
        void Bool(string key, string? bootstrap = null) => items.Add(new(key, RuntimeSettingValueType.Boolean, bootstrap ?? key));
        void Int(string key, int min, int max, string? bootstrap = null) => items.Add(new(key, RuntimeSettingValueType.Integer, bootstrap ?? key, min, max));
        void Text(string key, string[] choices, bool allowEmpty = false) =>
            items.Add(new(key, RuntimeSettingValueType.String, key,
                Choices: choices.ToHashSet(Comparer), AllowEmpty: allowEmpty));
        Int("Cache:SearchResultsMinutes", 1, 1440); Int("Cache:PlaylistImagesHours", 1, 8760);
        Int("Cache:LyricsDays", 1, 3650); Int("Cache:GenreDays", 1, 3650); Int("Cache:MetadataDays", 1, 3650);
        Int("Cache:OdesliLookupDays", 1, 3650); Int("Cache:ProxyImagesDays", 1, 3650);
        Int("Cache:TranscodeCacheMinutes", 1, 10080);
        items.Add(new("Cache:MediaDirectory", RuntimeSettingValueType.String, "Cache:MediaDirectory", AllowEmpty: true));
        Int("Cache:MediaMaximumMegabytes", 1, 1048576);
        Int("Cache:MediaMaximumEntryMegabytes", 1, 1024);
        Int("Cache:MediaCleanupFileLimit", 100, 1000000);
        Text(AudioQualityPolicy.SettingKey, AudioQualityPolicy.Steps.ToArray());
        Text("Deezer:Quality", ["FLAC", "MP3_320", "MP3_128"], allowEmpty: true); Int("Deezer:MinRequestIntervalMs", 0, 60000);
        Text("Qobuz:Quality", ["FLAC", "FLAC_24_HIGH", "FLAC_24_LOW", "FLAC_16", "MP3_320"], allowEmpty: true);
        Int("Qobuz:MinRequestIntervalMs", 0, 60000);
        items.Add(new("AppleDownload:BaseUrl", RuntimeSettingValueType.String, "AppleDownload:BaseUrl", AllowEmpty: true));
        items.Add(new("AppleDownload:Quality", RuntimeSettingValueType.String, "AppleDownload:Quality", AllowEmpty: true));
        items.AddRange(ProviderOrderPolicyCatalog.Definitions.Select(item => new RuntimeSettingDefinition(
            item.SettingKey,
            RuntimeSettingValueType.StringList,
            item.BootstrapKey,
            DefaultValue: item.DefaultValue)));
        items.Add(new("Providers:EnabledSearch", RuntimeSettingValueType.StringList, "MULTI_PROVIDER_ENABLED_SEARCH"));
        items.Add(new("Providers:EnabledPlaylist", RuntimeSettingValueType.StringList, "MULTI_PROVIDER_ENABLED_PLAYLIST"));
        items.Add(new("Providers:Disabled", RuntimeSettingValueType.StringList, "MULTI_PROVIDER_DISABLED_PROVIDERS"));
        Bool("Library:EnableExternalPlaylists", "Jellyfin:EnableExternalPlaylists");
        Int("Matching:LocalPreferencePercent", 0, 20);
        items.Add(new("Library:PlaylistsDirectory", RuntimeSettingValueType.String, "Jellyfin:PlaylistsDirectory"));
        items.Add(new("Library:ExplicitFilter", RuntimeSettingValueType.String, "Jellyfin:ExplicitFilter",
            Choices: new HashSet<string>(["All", "ExplicitOnly", "CleanOnly"], Comparer), DefaultValue: "All"));
        items.Add(new("Playback:ShowExternalLabel", RuntimeSettingValueType.Boolean,
            "Playback:ShowExternalLabel", DefaultValue: "true"));
        items.Add(new("Playback:ShowExplicitLabel", RuntimeSettingValueType.Boolean,
            "Playback:ShowExplicitLabel", DefaultValue: "true"));
        items.Add(new("Library:DownloadMode", RuntimeSettingValueType.String, "Jellyfin:DownloadMode", Choices: new HashSet<string>(["Track", "Album"], Comparer)));
        items.Add(new("Library:StorageMode", RuntimeSettingValueType.String, "Jellyfin:StorageMode", Choices: new HashSet<string>(["Cache", "Permanent"], Comparer)));
        Int("Library:CacheDurationHours", 1, 8760, "Jellyfin:CacheDurationHours");
        items.Add(new("ProviderAccounts:ListenersCanConnectOwnAccounts", RuntimeSettingValueType.Boolean,
            "ProviderAccounts:ListenersCanConnectOwnAccounts", DefaultValue: "true"));
        Bool("MusicBrainz:Enabled"); Bool("SpotifyApi:Enabled");
        Int("SpotifyApi:CacheDurationMinutes", 1, 10080); Int("SpotifyApi:RateLimitDelayMs", 0, 60000);
        items.Add(new("SpotifyApi:LyricsApiUrl", RuntimeSettingValueType.String,
            "SpotifyApi:LyricsApiUrl", AllowEmpty: true));
        Bool("SpotifyApi:PreferIsrcMatching"); Bool("SpotifyImport:Enabled");
        Int("SpotifyImport:MatchingIntervalHours", 0, 8760);
        items.Add(new("SpotifyImport:Playlists", RuntimeSettingValueType.String,
            "SpotifyImport:Playlists", MaximumLength: 65536, AllowEmpty: true));
        Bool("Scrobbling:Enabled"); Bool("Scrobbling:LocalTracksEnabled");
        Bool("Scrobbling:SyntheticLocalPlayedSignalEnabled"); Bool("Scrobbling:LastFm:Enabled");
        Bool("Scrobbling:ListenBrainz:Enabled");
        // This is application state rather than deployment configuration. Keeping it in the
        // settings store makes first-run completion survive browsers and app restarts.
        Bool("WebUi:SetupCompleted");
        return items.ToDictionary(item => item.Key, Comparer);
    }
}

public sealed class DurableRuntimeSettingsService : BackgroundService, IDurableRuntimeSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<AllstarrDbContext> _factory;
    private readonly IConfiguration _configuration;
    private readonly IPlatformClock _clock;
    private readonly RuntimeSettingsChangeSignal _signal;
    private readonly RuntimeSettingsLiveOptions? _liveOptions;
    private readonly ILogger<DurableRuntimeSettingsService>? _logger;
    private readonly SemaphoreSlim _refresh = new(0, 1);

    public DurableRuntimeSettingsService(IDbContextFactory<AllstarrDbContext> factory, IConfiguration configuration,
        IPlatformClock clock, RuntimeSettingsChangeSignal signal,
        RuntimeSettingsLiveOptions? liveOptions = null,
        ILogger<DurableRuntimeSettingsService>? logger = null) =>
        (_factory, _configuration, _clock, _signal, _liveOptions, _logger) =
        (factory, configuration, clock, signal, liveOptions, logger);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_liveOptions != null)
        {
            _signal.Changed += OnSettingsChanged;
            await ProjectLiveOptionsAsync(cancellationToken);
        }
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _signal.Changed -= OnSettingsChanged;
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_liveOptions == null) return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _refresh.WaitAsync(stoppingToken);
                await ProjectLiveOptionsAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void OnSettingsChanged(long _)
    {
        try { _refresh.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task ProjectLiveOptionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _liveOptions!.ProjectAsync(this, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Failed to project durable runtime settings");
        }
    }

    public async Task<EffectiveRuntimeSetting> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var definition = RuntimeSettingCatalog.Require(key);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var record = await db.RuntimeSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OwnerUserId == null && item.Key == definition.Key, cancellationToken);
        return record == null ? FromBootstrap(definition) : FromRecord(record, definition);
    }

    public async Task<IReadOnlyDictionary<string, EffectiveRuntimeSetting>> GetManyAsync(
        IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var definitions = keys.Select(RuntimeSettingCatalog.Require).DistinctBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var canonical = definitions.Select(item => item.Key).ToArray();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var records = await db.RuntimeSettings.AsNoTracking()
            .Where(item => item.OwnerUserId == null && canonical.Contains(item.Key))
            .ToListAsync(cancellationToken);
        var byKey = records.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        return definitions.ToDictionary(item => item.Key,
            item => byKey.TryGetValue(item.Key, out var record) ? FromRecord(record, item) : FromBootstrap(item),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<RuntimeSettingBatchResult> ApplyBatchAsync(IReadOnlyList<RuntimeSettingWrite> writes,
        string source, Guid? actorUserId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var staged = await StageBatchAsync(db, writes, source, actorUserId, cancellationToken);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = actorUserId,
            Category = "runtime-settings",
            Action = "runtime-settings.batch-apply",
            Outcome = "succeeded",
            CorrelationId = $"runtime-settings:{Guid.NewGuid():N}",
            DetailsJson = JsonSerializer.Serialize(new
            {
                source = source.Trim(),
                settings = staged.Select(item => new { item.Record.Key, item.Record.Revision }).ToArray()
            }, JsonOptions),
            CreatedAt = _clock.UtcNow
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (Exception ex) when (IsWriteConflict(ex))
        { throw new RuntimeSettingConflictException("A runtime setting changed during the update.", ex); }
        await transaction.CommitAsync(cancellationToken);
        var settings = staged.Select(item => FromRecord(item.Record, item.Definition)).ToArray();
        _liveOptions?.ApplyCommitted(settings);
        var version = _signal.Publish();
        return new(settings, version);
    }

    public async Task<IReadOnlyList<StagedRuntimeSetting>> StageBatchAsync(AllstarrDbContext db,
        IReadOnlyList<RuntimeSettingWrite> writes, string source, Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
        => await StageScopedBatchAsync(db, null, writes, source, actorUserId, cancellationToken);

    private async Task<IReadOnlyList<StagedRuntimeSetting>> StageScopedBatchAsync(
        AllstarrDbContext db, Guid? ownerUserId,
        IReadOnlyList<RuntimeSettingWrite> writes, string source, Guid? actorUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db); ArgumentNullException.ThrowIfNull(writes);
        if (string.IsNullOrWhiteSpace(source) || source.Trim().Length > 100 ||
            !source.Trim().All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':'))
            throw new ArgumentException("A bounded source identifier is required.", nameof(source));
        if (writes.Count == 0) throw new ArgumentException("At least one setting is required.", nameof(writes));
        var duplicate = writes.GroupBy(item => RuntimeSettingCatalog.Require(item.Key).Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null) throw new ArgumentException($"Runtime setting '{duplicate.Key}' appears more than once.", nameof(writes));
        if (actorUserId is { } actor && !await db.Users.AnyAsync(
                item => item.Id == actor && item.Enabled, cancellationToken))
            throw new ArgumentException("The actor is not an active user.", nameof(actorUserId));

        var definitions = writes.ToDictionary(item => RuntimeSettingCatalog.Require(item.Key).Key,
            item => (Write: item, Definition: RuntimeSettingCatalog.Require(item.Key)), StringComparer.OrdinalIgnoreCase);
        var normalizedWrites = definitions.ToDictionary(item => item.Key,
            item => Normalize(item.Value.Definition, item.Value.Write.RawValue), StringComparer.OrdinalIgnoreCase);
        if (ownerUserId.HasValue && definitions.Keys.Any(key => !ListeningPreferenceKeys.All.Contains(key)))
            throw new ArgumentException("Personal runtime settings contain an unsupported key.", nameof(writes));
        var keys = definitions.Keys.ToArray();
        var existing = await db.RuntimeSettings
            .Where(item => item.OwnerUserId == ownerUserId && keys.Contains(item.Key))
            .ToDictionaryAsync(item => item.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var now = _clock.UtcNow;
        var result = new List<StagedRuntimeSetting>(writes.Count);
        foreach (var (key, pair) in definitions)
        {
            var normalized = normalizedWrites[key];
            if (!existing.TryGetValue(key, out var record))
            {
                if (pair.Write.ExpectedRevision is not null) throw new RuntimeSettingConflictException($"Runtime setting '{key}' does not exist at the expected revision.");
                record = new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerUserId,
                    Key = key,
                    CreatedAt = now,
                    Revision = 1
                };
                db.RuntimeSettings.Add(record);
            }
            else
            {
                if (pair.Write.ExpectedRevision is null || pair.Write.ExpectedRevision != record.Revision)
                    throw new RuntimeSettingConflictException($"Runtime setting '{key}' already exists or has a different revision.");
                db.Entry(record).Property(item => item.Revision).OriginalValue = pair.Write.ExpectedRevision.Value;
                record.Revision++;
            }
            record.ValueType = pair.Definition.ValueType; record.ValueJson = normalized.Json;
            record.Source = source.Trim(); record.UpdatedByUserId = actorUserId; record.UpdatedAt = now;
            result.Add(new(record, pair.Definition));
        }
        return result;
    }

    public async Task<PersonalListeningPreferences> GetPreferencesAsync(
        Guid? userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await RequirePreferenceScopeAsync(db, userId, cancellationToken);
        return await ReadPreferencesAsync(db, userId, cancellationToken);
    }

    public async Task<PersonalListeningPreferences> UpdatePreferencesAsync(
        Guid userId, ListeningPreferences? overrides, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(expectedRevision))
            throw new ArgumentException("An expected preferences revision is required.", nameof(expectedRevision));

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RequirePreferenceScopeAsync(db, userId, cancellationToken);
        var current = await ReadPreferencesAsync(db, userId, cancellationToken);
        if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
            throw new RuntimeSettingConflictException("Listening preferences changed during the update.");

        var action = overrides == null ? "runtime-settings.personal-reset" : "runtime-settings.personal-update";
        if (overrides == null)
        {
            var personalRows = await db.RuntimeSettings
                .Where(item => item.OwnerUserId == userId && ListeningPreferenceKeys.All.Contains(item.Key))
                .ToListAsync(cancellationToken);
            db.RuntimeSettings.RemoveRange(personalRows);
        }
        else
        {
            var existingRevisions = await db.RuntimeSettings.AsNoTracking()
                .Where(item => item.OwnerUserId == userId && ListeningPreferenceKeys.All.Contains(item.Key))
                .ToDictionaryAsync(item => item.Key, item => (long?)item.Revision,
                    StringComparer.OrdinalIgnoreCase, cancellationToken);
            var writes = new[]
            {
                new RuntimeSettingWrite(ListeningPreferenceKeys.ExplicitFilter, overrides.ExplicitFilter,
                    existingRevisions.GetValueOrDefault(ListeningPreferenceKeys.ExplicitFilter)),
                new RuntimeSettingWrite(ListeningPreferenceKeys.ShowExternalLabel,
                    overrides.ShowExternalLabel.ToString(CultureInfo.InvariantCulture),
                    existingRevisions.GetValueOrDefault(ListeningPreferenceKeys.ShowExternalLabel)),
                new RuntimeSettingWrite(ListeningPreferenceKeys.ShowExplicitLabel,
                    overrides.ShowExplicitLabel.ToString(CultureInfo.InvariantCulture),
                    existingRevisions.GetValueOrDefault(ListeningPreferenceKeys.ShowExplicitLabel))
            };
            _ = await StageScopedBatchAsync(db, userId, writes, "personal-preferences", userId,
                cancellationToken);
        }

        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = userId,
            Category = "runtime-settings",
            Action = action,
            Outcome = "succeeded",
            CorrelationId = $"runtime-settings:{Guid.NewGuid():N}",
            DetailsJson = JsonSerializer.Serialize(new
            {
                scope = "personal",
                settings = ListeningPreferenceKeys.All.OrderBy(key => key, StringComparer.Ordinal).ToArray()
            }, JsonOptions),
            CreatedAt = _clock.UtcNow
        });

        PersonalListeningPreferences updated;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            updated = await ReadPreferencesAsync(db, userId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (IsWriteConflict(ex))
        { throw new RuntimeSettingConflictException("Listening preferences changed during the update.", ex); }
        _signal.Publish();
        return updated;
    }

    private static async Task RequirePreferenceScopeAsync(
        AllstarrDbContext db, Guid? userId, CancellationToken cancellationToken)
    {
        if (userId.HasValue && !await db.Users.AsNoTracking().AnyAsync(
                item => item.Id == userId.Value && item.Enabled, cancellationToken))
            throw new UnauthorizedAccessException("An active user is required.");
    }

    private async Task<PersonalListeningPreferences> ReadPreferencesAsync(
        AllstarrDbContext db, Guid? userId, CancellationToken cancellationToken)
    {
        var rows = await db.RuntimeSettings.AsNoTracking()
            .Where(item => (item.OwnerUserId == null || userId.HasValue && item.OwnerUserId == userId) &&
                           ListeningPreferenceKeys.All.Contains(item.Key))
            .ToListAsync(cancellationToken);
        var householdRows = rows.Where(item => item.OwnerUserId == null)
            .ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        var personalRows = rows.Where(item => item.OwnerUserId == userId && userId.HasValue)
            .ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        var householdSettings = ListeningPreferenceKeys.All.ToDictionary(
            key => key,
            key => householdRows.TryGetValue(key, out var row)
                ? FromRecord(row, RuntimeSettingCatalog.Require(key))
                : FromBootstrap(RuntimeSettingCatalog.Require(key)),
            StringComparer.OrdinalIgnoreCase);
        var household = ToListeningPreferences(householdSettings);
        var effectiveSettings = ListeningPreferenceKeys.All.ToDictionary(
            key => key,
            key => personalRows.TryGetValue(key, out var row)
                ? FromRecord(row, RuntimeSettingCatalog.Require(key))
                : householdSettings[key],
            StringComparer.OrdinalIgnoreCase);
        var effective = ToListeningPreferences(effectiveSettings);
        return new(effective, household, personalRows.Count == 0,
            ComputePreferencesRevision(userId, householdSettings, personalRows));
    }

    private static ListeningPreferences ToListeningPreferences(
        IReadOnlyDictionary<string, EffectiveRuntimeSetting> settings) => new(
        (string)settings[ListeningPreferenceKeys.ExplicitFilter].Value,
        (bool)settings[ListeningPreferenceKeys.ShowExternalLabel].Value,
        (bool)settings[ListeningPreferenceKeys.ShowExplicitLabel].Value);

    private static string ComputePreferencesRevision(
        Guid? userId,
        IReadOnlyDictionary<string, EffectiveRuntimeSetting> household,
        IReadOnlyDictionary<string, RuntimeSettingRecord> personal)
    {
        var input = new StringBuilder()
            .Append("user=").Append(userId?.ToString("N", CultureInfo.InvariantCulture) ?? "household");
        foreach (var key in ListeningPreferenceKeys.All.OrderBy(key => key, StringComparer.Ordinal))
        {
            var setting = household[key];
            input.Append(";h:").Append(key).Append(':').Append(setting.ValueType).Append(':')
                .Append(setting.NormalizedValue).Append(':').Append(setting.Revision?.ToString(CultureInfo.InvariantCulture) ?? "bootstrap");
            if (personal.TryGetValue(key, out var row))
                input.Append(";p:").Append(key).Append(':').Append(row.Id.ToString("N", CultureInfo.InvariantCulture))
                    .Append(':').Append(row.Revision.ToString(CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString()))).ToLowerInvariant();
    }

    private static bool IsWriteConflict(Exception exception) =>
        DbErrors.IsTransientConflict(exception) || DbErrors.IsUniqueViolation(exception);

    public long PublishExternalCommit() => _signal.Publish();

    internal static void ValidateStoredRecord(RuntimeSettingRecord record)
    {
        var definition = RuntimeSettingCatalog.Require(record.Key);
        if (record.ValueType != definition.ValueType) throw new InvalidOperationException($"Runtime setting '{record.Key}' has an invalid stored type.");
        _ = ParseStored(definition, record.ValueJson);
    }

    private EffectiveRuntimeSetting FromBootstrap(RuntimeSettingDefinition definition)
    {
        var bootstrapKey = ResolveBootstrapKey(definition);
        var raw = _configuration[bootstrapKey] ?? DefaultRaw(definition);
        var normalized = Normalize(definition, raw);
        return new(definition.Key, definition.ValueType, normalized.Value, normalized.Display,
            RuntimeSettingOrigin.Bootstrap, null, null, null, null);
    }

    private string ResolveBootstrapKey(RuntimeSettingDefinition definition)
    {
        if (!definition.Key.StartsWith("Library:", StringComparison.Ordinal)) return definition.BootstrapKey;
        var backend = _configuration["Backend:Type"] ?? "Jellyfin";
        return $"{(backend.Equals("Subsonic", StringComparison.OrdinalIgnoreCase) ? "Subsonic" : "Jellyfin")}:{definition.Key[8..]}";
    }

    private static EffectiveRuntimeSetting FromRecord(RuntimeSettingRecord record, RuntimeSettingDefinition definition)
    {
        if (record.ValueType != definition.ValueType) throw new InvalidOperationException($"Runtime setting '{record.Key}' has an invalid stored type.");
        var normalized = ParseStored(definition, record.ValueJson);
        return new(record.Key, record.ValueType, normalized.Value, normalized.Display, RuntimeSettingOrigin.Durable,
            record.Revision, record.Source, record.CreatedAt, record.UpdatedAt);
    }

    private static (object Value, string Display, string Json) Normalize(RuntimeSettingDefinition definition, string raw)
    {
        raw ??= string.Empty;
        return definition.ValueType switch
        {
            RuntimeSettingValueType.Boolean when bool.TryParse(raw.Trim(), out var value) => (value, value ? "true" : "false", JsonSerializer.Serialize(value, JsonOptions)),
            RuntimeSettingValueType.Integer when int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                value >= definition.Minimum && value <= definition.Maximum => (value, value.ToString(CultureInfo.InvariantCulture), JsonSerializer.Serialize(value, JsonOptions)),
            RuntimeSettingValueType.StringList => NormalizeList(definition, raw),
            RuntimeSettingValueType.String => NormalizeString(definition, raw),
            _ => throw new ArgumentException($"Runtime setting '{definition.Key}' has an invalid {definition.ValueType} value.")
        };
    }

    private static (object Value, string Display, string Json) NormalizeString(RuntimeSettingDefinition definition, string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0 && definition.AllowEmpty)
            return (string.Empty, string.Empty, JsonSerializer.Serialize(string.Empty, JsonOptions));
        if (value.Length == 0 || value.Length > definition.MaximumLength ||
            definition.Choices is { Count: > 0 } && !definition.Choices.Contains(value))
            throw new ArgumentException($"Runtime setting '{definition.Key}' has an invalid string value.");
        if (definition.Key == "Library:PlaylistsDirectory" &&
            (value is "." or ".." || value.Contains('/') || value.Contains('\\') || value.Contains('\0')))
            throw new ArgumentException("Library:PlaylistsDirectory must be a single safe directory name.");
        var canonical = definition.Choices?.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? value;
        return (canonical, canonical, JsonSerializer.Serialize(canonical, JsonOptions));
    }

    private static (object Value, string Display, string Json) NormalizeList(
        RuntimeSettingDefinition definition,
        string raw)
    {
        string[] parts;
        if (raw.TrimStart().StartsWith('[')) parts = JsonSerializer.Deserialize<string[]>(raw, JsonOptions) ?? [];
        else parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var values = parts.Select(item => item.Trim().ToLowerInvariant())
            .Where(item => item.Length > 0 &&
                           (definition.Key != "Providers:LyricsOrder" || item != "lyricsplus"))
            .ToArray();
        if (values.Length > 64 || values.Any(item => item.Length > 100 || !item.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')) ||
            values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
            throw new ArgumentException("A provider list contains an invalid or duplicate provider ID.");
        var json = JsonSerializer.Serialize(values, JsonOptions);
        if (json.Length > 4096) throw new ArgumentException("A provider list exceeds the durable setting size limit.");
        return (values, string.Join(',', values), json);
    }

    private static (object Value, string Display, string Json) ParseStored(RuntimeSettingDefinition definition, string json)
    {
        try
        {
            return definition.ValueType switch
            {
                RuntimeSettingValueType.Boolean => Normalize(definition, JsonSerializer.Deserialize<bool>(json, JsonOptions).ToString()),
                RuntimeSettingValueType.Integer => Normalize(definition, JsonSerializer.Deserialize<int>(json, JsonOptions).ToString(CultureInfo.InvariantCulture)),
                RuntimeSettingValueType.String => Normalize(definition, JsonSerializer.Deserialize<string>(json, JsonOptions) ?? string.Empty),
                RuntimeSettingValueType.StringList => NormalizeList(definition, json),
                _ => throw new InvalidOperationException()
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new InvalidOperationException($"Runtime setting '{definition.Key}' has an invalid stored value.", ex); }
    }

    private static string DefaultRaw(RuntimeSettingDefinition definition) => definition.DefaultValue ?? definition.ValueType switch
    {
        RuntimeSettingValueType.String when definition.Key == AudioQualityPolicy.SettingKey => AudioQualityPolicy.DefaultStep,
        RuntimeSettingValueType.Boolean => "false",
        RuntimeSettingValueType.Integer => definition.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "0",
        RuntimeSettingValueType.StringList => string.Empty,
        RuntimeSettingValueType.String when definition.AllowEmpty => string.Empty,
        RuntimeSettingValueType.String when definition.Choices?.Count > 0 => definition.Choices.First(),
        _ => "default"
    };
}

public sealed record StagedRuntimeSetting(RuntimeSettingRecord Record, RuntimeSettingDefinition Definition);
public sealed class RuntimeSettingConflictException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

public sealed class RuntimeSettingsLiveOptions
{
    private readonly ProviderAccountOptions _providerAccounts;
    private readonly IConfiguration _configuration;
    private readonly CacheSettings _cache;
    private readonly DeezerSettings _deezer;
    private readonly QobuzSettings _qobuz;
    private readonly AppleDownloadSettings _apple;
    private readonly SpotifyApiSettings _spotifyApi;
    private readonly SpotifyImportSettings _spotifyImport;
    private readonly MusicBrainzSettings _musicBrainz;
    private readonly ScrobblingSettings _scrobbling;
    private readonly JellyfinSettings _jellyfin;
    private readonly SubsonicSettings _subsonic;
    private readonly string? _bootstrapAppleBaseUrl;

    public RuntimeSettingsLiveOptions(
        IConfiguration configuration, IOptions<CacheSettings> cache, IOptions<DeezerSettings> deezer,
        IOptions<QobuzSettings> qobuz, IOptions<AppleDownloadSettings> apple,
        IOptions<SpotifyApiSettings> spotifyApi, IOptions<SpotifyImportSettings> spotifyImport,
        IOptions<MusicBrainzSettings> musicBrainz, IOptions<ScrobblingSettings> scrobbling,
        IOptions<JellyfinSettings> jellyfin, IOptions<SubsonicSettings> subsonic,
        ProviderAccountOptions providerAccounts)
    {
        (_configuration, _providerAccounts) = (configuration, providerAccounts);
        _bootstrapAppleBaseUrl = configuration["AppleDownload:BaseUrl"];
        (_cache, _deezer, _qobuz, _apple) = (cache.Value, deezer.Value, qobuz.Value, apple.Value);
        (_spotifyApi, _spotifyImport, _musicBrainz, _scrobbling) =
            (spotifyApi.Value, spotifyImport.Value, musicBrainz.Value, scrobbling.Value);
        (_jellyfin, _subsonic) = (jellyfin.Value, subsonic.Value);
    }

    public async Task ProjectAsync(IDurableRuntimeSettings settings, CancellationToken cancellationToken)
    {
        var values = await settings.GetManyAsync(RuntimeSettingCatalog.Definitions.Keys, cancellationToken);
        foreach (var setting in values.Values.Where(item =>
                     item.Origin == RuntimeSettingOrigin.Durable && item.Key != AudioQualityPolicy.SettingKey))
            Apply(setting);

        var audio = values[AudioQualityPolicy.SettingKey];
        if (audio.Origin != RuntimeSettingOrigin.Durable &&
            _configuration[AudioQualityPolicy.SettingKey] == null &&
            LegacyQualityIsDurable(values))
        {
            var migrated = AudioQualityPolicy.FromProviderCeilings(
                LegacyQuality(values, "AppleDownload:Quality", _apple.Quality),
                LegacyQuality(values, "Deezer:Quality", _deezer.Quality),
                LegacyQuality(values, "Qobuz:Quality", _qobuz.Quality));
            await settings.ApplyBatchAsync(
                [new RuntimeSettingWrite(AudioQualityPolicy.SettingKey, migrated)],
                "audio-quality-migration",
                cancellationToken: cancellationToken);
        }
    }

    internal void ApplyCommitted(IEnumerable<EffectiveRuntimeSetting> settings)
    {
        foreach (var setting in settings) Apply(setting);
    }

    private static bool LegacyQualityIsDurable(IReadOnlyDictionary<string, EffectiveRuntimeSetting> values) =>
        values["AppleDownload:Quality"].Origin == RuntimeSettingOrigin.Durable ||
        values["Deezer:Quality"].Origin == RuntimeSettingOrigin.Durable ||
        values["Qobuz:Quality"].Origin == RuntimeSettingOrigin.Durable;

    private static string? LegacyQuality(
        IReadOnlyDictionary<string, EffectiveRuntimeSetting> values,
        string key,
        string? bootstrap) =>
        values[key].Origin == RuntimeSettingOrigin.Durable ? values[key].NormalizedValue : bootstrap;

    private void Apply(EffectiveRuntimeSetting setting)
    {
        var value = setting.Value;
        switch (setting.Key)
        {
            case ProviderAccountOptions.ListenerConnectionsKey: _providerAccounts.ListenersCanConnectOwnAccounts = (bool)value; break;
            case "Cache:SearchResultsMinutes": _cache.SearchResultsMinutes = (int)value; break;
            case "Cache:PlaylistImagesHours": _cache.PlaylistImagesHours = (int)value; break;
            case "Cache:LyricsDays": _cache.LyricsDays = (int)value; break;
            case "Cache:GenreDays": _cache.GenreDays = (int)value; break;
            case "Cache:MetadataDays": _cache.MetadataDays = (int)value; break;
            case "Cache:OdesliLookupDays": _cache.OdesliLookupDays = (int)value; break;
            case "Cache:ProxyImagesDays": _cache.ProxyImagesDays = (int)value; break;
            case "Cache:TranscodeCacheMinutes": _cache.TranscodeCacheMinutes = (int)value; break;
            case "Deezer:MinRequestIntervalMs": _deezer.MinRequestIntervalMs = (int)value; break;
            case "Qobuz:MinRequestIntervalMs": _qobuz.MinRequestIntervalMs = (int)value; break;
            case "AppleDownload:BaseUrl":
                if (string.IsNullOrWhiteSpace(_bootstrapAppleBaseUrl)) _apple.BaseUrl = (string)value;
                break;
            case "MusicBrainz:Enabled": _musicBrainz.Enabled = (bool)value; break;
            case "SpotifyApi:Enabled": _spotifyApi.Enabled = (bool)value; break;
            case "SpotifyApi:CacheDurationMinutes": _spotifyApi.CacheDurationMinutes = (int)value; break;
            case "SpotifyApi:RateLimitDelayMs": _spotifyApi.RateLimitDelayMs = (int)value; break;
            case "SpotifyApi:LyricsApiUrl": _spotifyApi.LyricsApiUrl = (string)value; break;
            case "SpotifyApi:PreferIsrcMatching": _spotifyApi.PreferIsrcMatching = (bool)value; break;
            case "SpotifyImport:Enabled": _spotifyImport.Enabled = (bool)value; break;
            case "SpotifyImport:MatchingIntervalHours": _spotifyImport.MatchingIntervalHours = (int)value; break;
            case "SpotifyImport:Playlists": _spotifyImport.Playlists = SpotifyPlaylistConfigParser.Parse((string)value); break;
            case "Scrobbling:Enabled": _scrobbling.Enabled = (bool)value; break;
            case "Scrobbling:LocalTracksEnabled": _scrobbling.LocalTracksEnabled = (bool)value; break;
            case "Scrobbling:SyntheticLocalPlayedSignalEnabled": _scrobbling.SyntheticLocalPlayedSignalEnabled = (bool)value; break;
            case "Scrobbling:LastFm:Enabled": _scrobbling.LastFm.Enabled = (bool)value; break;
            case "Scrobbling:ListenBrainz:Enabled": _scrobbling.ListenBrainz.Enabled = (bool)value; break;
            case "Library:EnableExternalPlaylists": SetBoth(item => item.EnableExternalPlaylists = (bool)value, item => item.EnableExternalPlaylists = (bool)value); break;
            case "Library:PlaylistsDirectory": SetBoth(item => item.PlaylistsDirectory = (string)value, item => item.PlaylistsDirectory = (string)value); break;
            case "Library:DownloadMode": SetBoth(item => item.DownloadMode = Enum.Parse<DownloadMode>((string)value), item => item.DownloadMode = Enum.Parse<DownloadMode>((string)value)); break;
            case "Library:StorageMode": SetBoth(item => item.StorageMode = Enum.Parse<StorageMode>((string)value), item => item.StorageMode = Enum.Parse<StorageMode>((string)value)); break;
            case "Library:CacheDurationHours": SetBoth(item => item.CacheDurationHours = (int)value, item => item.CacheDurationHours = (int)value); break;
        }
    }

    private void SetBoth(Action<JellyfinSettings> jellyfin, Action<SubsonicSettings> subsonic)
    {
        jellyfin(_jellyfin);
        subsonic(_subsonic);
    }
}

public static class DurableRuntimeSettingsRegistration
{
    public static IServiceCollection AddDurableRuntimeSettings(this IServiceCollection services)
    {
        services.AddSingleton<RuntimeSettingsChangeSignal>();
        services.AddSingleton<IRuntimeSettingsChangeSignal>(sp => sp.GetRequiredService<RuntimeSettingsChangeSignal>());
        services.AddSingleton<RuntimeSettingsLiveOptions>();
        services.AddSingleton<DurableRuntimeSettingsService>();
        services.AddSingleton<IDurableRuntimeSettings>(sp => sp.GetRequiredService<DurableRuntimeSettingsService>());
        services.AddHostedService<DurableRuntimeSettingsService>(sp =>
            sp.GetRequiredService<DurableRuntimeSettingsService>());
        services.AddSingleton<IEffectiveProviderPolicyResolver, EffectiveProviderPolicyResolver>();
        return services;
    }
}
