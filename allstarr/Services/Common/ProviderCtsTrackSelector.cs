using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

public sealed record ProviderCtsTrackSelection(string TrackId, string Label, int CorpusSize);

public sealed class ProviderCtsTrackSelector(IDbContextFactory<AllstarrDbContext> contextFactory)
{
    public const int CorpusLimit = 100;
    private readonly ConcurrentDictionary<string, int> _nextIndexes = new(StringComparer.Ordinal);

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
        var index = _nextIndexes.AddOrUpdate(
            key,
            _ => Random.Shared.Next(corpus.Length),
            (_, current) => (current + 1) % corpus.Length);
        var selected = corpus[index % corpus.Length];
        return new ProviderCtsTrackSelection(selected.ExternalId, TrackLabel(selected.PayloadJson) ?? "Known provider track", corpus.Length);
    }

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
