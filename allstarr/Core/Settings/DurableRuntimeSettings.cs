using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Settings;

public enum RuntimeSettingValueType { Boolean, Integer, String, StringList }
public enum RuntimeSettingOrigin { Bootstrap, Durable }

public sealed class TenantRuntimeSettingRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
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
    Task<EffectiveRuntimeSetting> GetAsync(Guid tenantId, string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, EffectiveRuntimeSetting>> GetManyAsync(
        Guid tenantId, IEnumerable<string> keys, CancellationToken cancellationToken = default);
    Task<RuntimeSettingBatchResult> ApplyBatchAsync(
        Guid tenantId, IReadOnlyList<RuntimeSettingWrite> writes, string source,
        Guid? actorUserId = null, CancellationToken cancellationToken = default);
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
    bool AllowEmpty = false, int MaximumLength = 500);

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
        Text("SquidWTF:Quality", ["LOW", "HIGH", "LOSSLESS", "FLAC", "HI_RES", "HI_RES_LOSSLESS"], allowEmpty: true);
        Int("SquidWTF:MinRequestIntervalMs", 0, 60000);
        Text("Deezer:Quality", ["FLAC", "MP3_320", "MP3_128"], allowEmpty: true); Int("Deezer:MinRequestIntervalMs", 0, 60000);
        Text("Qobuz:Quality", ["FLAC", "FLAC_24_HIGH", "FLAC_24_LOW", "FLAC_16", "MP3_320"], allowEmpty: true);
        Int("Qobuz:MinRequestIntervalMs", 0, 60000);
        items.Add(new("AppleDownload:BaseUrl", RuntimeSettingValueType.String, "AppleDownload:BaseUrl", AllowEmpty: true));
        items.Add(new("AppleDownload:Quality", RuntimeSettingValueType.String, "AppleDownload:Quality", AllowEmpty: true));
        items.Add(new("Providers:MetadataOrder", RuntimeSettingValueType.StringList, "MULTI_PROVIDER_METADATA_ORDER"));
        items.Add(new("Providers:DownloadOrder", RuntimeSettingValueType.StringList, "MULTI_PROVIDER_DOWNLOAD_ORDER"));
        items.Add(new("Providers:StreamingOrder", RuntimeSettingValueType.StringList, "MULTI_PROVIDER_STREAMING_ORDER"));
        items.Add(new("Providers:PlaylistOrder", RuntimeSettingValueType.StringList, "MULTI_PROVIDER_PLAYLIST_ORDER"));
        items.Add(new("Providers:LyricsOrder", RuntimeSettingValueType.StringList, "MULTI_PROVIDER_LYRICS_ORDER"));
        items.Add(new("Providers:EnabledSearch", RuntimeSettingValueType.StringList, "MULTI_PROVIDER_ENABLED_SEARCH"));
        items.Add(new("Providers:EnabledPlaylist", RuntimeSettingValueType.StringList, "MULTI_PROVIDER_ENABLED_PLAYLIST"));
        items.Add(new("Providers:Disabled", RuntimeSettingValueType.StringList, "MULTI_PROVIDER_DISABLED_PROVIDERS"));
        Bool("Library:EnableExternalPlaylists", "Jellyfin:EnableExternalPlaylists");
        Int("Matching:LocalPreferencePercent", 0, 20);
        Int("Matching:ExtensionPenaltyPercent", 0, 20);
        items.Add(new("Library:PlaylistsDirectory", RuntimeSettingValueType.String, "Jellyfin:PlaylistsDirectory"));
        items.Add(new("Library:ExplicitFilter", RuntimeSettingValueType.String, "Jellyfin:ExplicitFilter", Choices: new HashSet<string>(["All", "ExplicitOnly", "CleanOnly"], Comparer)));
        items.Add(new("Library:DownloadMode", RuntimeSettingValueType.String, "Jellyfin:DownloadMode", Choices: new HashSet<string>(["Track", "Album"], Comparer)));
        items.Add(new("Library:StorageMode", RuntimeSettingValueType.String, "Jellyfin:StorageMode", Choices: new HashSet<string>(["Cache", "Permanent"], Comparer)));
        Int("Library:CacheDurationHours", 1, 8760, "Jellyfin:CacheDurationHours");
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
        // tenant settings store makes first-run completion survive browsers and app restarts.
        Bool("WebUi:SetupCompleted");
        return items.ToDictionary(item => item.Key, Comparer);
    }
}

public sealed class DurableRuntimeSettingsService : IDurableRuntimeSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<AllstarrDbContext> _factory;
    private readonly IConfiguration _configuration;
    private readonly IPlatformClock _clock;
    private readonly RuntimeSettingsChangeSignal _signal;

    public DurableRuntimeSettingsService(IDbContextFactory<AllstarrDbContext> factory, IConfiguration configuration,
        IPlatformClock clock, RuntimeSettingsChangeSignal signal) =>
        (_factory, _configuration, _clock, _signal) = (factory, configuration, clock, signal);

    public async Task<EffectiveRuntimeSetting> GetAsync(Guid tenantId, string key, CancellationToken cancellationToken = default)
    {
        var definition = RuntimeSettingCatalog.Require(key);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var record = await db.TenantRuntimeSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Key == definition.Key, cancellationToken);
        return record == null ? FromBootstrap(definition) : FromRecord(record, definition);
    }

    public async Task<IReadOnlyDictionary<string, EffectiveRuntimeSetting>> GetManyAsync(
        Guid tenantId, IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var definitions = keys.Select(RuntimeSettingCatalog.Require).DistinctBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var canonical = definitions.Select(item => item.Key).ToArray();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var records = await db.TenantRuntimeSettings.AsNoTracking()
            .Where(item => item.TenantId == tenantId && canonical.Contains(item.Key)).ToListAsync(cancellationToken);
        var byKey = records.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        return definitions.ToDictionary(item => item.Key,
            item => byKey.TryGetValue(item.Key, out var record) ? FromRecord(record, item) : FromBootstrap(item),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<RuntimeSettingBatchResult> ApplyBatchAsync(Guid tenantId, IReadOnlyList<RuntimeSettingWrite> writes,
        string source, Guid? actorUserId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var staged = await StageBatchAsync(db, tenantId, writes, source, actorUserId, cancellationToken);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
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
        catch (DbUpdateConcurrencyException ex) { throw new RuntimeSettingConflictException("A runtime setting changed during the update.", ex); }
        await transaction.CommitAsync(cancellationToken);
        var version = _signal.Publish();
        return new(staged.Select(item => FromRecord(item.Record, item.Definition)).ToArray(), version);
    }

    public async Task<IReadOnlyList<StagedRuntimeSetting>> StageBatchAsync(AllstarrDbContext db, Guid tenantId,
        IReadOnlyList<RuntimeSettingWrite> writes, string source, Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db); ArgumentNullException.ThrowIfNull(writes);
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(source) || source.Trim().Length > 100 ||
            !source.Trim().All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':'))
            throw new ArgumentException("A bounded source identifier is required.", nameof(source));
        if (writes.Count == 0) throw new ArgumentException("At least one setting is required.", nameof(writes));
        var duplicate = writes.GroupBy(item => RuntimeSettingCatalog.Require(item.Key).Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null) throw new ArgumentException($"Runtime setting '{duplicate.Key}' appears more than once.", nameof(writes));
        if (!await db.Tenants.AnyAsync(item => item.Id == tenantId, cancellationToken)) throw new ArgumentException("The tenant does not exist.", nameof(tenantId));
        if (actorUserId is { } actor && !await db.Users.AnyAsync(item => item.Id == actor && item.TenantId == tenantId, cancellationToken))
            throw new ArgumentException("The actor does not belong to the tenant.", nameof(actorUserId));

        var definitions = writes.ToDictionary(item => RuntimeSettingCatalog.Require(item.Key).Key,
            item => (Write: item, Definition: RuntimeSettingCatalog.Require(item.Key)), StringComparer.OrdinalIgnoreCase);
        var normalizedWrites = definitions.ToDictionary(item => item.Key,
            item => Normalize(item.Value.Definition, item.Value.Write.RawValue), StringComparer.OrdinalIgnoreCase);
        var keys = definitions.Keys.ToArray();
        var existing = await db.TenantRuntimeSettings.Where(item => item.TenantId == tenantId && keys.Contains(item.Key))
            .ToDictionaryAsync(item => item.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var now = _clock.UtcNow;
        var result = new List<StagedRuntimeSetting>(writes.Count);
        foreach (var (key, pair) in definitions)
        {
            var normalized = normalizedWrites[key];
            if (!existing.TryGetValue(key, out var record))
            {
                if (pair.Write.ExpectedRevision is not null) throw new RuntimeSettingConflictException($"Runtime setting '{key}' does not exist at the expected revision.");
                record = new() { Id = Guid.NewGuid(), TenantId = tenantId, Key = key, CreatedAt = now, Revision = 1 };
                db.TenantRuntimeSettings.Add(record);
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

    public long PublishExternalCommit() => _signal.Publish();

    internal static void ValidateStoredRecord(TenantRuntimeSettingRecord record)
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

    private static EffectiveRuntimeSetting FromRecord(TenantRuntimeSettingRecord record, RuntimeSettingDefinition definition)
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

    private static string DefaultRaw(RuntimeSettingDefinition definition) => definition.ValueType switch
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

public sealed record StagedRuntimeSetting(TenantRuntimeSettingRecord Record, RuntimeSettingDefinition Definition);
public sealed class RuntimeSettingConflictException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

public static class DurableRuntimeSettingsRegistration
{
    public static IServiceCollection AddDurableRuntimeSettings(this IServiceCollection services)
    {
        services.AddSingleton<RuntimeSettingsChangeSignal>();
        services.AddSingleton<IRuntimeSettingsChangeSignal>(sp => sp.GetRequiredService<RuntimeSettingsChangeSignal>());
        services.AddSingleton<DurableRuntimeSettingsService>();
        services.AddSingleton<IDurableRuntimeSettings>(sp => sp.GetRequiredService<DurableRuntimeSettingsService>());
        return services;
    }
}
