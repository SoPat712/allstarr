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
        string? snapshotVersion = null)
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

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        Id = id;
        Title = ProviderContractValidation.RequiredText(title, nameof(title), 500);
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
    }

    public ProviderExternalResourceId Id { get; }

    public string Title { get; }

    public IReadOnlyList<ProviderArtistCredit> Artists { get; }

    public ProviderExternalResourceId? AlbumId { get; }

    public string? AlbumTitle { get; }

    public TimeSpan? Duration { get; }

    public string? Isrc { get; }

    public bool? IsExplicit { get; }

    public ProviderArtworkReference? Artwork { get; }

    public string? SnapshotVersion { get; }
}

public sealed record ProviderAlbumMetadata
{
    public ProviderAlbumMetadata(
        ProviderExternalResourceId id,
        string title,
        IEnumerable<ProviderArtistCredit> artists,
        int? trackCount = null,
        ProviderArtworkReference? artwork = null,
        string? snapshotVersion = null)
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
    }

    public ProviderExternalResourceId Id { get; }

    public string Title { get; }

    public IReadOnlyList<ProviderArtistCredit> Artists { get; }

    public int? TrackCount { get; }

    public ProviderArtworkReference? Artwork { get; }

    public string? SnapshotVersion { get; }
}

public sealed record ProviderArtistMetadata
{
    public ProviderArtistMetadata(
        ProviderExternalResourceId id,
        string name,
        ProviderArtworkReference? artwork = null,
        string? snapshotVersion = null)
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
    }

    public ProviderExternalResourceId Id { get; }

    public string Name { get; }

    public ProviderArtworkReference? Artwork { get; }

    public string? SnapshotVersion { get; }
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

public interface IProviderMetadataCapability : IProviderCapability
{
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
}
