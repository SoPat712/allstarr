using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Services.Common;

public sealed record ProviderCtsTrackSelection(string TrackId, string Label, int CorpusSize);

public sealed class ProviderCtsTrackSelector(
    IDbContextFactory<AllstarrDbContext> contextFactory) : IDisposable
{
    public const int CorpusLimit = 100;
    private static readonly TimeSpan RotationLifetime = TimeSpan.FromHours(24);
    private readonly MemoryCache _nextIndexes = new(new MemoryCacheOptions
    {
        SizeLimit = 256
    });
    private readonly object _rotationLock = new();

    public async Task<ProviderCtsTrackSelection?> SelectAsync(
        string providerId,
        Guid providerAccountId,
        CancellationToken cancellationToken)
    {
        providerId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var recentSnapshots = await (
            from snapshot in db.ExternalMetadataSnapshots.AsNoTracking()
            join identity in db.ProviderTrackIdentities.AsNoTracking()
                on snapshot.ProviderTrackIdentityId equals identity.Id
            where snapshot.ProviderAccountId == providerAccountId &&
                  snapshot.ProviderId == providerId &&
                  identity.ProviderId == providerId &&
                  identity.ResourceKind == ProviderResourceKind.Track
            orderby snapshot.RetrievedAt descending, identity.Id
            select new { identity.Id, identity.ExternalId, snapshot.PayloadJson, snapshot.RetrievedAt })
            .Take(CorpusLimit * 4)
            .ToArrayAsync(cancellationToken);
        var corpus = recentSnapshots
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .Take(CorpusLimit)
            .ToArray();
        if (corpus.Length == 0) return null;

        var key = $"{providerId}:{providerAccountId:N}";
        int index;
        lock (_rotationLock)
        {
            index = _nextIndexes.TryGetValue<int>(key, out var current)
                ? (current + 1) % corpus.Length
                : Random.Shared.Next(corpus.Length);
            _nextIndexes.Set(
                key,
                index,
                new MemoryCacheEntryOptions
                {
                    Size = 1,
                    SlidingExpiration = RotationLifetime
                });
        }
        var selected = corpus[index % corpus.Length];
        return new ProviderCtsTrackSelection(selected.ExternalId, TrackLabel(selected.PayloadJson) ?? "Known provider track", corpus.Length);
    }

    public void Dispose() => _nextIndexes.Dispose();

    private static string? TrackLabel(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var title = Property(document.RootElement, "title");
            var artist = Property(document.RootElement, "artist");
            if (string.IsNullOrWhiteSpace(title)) return null;
            return string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }
        return null;
    }
}
