using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Downloads;

public sealed class DownloadedSongMappingEntity
{
    public Guid Id { get; set; }
    public string ScopeKey { get; set; } = DownloadedSongMappingScope.LegacyKey;
    public Guid? TenantId { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string? LibraryScopeId { get; set; }
    public ProviderAudioQuality AudioQuality { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public DateTimeOffset DownloadedAt { get; set; }
    public long Revision { get; set; }
}

public sealed record DownloadedSongMappingScope(
    Guid TenantId,
    Guid? ProviderAccountId,
    string? LibraryScopeId,
    ProviderAudioQuality AudioQuality)
{
    public const string LegacyKey = "legacy";

    public string Key => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f',
        TenantId.ToString("N"),
        ProviderAccountId?.ToString("N") ?? string.Empty,
        LibraryScopeId?.Trim().ToLowerInvariant() ?? string.Empty,
        ((int)AudioQuality).ToString(CultureInfo.InvariantCulture)))));
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
            entity.Property(item => item.LibraryScopeId).HasMaxLength(300);
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
            existing.TenantId = mapping.TenantId;
            existing.ProviderAccountId = mapping.ProviderAccountId;
            existing.LibraryScopeId = mapping.LibraryScopeId;
            existing.AudioQuality = mapping.AudioQuality;
            existing.Title = mapping.Title;
            existing.Artist = mapping.Artist;
            existing.Album = mapping.Album;
            existing.DownloadedAt = mapping.DownloadedAt;
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
}
