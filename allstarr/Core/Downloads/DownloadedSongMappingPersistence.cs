using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Downloads;

public sealed class DownloadedSongMappingEntity
{
    public Guid Id { get; set; }
    public string ScopeKey { get; set; } = DownloadedSongMappingScope.LegacyKey;
    public Guid? ProviderAccountId { get; set; }
    public ProviderAudioQuality AudioQuality { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public DateTimeOffset DownloadedAt { get; set; }
    public long Revision { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CacheIdentityJson { get; set; }
}

public enum DownloadedSongRepresentation
{
    Playback,
    Original
}

public sealed record DownloadedSongMappingScope(
    Guid? ProviderAccountId,
    ProviderAudioQuality AudioQuality,
    DownloadedSongRepresentation Representation = DownloadedSongRepresentation.Playback,
    long? SourceAccountRevision = null)
{
    public static string LegacyKey { get; } = new DownloadedSongMappingScope(
        null,
        ProviderAudioQuality.Any).Key;

    public string Key
    {
        get
        {
            var account = ProviderAccountId?.ToString("N") ?? "anonymous";
            var quality = ((int)AudioQuality).ToString(CultureInfo.InvariantCulture);
            var value = Representation == DownloadedSongRepresentation.Playback && SourceAccountRevision == null
                ? string.Join('\u001f', account, quality)
                : string.Join('\u001f', account, quality, Representation.ToString(),
                    SourceAccountRevision?.ToString(CultureInfo.InvariantCulture) ?? "none");
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }

    public static bool TryResolve(DownloadedSongMappingEntity mapping, out DownloadedSongMappingScope scope)
    {
        scope = new(mapping.ProviderAccountId, mapping.AudioQuality);
        if (mapping.ScopeKey == scope.Key)
            return mapping.ProviderAccountId != null || mapping.ScopeKey == LegacyKey;
        try
        {
            if (mapping.CacheIdentityJson == null) return false;
            using var document = JsonDocument.Parse(mapping.CacheIdentityJson);
            var proof = document.RootElement;
            if (proof.ValueKind != JsonValueKind.Object ||
                !proof.TryGetProperty("Representation", out var representation) ||
                representation.ValueKind != JsonValueKind.String || representation.GetString() != "Original" ||
                !proof.TryGetProperty("SourceAccountRevision", out var sourceRevision)) return false;
            long? revision = null;
            if (sourceRevision.ValueKind != JsonValueKind.Null)
            {
                if (sourceRevision.ValueKind != JsonValueKind.Number || !sourceRevision.TryGetInt64(out var value) || value < 0)
                    return false;
                revision = value;
            }
            if (mapping.ProviderAccountId == null && revision != null) return false;
            scope = new(mapping.ProviderAccountId, mapping.AudioQuality, DownloadedSongRepresentation.Original, revision);
            return mapping.ScopeKey == scope.Key;
        }
        catch (JsonException) { return false; }
    }
}

public interface IDownloadedSongMappingStore
{
    Task<DownloadedSongMappingEntity?> FindAsync(
        string providerId,
        string externalId,
        CancellationToken cancellationToken = default);

    Task<DownloadedSongMappingEntity?> FindAsync(
        DownloadedSongMappingScope scope,
        string providerId,
        string externalId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        DownloadedSongMappingEntity mapping,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        Guid id,
        long revision,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DownloadedSongMappingEntity>> ListCacheMappingsAsync(CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<DownloadedSongMappingEntity>>([]);
    Task<DownloadedSongMappingEntity?> MarkCacheOwnedAsync(DownloadedSongMappingScope scope, string provider, string track,
        string path, string identityJson, CancellationToken token) => Task.FromResult<DownloadedSongMappingEntity?>(null);
    Task<bool> TouchCacheAsync(DownloadedSongMappingEntity expected, string identityJson, CancellationToken token) => Task.FromResult(false);
    Task<bool> RemoveCacheFileAsync(DownloadedSongMappingEntity expected, Func<bool> removeFile, CancellationToken token) => Task.FromResult(false);
}

public static class DownloadedSongMappingModelConfiguration
{
    public static void ConfigureDownloadedSongMappings(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DownloadedSongMappingEntity>(entity =>
        {
            entity.ToTable("downloaded_song_mappings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.ScopeKey).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ProviderId).HasMaxLength(100).IsRequired();
            entity.Property(item => item.ExternalId).HasMaxLength(500).IsRequired();
            entity.Property(item => item.LocalPath).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Artist).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Album).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasIndex(item => new { item.ScopeKey, item.ProviderId, item.ExternalId })
                .IsUnique()
                .HasDatabaseName("IX_downloaded_song_mapping_identity");
        });
    }
}

public sealed class EfDownloadedSongMappingStore(
    IDbContextFactory<AllstarrDbContext> factory) : IDownloadedSongMappingStore
{
    public async Task<DownloadedSongMappingEntity?> FindAsync(
        string providerId,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.DownloadedSongMappings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ScopeKey == DownloadedSongMappingScope.LegacyKey &&
                        item.ProviderId == providerId && item.ExternalId == externalId,
                cancellationToken);
    }

    public async Task<DownloadedSongMappingEntity?> FindAsync(
        DownloadedSongMappingScope scope,
        string providerId,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var scopeKey = scope.Key;
        return await db.DownloadedSongMappings.AsNoTracking().SingleOrDefaultAsync(
            item => item.ScopeKey == scopeKey &&
                    item.ProviderId == providerId && item.ExternalId == externalId,
            cancellationToken);
    }

    public async Task UpsertAsync(
        DownloadedSongMappingEntity mapping,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        mapping.CacheIdentityJson = null;
        var existing = await db.DownloadedSongMappings.SingleOrDefaultAsync(
            item => item.ScopeKey == mapping.ScopeKey &&
                    item.ProviderId == mapping.ProviderId && item.ExternalId == mapping.ExternalId,
            cancellationToken);
        if (existing is null)
        {
            db.DownloadedSongMappings.Add(mapping);
        }
        else
        {
            existing.LocalPath = mapping.LocalPath;
            existing.ProviderAccountId = mapping.ProviderAccountId;
            existing.AudioQuality = mapping.AudioQuality;
            existing.Title = mapping.Title;
            existing.Artist = mapping.Artist;
            existing.Album = mapping.Album;
            existing.DownloadedAt = mapping.DownloadedAt;
            existing.CacheIdentityJson = null;
            existing.Revision++;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(
        Guid id,
        long revision,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.DownloadedSongMappings
            .Where(item => item.Id == id && item.Revision == revision)
            .ExecuteDeleteAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<DownloadedSongMappingEntity>> ListCacheMappingsAsync(CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.DownloadedSongMappings.AsNoTracking().ToArrayAsync(token);
    }

    public async Task<DownloadedSongMappingEntity?> MarkCacheOwnedAsync(DownloadedSongMappingScope scope, string provider,
        string track, string path, string identityJson, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.DownloadedSongMappings.SingleOrDefaultAsync(item => item.ScopeKey == scope.Key &&
            item.ProviderId == provider && item.ExternalId == track && item.LocalPath == path, token);
        if (row == null) return null;
        row.CacheIdentityJson = identityJson;
        row.Revision++;
        await db.SaveChangesAsync(token);
        return row;
    }

    public async Task<bool> TouchCacheAsync(DownloadedSongMappingEntity expected, string identityJson, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.DownloadedSongMappings.Where(item => item.Id == expected.Id && item.Revision == expected.Revision &&
            item.LocalPath == expected.LocalPath && item.CacheIdentityJson == expected.CacheIdentityJson)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.CacheIdentityJson, identityJson)
                .SetProperty(item => item.Revision, item => item.Revision + 1), token) == 1;
    }

    public async Task<bool> RemoveCacheFileAsync(DownloadedSongMappingEntity expected, Func<bool> removeFile, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
        var current = await db.DownloadedSongMappings.AsNoTracking().SingleOrDefaultAsync(item => item.Id == expected.Id &&
            item.Revision == expected.Revision && item.LocalPath == expected.LocalPath &&
            item.CacheIdentityJson == expected.CacheIdentityJson && item.CacheIdentityJson != null, token);
        if (current == null || !removeFile()) return false;
        await db.DownloadedSongMappings.Where(item => item.Id == current.Id && item.Revision == current.Revision).ExecuteDeleteAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

}
