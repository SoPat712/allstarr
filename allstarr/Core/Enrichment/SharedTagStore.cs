using System.Globalization;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

internal sealed record SharedTagDocument(IReadOnlyList<SharedTagEvidence> Evidence, IReadOnlyList<string> ArtistKeys, bool HistoryCaptured = false);

public sealed class SharedTagStore(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock)
{
    private readonly SemaphoreSlim writes = new(1, 1);
    public static string RecordingKey(Guid id) => $"recording:{id:D}";
    public static string ArtistKey(string id) => "artist:" + (id.StartsWith("musicbrainz:", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParse(id[12..], out var mbid) ? $"musicbrainz:{mbid:D}" : id);
    internal static SharedTagDocument Read(SharedTagProfileRecord? row) => row == null
        ? new([], []) : JsonSerializer.Deserialize<SharedTagDocument>(row.PayloadJson) ?? new([], []);

    public Task CaptureAudioMuseAsync(Guid recording, IReadOnlyList<SharedTagInput> moods, double? energy, double? tempo,
        CancellationToken token) => SaveAsync(RecordingKey(recording), [new("audiomuse",
            moods.OrderByDescending(item => item.Weight ?? 0).Take(5).Concat(new[] { new SharedTagInput("energy", energy), new SharedTagInput("tempo", tempo) }
                .Where(item => item.Weight != null)).ToArray(), clock.UtcNow)], [], false, token);

    public Task CaptureAudioMuseAsync(Guid recording, string? moodVector, string? otherFeatures, string? topGenre, string? topMood,
        CancellationToken token)
    {
        var tags = new List<SharedTagInput>();
        foreach (var part in ((moodVector ?? "") + "," + (otherFeatures ?? "")).Split(','))
        {
            var pair = part.Split(':');
            if (pair.Length == 2 && double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) && double.IsFinite(weight))
                tags.Add(new(pair[0].Trim(), weight));
        }
        if (!string.IsNullOrWhiteSpace(topGenre)) tags.Add(new(topGenre, IsGenre: true));
        if (!string.IsNullOrWhiteSpace(topMood)) tags.Add(new(topMood));
        return SaveAsync(RecordingKey(recording), [new("audiomuse", tags, clock.UtcNow)], [], false, token);
    }

    public async Task SaveAsync(string key, IReadOnlyList<SharedTagBatch> batches, IReadOnlyList<string> artistKeys,
        bool refreshed, CancellationToken token, IReadOnlyList<string>? artistNames = null, DateTimeOffset? capturedAt = null, bool historyCaptured = false)
    {
        await writes.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var row = await db.SharedTagProfiles.FindAsync([key], token);
            var document = Read(row);
            var incoming = SharedTagNormalization.Evidence(batches, artistNames ?? []);
            var sources = batches.Select(item => item.Source).ToHashSet(StringComparer.Ordinal);
            var evidence = document.Evidence.Where(item => !sources.Contains(item.Source)).Concat(incoming).ToArray();
            row ??= new() { Key = key };
            if (db.Entry(row).State == EntityState.Detached) db.SharedTagProfiles.Add(row);
            row.PayloadJson = JsonSerializer.Serialize(new SharedTagDocument(evidence,
                document.ArtistKeys.Concat(artistKeys).Distinct(StringComparer.Ordinal).Take(10).ToArray(), document.HistoryCaptured || historyCaptured));
            if (capturedAt != null) row.CapturedAt = capturedAt.Value;
            if (refreshed) row.RefreshedAt = clock.UtcNow;
            await db.SaveChangesAsync(token);
        }
        finally { writes.Release(); }
    }

    public async Task<SharedSongTags> GetTags(Guid recording, CancellationToken token = default) =>
        (await GetTags([recording], token))[recording];

    public async Task<IReadOnlyDictionary<Guid, SharedSongTags>> GetTags(IReadOnlyCollection<Guid> recordings,
        CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var ids = recordings.Distinct().ToArray();
        var canonical = await db.CanonicalRecordings.AsNoTracking().Where(item => ids.Contains(item.Id) ||
            item.MergedIntoId != null && ids.Contains(item.MergedIntoId.Value)).ToArrayAsync(token);
        var roots = ids.ToDictionary(id => id, id => canonical.FirstOrDefault(item => item.Id == id)?.MergedIntoId ?? id);
        var rootIds = roots.Values.Distinct().ToArray();
        var aliases = await db.CanonicalRecordings.AsNoTracking().Where(item => item.MergedIntoId != null &&
            rootIds.Contains(item.MergedIntoId.Value)).ToArrayAsync(token);
        var keys = rootIds.Concat(aliases.Select(item => item.Id)).Select(RecordingKey).ToArray();
        var rows = await db.SharedTagProfiles.AsNoTracking().Where(item => keys.Contains(item.Key)).ToArrayAsync(token);
        var documents = rows.ToDictionary(item => item.Key, Read);
        var artistKeys = documents.Values.SelectMany(item => item.ArtistKeys).Distinct().Select(ArtistKey).ToArray();
        var artists = (await db.SharedTagProfiles.AsNoTracking().Where(item => artistKeys.Contains(item.Key)).ToArrayAsync(token))
            .ToDictionary(item => item.Key, Read);
        return roots.ToDictionary(item => item.Key, item =>
        {
            var related = aliases.Where(alias => alias.MergedIntoId == item.Value).Select(alias => alias.Id).Append(item.Value)
                .Select(id => documents.GetValueOrDefault(RecordingKey(id))).OfType<SharedTagDocument>().ToArray();
            var evidence = related.SelectMany(document => document.Evidence).Concat(related.SelectMany(document => document.ArtistKeys)
                .Distinct().SelectMany(key => artists.GetValueOrDefault(ArtistKey(key))?.Evidence ?? []));
            return new SharedSongTags(item.Value, SharedTagNormalization.Combine(evidence));
        });
    }
}
