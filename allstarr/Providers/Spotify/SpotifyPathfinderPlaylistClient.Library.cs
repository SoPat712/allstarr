using System.Globalization;
using System.Text.Json;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.Spotify;

public sealed partial class SpotifyPathfinderPlaylistClient
{
    public async Task<ProviderOutcome<ProviderLibraryPage>> GetLibraryResourcesAsync(string token, string kind,
        ProviderPageRequest page, CancellationToken cancellationToken)
    {
        if (kind is not ("saved-albums" or "followed-artists") || !TryOffset(page.Cursor, out var offset) || offset >= 100000)
            return ProviderOutcome<ProviderLibraryPage>.Failure(new(ProviderErrorKind.PermanentFailure));
        var response = await QueryAsync(token, CurrentLibraryQuery, new
        {
            filters = new[] { kind == "saved-albums" ? "Albums" : "Artists" },
            order = (string?)null,
            textFilter = "",
            features = new[] { "LIKED_SONGS", "YOUR_EPISODES_V2", "PRERELEASES", "EVENTS" },
            offset,
            limit = page.Limit,
            flatten = true,
            expandedFolders = System.Array.Empty<string>(),
            folderUri = (string?)null,
            includeFoldersWhenFlattening = true,
            withCuration = true
        }, cancellationToken);
        if (!response.Outcome.IsSuccess) return ProviderOutcome<ProviderLibraryPage>.Failure(response.Outcome.Error!);
        try
        {
            if (response.Body!.Length > 4 * 1024 * 1024) throw new InvalidDataException();
            using var document = JsonDocument.Parse(response.Body);
            if (GraphQlFailure(document.RootElement, LibraryOperation) is { } failure)
                return ProviderOutcome<ProviderLibraryPage>.Failure(failure);
            if (!TryPath(document.RootElement, out var library, "data", "me", "libraryV3")) throw new InvalidDataException();
            var total = Integer(library, "totalCount");
            if (total is not (>= 0 and <= 100000) ||
                !TryPath(library, out var rows, "items") || rows.ValueKind != JsonValueKind.Array ||
                rows.GetArrayLength() > page.Limit || offset + rows.GetArrayLength() > total ||
                rows.GetArrayLength() == 0 && offset < total) throw new InvalidDataException();
            var items = new List<ProviderLibraryResource>();
            foreach (var row in rows.EnumerateArray())
            {
                if (!TryPath(row, out var data, "item", "data")) throw new InvalidDataException();
                var album = kind == "saved-albums";
                var id = SpotifyId(String(data, "uri"), album ? "album" : "artist");
                var title = album ? Text(data, "name") : TryPath(data, out var profile, "profile") ? Text(profile, "name") : Text(data, "name");
                if (id == null || !FavoriteText(title)) throw new InvalidDataException();
                _ = new ProviderExternalResourceId("spotify", album ? ProviderResourceKind.Album : ProviderResourceKind.Artist, id);
                var artists = new List<string>();
                var artistIds = new List<string>();
                if (TryPath(data, out var credits, "artists", "items"))
                    foreach (var credit in credits.EnumerateArray())
                    {
                        var value = TryPath(credit, out var nested, "data") ? nested : credit;
                        var name = TryPath(value, out var artist, "profile") ? Text(artist, "name") : Text(value, "name");
                        var artistId = SpotifyId(String(value, "uri"), "artist");
                        if (!FavoriteText(name) || !FavoriteText(artistId) || artists.Count >= 20) throw new InvalidDataException();
                        artists.Add(name!); artistIds.Add(artistId!);
                    }
                if (album && artists.Count == 0) throw new InvalidDataException();
                var date = TryPath(data, out var release, "date") ? String(release, "isoString") : String(data, "releaseDate");
                var count = TryPath(data, out var tracks, "tracks") ? Integer(tracks, "totalCount") : (int?)null;
                var upc = String(data, "upc");
                var edition = String(data, "edition");
                var mbid = String(data, "musicBrainzArtistId");
                if (date is { Length: > 40 } || date?.Any(char.IsControl) == true || count is < 0 or > 100000 ||
                    upc is { Length: > 100 } || edition != null && !FavoriteText(edition) || mbid is { Length: > 100 }) throw new InvalidDataException();
                bool? isExplicit = TryPath(data, out var rating, "contentRating") && String(rating, "label") is { } label
                    ? label == "EXPLICIT" ? true : label == "NONE" ? false : null : null;
                var albums = TryPath(data, out var known, "albums", "items") ? known.EnumerateArray()
                    .Take(20).Select(item => Text(item, "name")).Where(FavoriteText).Select(item => item!).ToArray() : [];
                items.Add(new(id, title!, artists.ToArray(), artistIds.ToArray(), date, count, ArtworkUri(data)?.ToString(),
                    upc, isExplicit, edition, albums, mbid));
            }
            var next = offset + items.Count;
            return ProviderOutcome<ProviderLibraryPage>.Success(new(items, total.Value, next < total ? next.ToString(CultureInfo.InvariantCulture) : null));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return ProviderOutcome<ProviderLibraryPage>.Failure(ProviderError.CompatibilityContractChanged());
        }
    }

    private static bool FavoriteText(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 500 && !text.Any(char.IsControl);
}
