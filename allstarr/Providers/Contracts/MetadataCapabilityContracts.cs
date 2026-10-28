namespace allstarr.Core.Capabilities;

public sealed record ProviderArtistCredit
{
    public ProviderArtistCredit(string name, ProviderExternalResourceId? artistId = null)
    {
        if (artistId != null && artistId.ResourceKind != ProviderResourceKind.Artist)
        {
            throw new ArgumentException("Artist credits require artist resource IDs.", nameof(artistId));
        }

        Name = ProviderContractValidation.RequiredText(name, nameof(name), 300);
        ArtistId = artistId;
    }

    public string Name { get; }

    public ProviderExternalResourceId? ArtistId { get; }
}

public sealed record ProviderTrackMetadata
{
    public ProviderTrackMetadata(
        ProviderExternalResourceId id,
        string title,
        IEnumerable<ProviderArtistCredit> artists,
        ProviderExternalResourceId? albumId = null,
        string? albumTitle = null,
        TimeSpan? duration = null,
        string? isrc = null,
        bool? isExplicit = null,
        ProviderArtworkReference? artwork = null,
        string? snapshotVersion = null,
        int? trackNumber = null,
        int? discNumber = null,
        int? totalTracks = null,
        int? year = null,
        string? genre = null,
        int? bpm = null,
        string? spotifyId = null,
        string? releaseDate = null,
        string? albumArtist = null,
        string? composer = null,
        string? label = null,
        string? copyright = null,
        IEnumerable<string>? contributors = null,
        int? explicitContentLyrics = null,
        int? bitrate = null,
        IEnumerable<ProviderArtistCredit>? albumArtists = null,
        IReadOnlyDictionary<string, string>? providerIds = null,
        bool isBestVersion = false,
        int? totalDiscs = null,
        string? upc = null,
        string? musicalKey = null,
        string? notes = null,
        ProviderMediaFormat? media = null,
        long? size = null,
        ProviderReplayGain? replayGain = null,
        IEnumerable<string>? genres = null,
        IEnumerable<string>? alternateTitles = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        id.RequireOwner(id.ProviderId, ProviderResourceKind.Track);
        if (albumId != null &&
            (albumId.ResourceKind != ProviderResourceKind.Album ||
             !albumId.ProviderId.Equals(id.ProviderId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Album IDs must belong to the track provider.", nameof(albumId));
        }

        var artistList = ProviderContractValidation.Copy(artists);
        if (artistList.Count == 0 || artistList.Any(item => item == null))
        {
            throw new ArgumentException("Track metadata requires at least one artist credit.", nameof(artists));
        }

        if (artistList.Any(item =>
                item.ArtistId != null &&
                !item.ArtistId.ProviderId.Equals(id.ProviderId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Artist IDs must belong to the track provider.", nameof(artists));
        }

        var albumArtistList = ProviderContractValidation.Copy(albumArtists);
        if (albumArtistList.Any(item => item == null ||
                item.ArtistId != null &&
                !item.ArtistId.ProviderId.Equals(id.ProviderId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Album artist IDs must belong to the track provider.", nameof(albumArtists));
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        if (bitrate is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitrate));
        }

        Id = id;
        Title = ProviderContractValidation.RequiredText(title, nameof(title), 500);
        IsBestVersion = isBestVersion;
        Artists = artistList;
        AlbumId = albumId;
        AlbumTitle = ProviderContractValidation.OptionalText(albumTitle, nameof(albumTitle), 500);
        Duration = duration > TimeSpan.Zero ? duration : null;
        Isrc = ProviderContractValidation.OptionalText(isrc, nameof(isrc), 20);
        IsExplicit = isExplicit;
        Artwork = artwork;
        SnapshotVersion = ProviderContractValidation.OptionalText(
            snapshotVersion,
            nameof(snapshotVersion),
            300);
        TrackNumber = trackNumber;
        DiscNumber = discNumber;
        TotalTracks = totalTracks;
        Year = year;
        Genre = ProviderContractValidation.OptionalText(genre, nameof(genre), 300);
        Bpm = bpm;
        SpotifyId = ProviderContractValidation.OptionalText(spotifyId, nameof(spotifyId), 300);
        ReleaseDate = ProviderContractValidation.OptionalText(releaseDate, nameof(releaseDate), 40);
        AlbumArtist = ProviderContractValidation.OptionalText(albumArtist, nameof(albumArtist), 500);
        Composer = ProviderContractValidation.OptionalText(composer, nameof(composer), 1000);
        Label = ProviderContractValidation.OptionalText(label, nameof(label), 500);
        Copyright = ProviderContractValidation.OptionalContent(copyright, nameof(copyright), 2000);
        Contributors = contributors == null
            ? []
            : ProviderContractValidation.Copy(contributors
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));
        ExplicitContentLyrics = explicitContentLyrics;
        Bitrate = bitrate;
        AlbumArtists = albumArtistList;
        ProviderIds = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            providerIds == null ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(providerIds, StringComparer.Ordinal));
        TotalDiscs = totalDiscs is > 0 ? totalDiscs : null;
        Upc = ProviderContractValidation.OptionalText(upc, nameof(upc), 100);
        MusicalKey = ProviderContractValidation.OptionalText(musicalKey, nameof(musicalKey), 100);
        Notes = ProviderContractValidation.OptionalContent(notes is { Length: > 4000 } ? notes[..4000] : notes, nameof(notes), 4000);
        Media = media;
        Size = size is > 0 ? size : null;
        ReplayGain = replayGain;
        Genres = ProviderContractValidation.Copy((genres ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));
        var aliases = (alternateTitles ?? []).Take(9).ToArray();
        if (aliases.Length > 8)
            throw new ArgumentException("Track metadata supports at most eight alternate titles.", nameof(alternateTitles));
        AlternateTitles = ProviderContractValidation.Copy(aliases
            .Select(value => ProviderContractValidation.RequiredText(value?.Trim()!, nameof(alternateTitles), 500))
            .Where(value => !string.Equals(value, Title, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public ProviderExternalResourceId Id { get; }

    public string Title { get; }

    public IReadOnlyList<string> AlternateTitles { get; }

    public bool IsBestVersion { get; }

    public IReadOnlyList<ProviderArtistCredit> Artists { get; }

    public ProviderExternalResourceId? AlbumId { get; }

    public string? AlbumTitle { get; }

    public TimeSpan? Duration { get; }

    public string? Isrc { get; }

    public bool? IsExplicit { get; }

    public ProviderArtworkReference? Artwork { get; }

    public string? SnapshotVersion { get; }

    public int? TrackNumber { get; }

    public int? DiscNumber { get; }

    public int? TotalTracks { get; }

    public int? Year { get; }

    public string? Genre { get; }

    public int? Bpm { get; }

    public string? SpotifyId { get; }

    public string? ReleaseDate { get; }

    public string? AlbumArtist { get; }

    public string? Composer { get; }

    public string? Label { get; }

    public string? Copyright { get; }

    public IReadOnlyList<string> Contributors { get; }

    public int? ExplicitContentLyrics { get; }

    public int? Bitrate { get; }

    public IReadOnlyList<ProviderArtistCredit> AlbumArtists { get; }

    public IReadOnlyDictionary<string, string> ProviderIds { get; }

    public int? TotalDiscs { get; }
    public string? Upc { get; }
    public string? MusicalKey { get; }
    public string? Notes { get; }
    public ProviderMediaFormat? Media { get; }
    public long? Size { get; }
    public ProviderReplayGain? ReplayGain { get; }
    public IReadOnlyList<string> Genres { get; }
}

public sealed record ProviderReplayGain
{
    public double? TrackGain { get; init; }
    public double? AlbumGain { get; init; }
    public double? TrackPeak { get; init; }
    public double? AlbumPeak { get; init; }
}

public sealed record ProviderAlbumMetadata
{
    public ProviderAlbumMetadata(
        ProviderExternalResourceId id,
        string title,
        IEnumerable<ProviderArtistCredit> artists,
        int? trackCount = null,
        ProviderArtworkReference? artwork = null,
        string? snapshotVersion = null,
        int? year = null,
        string? genre = null,
        IEnumerable<ProviderTrackMetadata>? tracks = null,
        string? releaseDate = null,
        string? label = null,
        string? notes = null,
        IReadOnlyDictionary<string, string>? providerIds = null,
        string? lastFmUrl = null,
        bool? isExplicit = null,
        string? upc = null,
        string? edition = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        id.RequireOwner(id.ProviderId, ProviderResourceKind.Album);
        if (trackCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackCount));
        }

        var artistList = ProviderContractValidation.Copy(artists);
        if (artistList.Count == 0 || artistList.Any(item => item == null))
        {
            throw new ArgumentException("Album metadata requires at least one artist credit.", nameof(artists));
        }

        if (artistList.Any(item =>
                item.ArtistId != null &&
                !item.ArtistId.ProviderId.Equals(id.ProviderId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Artist IDs must belong to the album provider.", nameof(artists));
        }

        Id = id;
        Title = ProviderContractValidation.RequiredText(title, nameof(title), 500);
        Artists = artistList;
        TrackCount = trackCount;
        Artwork = artwork;
        SnapshotVersion = ProviderContractValidation.OptionalText(
            snapshotVersion,
            nameof(snapshotVersion),
            300);
        ReleaseDate = ProviderContractValidation.OptionalText(releaseDate, nameof(releaseDate), 40);
        Year = year;
        Genre = ProviderContractValidation.OptionalText(genre, nameof(genre), 300);
        Label = ProviderContractValidation.OptionalText(label, nameof(label), 500);
        Notes = ProviderContractValidation.OptionalContent(notes, nameof(notes), int.MaxValue);
        ProviderIds = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            providerIds == null ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(providerIds, StringComparer.Ordinal));
        LastFmUrl = ProviderContractValidation.OptionalText(lastFmUrl, nameof(lastFmUrl), 4000);
        IsExplicit = isExplicit;
        Upc = ProviderContractValidation.OptionalText(upc, nameof(upc), 100);
        Edition = ProviderContractValidation.OptionalText(edition, nameof(edition), 500);
        Tracks = tracks == null ? [] : ProviderContractValidation.Copy(tracks);
        if (Tracks.Any(track => !track.Id.ProviderId.Equals(id.ProviderId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Album tracks must belong to the album provider.", nameof(tracks));
        }
    }

    public ProviderExternalResourceId Id { get; }

    public string Title { get; }

    public IReadOnlyList<ProviderArtistCredit> Artists { get; }

    public int? TrackCount { get; }

    public ProviderArtworkReference? Artwork { get; }

    public string? SnapshotVersion { get; }

    public string? ReleaseDate { get; }

    public int? Year { get; }

    public string? Genre { get; }

    public IReadOnlyList<ProviderTrackMetadata> Tracks { get; }

    public string? Label { get; }

    public string? Notes { get; }

    public IReadOnlyDictionary<string, string> ProviderIds { get; }

    public string? LastFmUrl { get; }

    public bool? IsExplicit { get; }

    public string? Upc { get; }

    public string? Edition { get; }
}

public sealed record ProviderArtistMetadata
{
    public ProviderArtistMetadata(
        ProviderExternalResourceId id,
        string name,
        ProviderArtworkReference? artwork = null,
        string? snapshotVersion = null,
        string? biography = null,
        IReadOnlyDictionary<string, string>? providerIds = null,
        string? lastFmUrl = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        id.RequireOwner(id.ProviderId, ProviderResourceKind.Artist);
        Id = id;
        Name = ProviderContractValidation.RequiredText(name, nameof(name), 500);
        Artwork = artwork;
        SnapshotVersion = ProviderContractValidation.OptionalText(
            snapshotVersion,
            nameof(snapshotVersion),
            300);
        Biography = ProviderContractValidation.OptionalContent(biography, nameof(biography), int.MaxValue);
        ProviderIds = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            providerIds == null ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(providerIds, StringComparer.Ordinal));
        LastFmUrl = ProviderContractValidation.OptionalText(lastFmUrl, nameof(lastFmUrl), 4000);
    }

    public ProviderExternalResourceId Id { get; }

    public string Name { get; }

    public ProviderArtworkReference? Artwork { get; }

    public string? SnapshotVersion { get; }

    public string? Biography { get; }

    public IReadOnlyDictionary<string, string> ProviderIds { get; }

    public string? LastFmUrl { get; }
}

public sealed record ProviderMetadataSearchRequest
{
    public ProviderMetadataSearchRequest(string query, ProviderPageRequest page, string? market = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        Query = ProviderContractValidation.RequiredText(query, nameof(query), 500);
        Page = page;
        Market = ProviderContractValidation.OptionalText(market, nameof(market), 20);
    }

    public string Query { get; }

    public ProviderPageRequest Page { get; }

    public string? Market { get; }
}

public sealed record ProviderTrackLookupRequest
{
    public ProviderTrackLookupRequest(
        ProviderExternalResourceId id,
        string? expectedSnapshotVersion = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        id.RequireOwner(id.ProviderId, ProviderResourceKind.Track);
        Id = id;
        ExpectedSnapshotVersion = ProviderContractValidation.OptionalText(
            expectedSnapshotVersion,
            nameof(expectedSnapshotVersion),
            300);
    }

    public ProviderExternalResourceId Id { get; }

    public string? ExpectedSnapshotVersion { get; }
}

public sealed record ProviderAlbumLookupRequest
{
    public ProviderAlbumLookupRequest(
        ProviderExternalResourceId id,
        string? expectedSnapshotVersion = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        id.RequireOwner(id.ProviderId, ProviderResourceKind.Album);
        Id = id;
        ExpectedSnapshotVersion = ProviderContractValidation.OptionalText(
            expectedSnapshotVersion,
            nameof(expectedSnapshotVersion),
            300);
    }

    public ProviderExternalResourceId Id { get; }

    public string? ExpectedSnapshotVersion { get; }
}

public sealed record ProviderArtistLookupRequest
{
    public ProviderArtistLookupRequest(
        ProviderExternalResourceId id,
        string? expectedSnapshotVersion = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        id.RequireOwner(id.ProviderId, ProviderResourceKind.Artist);
        Id = id;
        ExpectedSnapshotVersion = ProviderContractValidation.OptionalText(
            expectedSnapshotVersion,
            nameof(expectedSnapshotVersion),
            300);
    }

    public ProviderExternalResourceId Id { get; }

    public string? ExpectedSnapshotVersion { get; }
}

public sealed record ProviderArtistItemsRequest
{
    public ProviderArtistItemsRequest(
        ProviderExternalResourceId id,
        ProviderPageRequest page,
        string? expectedSnapshotVersion = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(page);
        id.RequireOwner(id.ProviderId, ProviderResourceKind.Artist);
        Id = id;
        Page = page;
        ExpectedSnapshotVersion = ProviderContractValidation.OptionalText(
            expectedSnapshotVersion,
            nameof(expectedSnapshotVersion),
            300);
    }

    public ProviderExternalResourceId Id { get; }

    public ProviderPageRequest Page { get; }

    public string? ExpectedSnapshotVersion { get; }
}

public sealed record ProviderIsrcLookupRequest
{
    public ProviderIsrcLookupRequest(string isrc, string? market = null)
    {
        Isrc = ProviderContractValidation.RequiredText(isrc, nameof(isrc), 20);
        Market = ProviderContractValidation.OptionalText(market, nameof(market), 20);
    }

    public string Isrc { get; }

    public string? Market { get; }
}

/// <summary>Songs a provider recommends after a seed track, best match first.</summary>
public sealed record ProviderSimilarTracksRequest
{
    public ProviderSimilarTracksRequest(ProviderExternalResourceId seed, int limit)
    {
        ArgumentNullException.ThrowIfNull(seed);
        seed.RequireOwner(seed.ProviderId, ProviderResourceKind.Track);
        if (limit is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(limit), "Similar-track limits must be between 1 and 200.");
        Seed = seed;
        Limit = limit;
    }

    public ProviderExternalResourceId Seed { get; }

    public int Limit { get; }
}

/// <summary>Artists a provider relates to a seed artist, best match first.</summary>
public sealed record ProviderSimilarArtistsRequest
{
    public ProviderSimilarArtistsRequest(ProviderExternalResourceId seed, int limit)
    {
        ArgumentNullException.ThrowIfNull(seed);
        seed.RequireOwner(seed.ProviderId, ProviderResourceKind.Artist);
        if (limit is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(limit), "Similar-artist limits must be between 1 and 200.");
        Seed = seed;
        Limit = limit;
    }

    public ProviderExternalResourceId Seed { get; }

    public int Limit { get; }
}

public interface IProviderMetadataCapability : IProviderCapability
{
    Task<ProviderOutcome<ProviderArtworkReference>> GetPlaylistArtworkAsync(
        ProviderExecutionContext context,
        ProviderExternalResourceId playlistId) => Task.FromResult(
            ProviderOutcome<ProviderArtworkReference>.Failure(new ProviderError(ProviderErrorKind.NotSupported)));

    Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request);

    Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
        ProviderExecutionContext context,
        ProviderTrackLookupRequest request);

    Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(
        ProviderExecutionContext context,
        ProviderIsrcLookupRequest request);

    Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request);

    Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(
        ProviderExecutionContext context,
        ProviderAlbumLookupRequest request);

    Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request);

    Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(
        ProviderExecutionContext context,
        ProviderArtistLookupRequest request);

    Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> GetArtistAlbumsAsync(
        ProviderExecutionContext context,
        ProviderArtistItemsRequest request) => Task.FromResult(
            ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Failure(
                new ProviderError(ProviderErrorKind.CapabilityUnavailable)));

    Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> GetArtistTracksAsync(
        ProviderExecutionContext context,
        ProviderArtistItemsRequest request) => Task.FromResult(
            ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(
                new ProviderError(ProviderErrorKind.CapabilityUnavailable)));

    Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> GetSimilarTracksAsync(
        ProviderExecutionContext context,
        ProviderSimilarTracksRequest request) => Task.FromResult(
            ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(
                new ProviderError(ProviderErrorKind.CapabilityUnavailable)));

    Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> GetSimilarArtistsAsync(
        ProviderExecutionContext context,
        ProviderSimilarArtistsRequest request) => Task.FromResult(
            ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Failure(
                new ProviderError(ProviderErrorKind.CapabilityUnavailable)));
}
