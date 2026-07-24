using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class ManualLyricsMappingRecord
{
    public Guid Id { get; set; }
    public string IdentityHash { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Album { get; set; }
    public int DurationSeconds { get; set; }
    public int LyricsId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public interface IManualLyricsMappingStore
{
    Task<int?> FindLyricsIdAsync(
        string artist,
        string title,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManualLyricsMappingRecord>> ListAsync(
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        string artist,
        string title,
        string? album,
        int durationSeconds,
        int lyricsId,
        CancellationToken cancellationToken = default);
}

public sealed class EfManualLyricsMappingStore(
    IDbContextFactory<AllstarrDbContext> contextFactory) : IManualLyricsMappingStore
{
    public async Task<int?> FindLyricsIdAsync(
        string artist,
        string title,
        CancellationToken cancellationToken = default)
    {
        var identityHash = BuildIdentityHash(artist, title);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ManualLyricsMappings
            .AsNoTracking()
            .Where(item => item.IdentityHash == identityHash)
            .Select(item => (int?)item.LyricsId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ManualLyricsMappingRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ManualLyricsMappings
            .AsNoTracking()
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertAsync(
        string artist,
        string title,
        string? album,
        int durationSeconds,
        int lyricsId,
        CancellationToken cancellationToken = default)
    {
        var identityHash = BuildIdentityHash(artist, title);
        var now = DateTimeOffset.UtcNow;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var record = await context.ManualLyricsMappings
            .SingleOrDefaultAsync(item => item.IdentityHash == identityHash, cancellationToken);
        if (record is null)
        {
            record = new ManualLyricsMappingRecord
            {
                Id = Guid.NewGuid(),
                IdentityHash = identityHash,
                CreatedAt = now,
            };
            context.ManualLyricsMappings.Add(record);
        }

        record.Artist = artist.Trim();
        record.Title = title.Trim();
        record.Album = string.IsNullOrWhiteSpace(album) ? null : album.Trim();
        record.DurationSeconds = Math.Max(0, durationSeconds);
        record.LyricsId = lyricsId;
        record.UpdatedAt = now;

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string BuildIdentityHash(string artist, string title)
    {
        var normalized = $"{Normalize(artist)}\n{Normalize(title)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

internal static class ManualLyricsMappingModelConfiguration
{
    public static void ConfigureManualLyricsMappings(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManualLyricsMappingRecord>(entity =>
        {
            entity.ToTable("manual_lyrics_mappings");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.IdentityHash).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Artist).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Album).HasMaxLength(500);
            entity.HasIndex(item => item.IdentityHash).IsUnique();
            entity.HasIndex(item => item.UpdatedAt);
        });
    }
}
