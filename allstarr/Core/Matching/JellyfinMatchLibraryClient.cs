using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Services.Common;

namespace allstarr.Core.Matching;

public sealed class JellyfinMatchLibraryClient(
    IHttpClientFactory clients,
    IBackendLibraryPermissionSource permissions) : IMatchLibraryClient
{
    public const string HttpClientName = "MatchLibrary";
    private const string AudioFields = "Path,ProviderIds,DateModified,DateCreated,Album,AlbumArtist,Artists,RunTimeTicks,ImageTags";

    public Task<IReadOnlyList<LibraryTrackIndexInput>> SearchAsync(ProtocolExecutionContext context, string libraryId, string query, CancellationToken cancellationToken) =>
        AudioAsync(context, libraryId, "SearchTerm=" + Uri.EscapeDataString(query), cancellationToken);

    public async Task<IReadOnlyList<LibraryTrackIndexInput>> ArtistSongsAsync(ProtocolExecutionContext context, string libraryId, string artist, CancellationToken cancellationToken)
    {
        var artists = await ReadAsync(context, libraryId, "Artists", "SearchTerm=" + Uri.EscapeDataString(artist) + "&Limit=5", cancellationToken);
        var ids = artists.Where(item => MatchLibrarySearch.SimilarTitle(artist, Text(item, "Name") ?? string.Empty))
            .Select(item => Text(item, "Id")).OfType<string>().Take(3).ToArray();
        return ids.Length == 0 ? [] : await AudioAsync(context, libraryId, "ArtistIds=" + Uri.EscapeDataString(string.Join(',', ids)), cancellationToken, paginate: true);
    }

    public async Task<IReadOnlyList<LibraryTrackIndexInput>> IsrcAsync(ProtocolExecutionContext context, string libraryId, string isrc, CancellationToken cancellationToken) =>
        (await AudioAsync(context, libraryId, "AnyProviderIdEquals=" + Uri.EscapeDataString("isrc." + isrc), cancellationToken))
        .Where(item => FuzzyMatcher.IdentifiersEqual(item.Isrc, isrc)).ToArray();

    private async Task<IReadOnlyList<LibraryTrackIndexInput>> AudioAsync(ProtocolExecutionContext context, string libraryId, string filter, CancellationToken cancellationToken, bool paginate = false)
    {
        var items = new List<JsonElement>();
        for (var offset = 0; offset < 2000; offset += 200)
        {
            var page = await ReadAsync(context, libraryId, "Items", $"IncludeItemTypes=Audio&Recursive=true&Limit=200&StartIndex={offset}&Fields={AudioFields}&{filter}", cancellationToken);
            items.AddRange(page);
            if (!paginate || page.Length < 200) break;
        }
        var result = new List<LibraryTrackIndexInput>();
        foreach (var item in items)
        {
            var id = Text(item, "Id");
            var title = Text(item, "Name");
            var path = Text(item, "Path");
            var artists = item.TryGetProperty("Artists", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).OfType<string>().ToArray() : [];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(path) || artists.Length == 0) continue;
            var providerIds = item.TryGetProperty("ProviderIds", out var providers) && providers.ValueKind == JsonValueKind.Object
                ? providers.EnumerateObject().Where(value => value.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(value => value.Name.ToLowerInvariant(), value => value.Value.GetString()!, StringComparer.Ordinal)
                : new Dictionary<string, string>();
            var duration = item.TryGetProperty("RunTimeTicks", out var ticks) && ticks.TryGetInt64(out var count) && count > 0
                ? (long?)(count / TimeSpan.TicksPerMillisecond) : null;
            var modified = DateTimeOffset.TryParse(Text(item, "DateModified") ?? Text(item, "DateCreated"), out var date) ? date : DateTimeOffset.UtcNow;
            var image = item.TryGetProperty("ImageTags", out var tags) ? Text(tags, "Primary") : null;
            result.Add(new(libraryId, id, path, title, string.Join(", ", artists), Text(item, "Album"), Text(item, "AlbumArtist"),
                duration, duration.HasValue ? "jellyfin" : null, duration.HasValue ? DateTimeOffset.UtcNow : null,
                providerIds.GetValueOrDefault("isrc"), providerIds.GetValueOrDefault("musicbrainztrack"),
                providerIds.GetValueOrDefault("musicbrainzalbum"), providerIds.GetValueOrDefault("musicbrainzartist"),
                providerIds, null, null, image == null ? null : $"jellyfin:{id}:{image}", modified));
        }
        return result;
    }

    private async Task<JsonElement[]> ReadAsync(ProtocolExecutionContext context, string libraryId, string endpoint, string filter, CancellationToken cancellationToken)
    {
        if (context.Protocol != ProtocolKind.Jellyfin || context.Principal == null) return [];
        using var authentication = await permissions.CreateRequestAsync(context, cancellationToken);
        if (authentication?.RequestUri == null) return [];
        var uri = new Uri(new Uri(authentication.RequestUri, "."),
            $"{endpoint}?UserId={Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}&ParentId={Uri.EscapeDataString(libraryId)}&{filter}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in authentication.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var client = clients.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => item.Clone()).ToArray() : [];
    }

    private static string? Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
