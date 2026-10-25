using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services.Local;

namespace allstarr.Core.Favorites;

public sealed record FavoriteDisplaySnapshot(int Version, string ItemId, string Kind, string Title,
    string[] Artists, string? Album = null, long? DurationMilliseconds = null, int? Year = null, int? Count = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string[] ArtistIds { get; init; } = [];
    public string[] AlbumArtistIds { get; init; } = [];
    public string[] AlbumArtists { get; init; } = [];
    public string? AlbumId { get; init; }
    public string[] Genres { get; init; } = [];

    public static ProviderExternalResourceId? Resource(string itemId)
    {
        var parsed = LocalLibraryService.ParseExternalResource(itemId);
        if (!parsed.isExternal || parsed.provider == null || parsed.externalId == null) return null;
        var kind = parsed.type switch
        {
            "song" => ProviderResourceKind.Track,
            "album" => ProviderResourceKind.Album,
            "artist" => ProviderResourceKind.Artist,
            "playlist" => ProviderResourceKind.Playlist,
            _ => ProviderResourceKind.Unknown
        };
        if (kind == ProviderResourceKind.Unknown) return null;
        try { return new(parsed.provider, kind, parsed.externalId); }
        catch (ArgumentException) { return null; }
    }

    public static FavoriteDisplaySnapshot? Read(string itemId, string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 32768) return null;
        try
        {
            var value = JsonSerializer.Deserialize<FavoriteDisplaySnapshot>(json, JsonOptions);
            return value != null && value.ItemId == itemId && value.Valid() ? value : null;
        }
        catch (JsonException) { return null; }
    }

    public static FavoriteDisplaySnapshot Placeholder(string itemId, ProviderResourceKind kind) =>
        new(1, itemId, KindName(kind), kind switch
        {
            ProviderResourceKind.Track => "Saved track",
            ProviderResourceKind.Album => "Saved album",
            ProviderResourceKind.Artist => "Saved artist",
            _ => "Saved playlist"
        }, []);

    internal bool Valid() => Version == 1 && Text(Title) &&
        Artists is { Length: <= 20 } && Artists.All(Text) &&
        (Album == null || Text(Album)) && DurationMilliseconds is null or >= 0 and <= 86_400_000 &&
        Year is null or >= 0 and <= 9999 && Count is null or >= 0 &&
        ArtistIds is { Length: <= 20 } && ArtistIds.All(Text) &&
        AlbumArtistIds is { Length: <= 20 } && AlbumArtistIds.All(Text) &&
        AlbumArtists is { Length: <= 20 } && AlbumArtists.All(Text) &&
        (AlbumId == null || Text(AlbumId)) && Genres is { Length: <= 20 } && Genres.All(Text) &&
        Resource(ItemId) is { } resource && Kind == KindName(resource.ResourceKind);

    internal static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 500 &&
        !value.Any(char.IsControl);

    internal static string KindName(ProviderResourceKind kind) => kind switch
    {
        ProviderResourceKind.Track => "song",
        ProviderResourceKind.Album => "album",
        ProviderResourceKind.Artist => "artist",
        _ => "playlist"
    };
}

public sealed record FavoriteViewItem(string ItemId, ProviderExternalResourceId Resource,
    DateTimeOffset FavoritedAt, FavoriteDisplaySnapshot Display)
{
    internal bool SharedScope { get; init; }

    public Song Song() => new()
    {
        Id = ItemId,
        ExternalProvider = Resource.ProviderId,
        ExternalId = Resource.Value,
        Title = Display.Title,
        Artist = Display.Artists.FirstOrDefault() ?? "Unknown artist",
        Artists = Display.Artists.ToList(),
        ArtistId = Display.ArtistIds.FirstOrDefault(),
        ArtistIds = Display.ArtistIds.ToList(),
        Album = Display.Album ?? string.Empty,
        AlbumId = Display.AlbumId,
        AlbumArtist = Display.AlbumArtists.FirstOrDefault(),
        AlbumArtists = Display.AlbumArtistIds.Select((id, index) => new Artist
        {
            Id = id,
            Name = Display.AlbumArtists.ElementAtOrDefault(index) ??
                (Array.IndexOf(Display.ArtistIds, id) is var artistIndex && artistIndex >= 0 ? Display.Artists.ElementAtOrDefault(artistIndex) : null) ?? "Saved artist"
        }).ToList(),
        Genre = Display.Genres.FirstOrDefault(),
        Year = Display.Year,
        Duration = Display.DurationMilliseconds is { } milliseconds ? (int)(milliseconds / 1000) : null,
        PreciseDuration = Display.DurationMilliseconds is { } precise ? TimeSpan.FromMilliseconds(precise) : null
    };

    public Album Album() => new()
    {
        Id = ItemId,
        ExternalProvider = Resource.ProviderId,
        ExternalId = Resource.Value,
        Title = Display.Title,
        Artist = Display.AlbumArtists.FirstOrDefault() ?? Display.Artists.FirstOrDefault() ?? "Unknown artist",
        ArtistId = Display.AlbumArtistIds.FirstOrDefault() ?? Display.ArtistIds.FirstOrDefault(),
        Genre = Display.Genres.FirstOrDefault(),
        Year = Display.Year,
        SongCount = Display.Count
    };

    public Artist Artist() => new()
    {
        Id = ItemId,
        ExternalProvider = Resource.ProviderId,
        ExternalId = Resource.Value,
        Name = Display.Title,
        AlbumCount = Display.Count
    };

    public ExternalPlaylist Playlist() => new()
    {
        Id = ItemId,
        Provider = Resource.ProviderId,
        ExternalId = Resource.Value,
        Name = Display.Title,
        CuratorName = Display.Artists.FirstOrDefault(),
        TrackCount = Display.Count ?? 0,
        Duration = (int)((Display.DurationMilliseconds ?? 0) / 1000)
    };
}
