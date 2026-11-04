using System.Globalization;
using System.Text.Json;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.AppleMusicKit;

public sealed partial class AppleMusicKitPlaylistCapabilityAdapter
{
    public Task<ProviderOutcome<string>> GetSourceUserAsync(ProviderExecutionContext context) => ExecuteAsync(context,
        async (credential, storefront, ct) =>
        {
            var result = await SendAsync(credential, "v1/me/library/songs?limit=1&offset=0", ct);
            return result.Outcome.IsSuccess
                ? ProviderOutcome<string>.Success($"apple-library:{context.Account!.AccountId:N}")
                : ProviderOutcome<string>.Failure(result.Outcome.Error!);
        });

    private Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetLibrarySongsAsync(ProviderExecutionContext context,
        ProviderPlaylistTracksRequest request) => ExecuteAsync(context, async (credential, storefront, ct) =>
    {
        context.RequireResourceOwner(request.PlaylistId, ProviderResourceKind.Playlist);
        var result = await ReadLibraryPageAsync(credential, "v1/me/library/songs", request.Page, ct);
        if (!result.IsSuccess) return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(result.Error!);
        try
        {
            var page = result.RequireValue();
            var revision = $"library-songs:{page.Total}";
            if (request.ExpectedRevision != null && request.ExpectedRevision != revision)
                return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(ProviderError.CompatibilityContractChanged());
            var tracks = page.Items.Select((item, index) =>
            {
                var metadata = AppleMusicKitMetadataCapabilityAdapter.MapTrack(item, null);
                return new ProviderPlaylistTrack(page.Offset + index, metadata.Id, metadata: metadata);
            }).ToArray();
            var summary = new ProviderPlaylistSummary(request.PlaylistId, "Library songs", new("selected-user"), revision, trackCount: page.Total);
            return ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(summary,
                new(StableProviderId, tracks, page.NextCursor, page.NextCursor != null, revision)));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            return ProviderOutcome<ProviderPlaylistTrackPage>.Failure(ProviderError.CompatibilityContractChanged());
        }
    });

    public Task<ProviderOutcome<ProviderLibraryPage>> GetLibraryResourcesAsync(ProviderExecutionContext context,
        string kind, ProviderPageRequest page) => ExecuteAsync(context, async (credential, storefront, ct) =>
    {
        if (kind is not ("saved-albums" or "followed-artists"))
            return ProviderOutcome<ProviderLibraryPage>.Failure(new(ProviderErrorKind.NotSupported));
        var album = kind == "saved-albums";
        var path = album ? "v1/me/library/albums" : "v1/me/library/artists";
        var result = await ReadLibraryPageAsync(credential, path, page, ct);
        if (!result.IsSuccess) return ProviderOutcome<ProviderLibraryPage>.Failure(result.Error!);
        try
        {
            var value = result.RequireValue();
            var resources = value.Items.Select(item =>
            {
                var attributes = Object(item, "attributes");
                var artist = String(attributes, "artistName");
                if (album && string.IsNullOrWhiteSpace(artist)) throw new JsonException();
                var artwork = String(Object(attributes, "artwork"), "url")?.Replace("{w}", "512", StringComparison.Ordinal)
                    .Replace("{h}", "512", StringComparison.Ordinal);
                var albums = Data(Object(Object(item, "relationships"), "albums"))
                    .Select(related => String(Object(related, "attributes"), "name")).OfType<string>().Take(20).ToArray();
                var rating = String(attributes, "contentRating");
                return new ProviderLibraryResource(Required(item, "id"), Required(attributes, "name"),
                    album ? [artist!] : [], [], String(attributes, "releaseDate"), Integer(attributes, "trackCount"), artwork,
                    String(attributes, "upc"), rating == "explicit" ? true : rating == "clean" ? false : null, Albums: albums);
            }).ToArray();
            return ProviderOutcome<ProviderLibraryPage>.Success(new(resources, value.Total, value.NextCursor));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            return ProviderOutcome<ProviderLibraryPage>.Failure(ProviderError.CompatibilityContractChanged());
        }
    });

    private sealed record LibraryPage(JsonElement[] Items, int Offset, int Total, string? NextCursor);

    private async Task<ProviderOutcome<LibraryPage>> ReadLibraryPageAsync(AppleMusicCredential? credential, string path,
        ProviderPageRequest page, CancellationToken ct)
    {
        if (!TryLibraryCursor(page.Cursor, out var offset, out var knownTotal))
            return ProviderOutcome<LibraryPage>.Failure(new(ProviderErrorKind.PermanentFailure));
        var limit = Math.Clamp(page.Limit, 1, 25);
        var result = await SendAsync(credential, $"{path}?limit={limit}&offset={offset}", ct);
        if (!result.Outcome.IsSuccess) return ProviderOutcome<LibraryPage>.Failure(result.Outcome.Error!);
        try
        {
            using var document = JsonDocument.Parse(result.Body!);
            var root = document.RootElement;
            if (!ValidLibraryData(root)) throw new JsonException();
            var items = Data(root).Select(item => item.Clone()).ToArray();
            var total = await LibraryTotalAsync(credential, path, root, offset, ct, knownTotal);
            if (!total.IsSuccess) return ProviderOutcome<LibraryPage>.Failure(total.Error!);
            var count = total.RequireValue();
            if (items.Length > limit || offset + items.Length > count ||
                HasNext(root) && (items.Length == 0 || offset + items.Length >= count) ||
                !HasNext(root) && offset + items.Length != count) throw new JsonException();
            var next = HasNext(root) ? LibraryCursor(offset + items.Length, count, root) : null;
            return ProviderOutcome<LibraryPage>.Success(new(items, offset, count, next));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            return ProviderOutcome<LibraryPage>.Failure(ProviderError.CompatibilityContractChanged());
        }
    }

    private async Task<ProviderOutcome<int>> LibraryTotalAsync(AppleMusicCredential? credential, string path,
        JsonElement root, int offset, CancellationToken ct, int? knownTotal = null)
    {
        if (Integer(Object(root, "meta"), "total") is { } total)
            return total is >= 0 and <= 100000 ? ProviderOutcome<int>.Success(total)
                : ProviderOutcome<int>.Failure(ProviderError.CompatibilityContractChanged());
        if (knownTotal != null) return ProviderOutcome<int>.Success(knownTotal.Value);
        var count = offset + Data(root).Count();
        var hasNext = HasNext(root);
        for (var pages = 0; hasNext && pages < 4000 && count < 100000; pages++)
        {
            var result = await SendAsync(credential, $"{path}?limit=25&offset={count}", ct);
            if (!result.Outcome.IsSuccess) return ProviderOutcome<int>.Failure(result.Outcome.Error!);
            using var document = JsonDocument.Parse(result.Body!);
            if (!ValidLibraryData(document.RootElement)) return ProviderOutcome<int>.Failure(ProviderError.CompatibilityContractChanged());
            var length = Data(document.RootElement).Count();
            if (length is 0 or > 25) return ProviderOutcome<int>.Failure(ProviderError.CompatibilityContractChanged());
            count += length;
            hasNext = HasNext(document.RootElement);
        }
        return hasNext || count > 100000 ? ProviderOutcome<int>.Failure(ProviderError.CompatibilityContractChanged())
            : ProviderOutcome<int>.Success(count);
    }

    private static bool TryLibraryCursor(string? cursor, out int offset, out int? total)
    {
        total = null;
        var parts = cursor?.Split(':');
        if (!TryOffset(parts?[0], out offset) || offset > 100000 || parts?.Length > 2) return false;
        if (parts?.Length != 2) return true;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < offset || count > 100000) return false;
        total = count;
        return true;
    }

    private static string LibraryCursor(int offset, int total, JsonElement root) =>
        Integer(Object(root, "meta"), "total") == null ? $"{offset}:{total}" : offset.ToString(CultureInfo.InvariantCulture);

    private static bool ValidLibraryData(JsonElement root) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array;
    private static int? Integer(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
}
