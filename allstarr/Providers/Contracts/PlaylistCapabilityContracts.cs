namespace allstarr.Core.Capabilities;

public sealed record ProviderPlaylistOwner
{
    public ProviderPlaylistOwner(string providerUserId, string? displayName = null)
    {
        ProviderUserId = ProviderContractValidation.RequiredText(
            providerUserId,
            nameof(providerUserId),
            500);
        DisplayName = ProviderContractValidation.OptionalText(displayName, nameof(displayName), 300);
    }

    public string ProviderUserId { get; }

    public string? DisplayName { get; }
}

public sealed record ProviderPlaylistSummary
{
    public ProviderPlaylistSummary(
        ProviderExternalResourceId id,
        string name,
        ProviderPlaylistOwner owner,
        string sourceRevision,
        string? description = null,
        ProviderArtworkReference? artwork = null,
        int? trackCount = null,
        string? sourceETag = null,
        int? durationSeconds = null,
        DateTime? createdDate = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(owner);
        id.RequireOwner(id.ProviderId, ProviderResourceKind.Playlist);
        if (trackCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackCount));
        }
        if (durationSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        Id = id;
        Name = ProviderContractValidation.RequiredText(name, nameof(name), 500);
        Owner = owner;
        SourceRevision = ProviderContractValidation.RequiredText(
            sourceRevision,
            nameof(sourceRevision),
            300);
        Description = ProviderContractValidation.OptionalContent(description, nameof(description), 4000);
        Artwork = artwork;
        TrackCount = trackCount;
        SourceETag = ProviderContractValidation.OptionalText(sourceETag, nameof(sourceETag), 500);
        DurationSeconds = durationSeconds;
        CreatedDate = createdDate;
    }

    public ProviderExternalResourceId Id { get; }

    public string Name { get; }

    public ProviderPlaylistOwner Owner { get; }

    public string SourceRevision { get; }

    public string? Description { get; }

    public ProviderArtworkReference? Artwork { get; }

    public int? TrackCount { get; }

    public string? SourceETag { get; }

    public int? DurationSeconds { get; }

    public DateTime? CreatedDate { get; }
}

public sealed record ProviderPlaylistTrack
{
    public ProviderPlaylistTrack(
        int position,
        ProviderExternalResourceId trackId,
        Guid? canonicalRecordingId = null,
        ProviderTrackMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(trackId);
        trackId.RequireOwner(trackId.ProviderId, ProviderResourceKind.Track);
        if (position < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (canonicalRecordingId == Guid.Empty)
        {
            throw new ArgumentException("Canonical recording IDs cannot be empty.", nameof(canonicalRecordingId));
        }

        if (metadata != null && metadata.Id != trackId)
        {
            throw new ArgumentException("Playlist track metadata must describe the same track ID.", nameof(metadata));
        }

        Position = position;
        TrackId = trackId;
        CanonicalRecordingId = canonicalRecordingId;
        Metadata = metadata;
    }

    public int Position { get; }

    public ProviderExternalResourceId TrackId { get; }

    public Guid? CanonicalRecordingId { get; }

    public ProviderTrackMetadata? Metadata { get; }
}

public sealed record ProviderPlaylistTrackPage
{
    public ProviderPlaylistTrackPage(
        ProviderPlaylistSummary playlist,
        ProviderPage<ProviderPlaylistTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(tracks);
        if (!playlist.Id.ProviderId.Equals(tracks.ProviderId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Playlist and track page provenance must match.", nameof(tracks));
        }

        if (tracks.Items.Any(item =>
                !item.TrackId.ProviderId.Equals(playlist.Id.ProviderId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Every playlist track must belong to the playlist provider.",
                nameof(tracks));
        }

        var positions = tracks.Items.Select(item => item.Position).ToArray();
        if (!positions.SequenceEqual(positions.OrderBy(item => item)) || positions.Distinct().Count() != positions.Length)
        {
            throw new ArgumentException("Playlist tracks must be uniquely ordered by position.", nameof(tracks));
        }

        Playlist = playlist;
        Tracks = tracks;
    }

    public ProviderPlaylistSummary Playlist { get; }

    public ProviderPage<ProviderPlaylistTrack> Tracks { get; }
}

public sealed record ProviderUserPlaylistsRequest
{
    public ProviderUserPlaylistsRequest(ProviderPageRequest page)
    {
        Page = page ?? throw new ArgumentNullException(nameof(page));
    }

    public ProviderPageRequest Page { get; }
}

public sealed record ProviderPlaylistTracksRequest
{
    public ProviderPlaylistTracksRequest(
        ProviderExternalResourceId playlistId,
        ProviderPageRequest page,
        string? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(playlistId);
        playlistId.RequireOwner(playlistId.ProviderId, ProviderResourceKind.Playlist);
        PlaylistId = playlistId;
        Page = page ?? throw new ArgumentNullException(nameof(page));
        ExpectedRevision = ProviderContractValidation.OptionalText(
            expectedRevision,
            nameof(expectedRevision),
            300);
    }

    public ProviderExternalResourceId PlaylistId { get; }

    public ProviderPageRequest Page { get; }

    public string? ExpectedRevision { get; }
}

public sealed record ProviderPlaylistSearchRequest
{
    public ProviderPlaylistSearchRequest(string query, ProviderPageRequest page)
    {
        Query = ProviderContractValidation.RequiredText(query, nameof(query), 500);
        Page = page ?? throw new ArgumentNullException(nameof(page));
    }

    public string Query { get; }

    public ProviderPageRequest Page { get; }
}

public sealed record ProviderPlaylistArtworkRequest
{
    public ProviderPlaylistArtworkRequest(ProviderArtworkReference artwork, int maximumBytes = 8 * 1024 * 1024)
    {
        Artwork = artwork ?? throw new ArgumentNullException(nameof(artwork));
        if (maximumBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        MaximumBytes = maximumBytes;
    }

    public ProviderArtworkReference Artwork { get; }
    public int MaximumBytes { get; }
}

public sealed record ProviderPlaylistArtwork
{
    public ProviderPlaylistArtwork(byte[] bytes, string contentType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) throw new ArgumentException("Artwork cannot be empty.", nameof(bytes));
        Bytes = bytes.ToArray();
        ContentType = contentType is "image/jpeg" or "image/png" or "image/webp"
            ? contentType
            : throw new ArgumentException("Artwork must use a supported image content type.", nameof(contentType));
    }

    public byte[] Bytes { get; }
    public string ContentType { get; }
}

public enum ProviderPlaylistConflictBehavior
{
    FailIfChanged,
    Reconcile,
    Recreate
}

public sealed record ProviderPlaylistMutationSupport(
    bool CanCreate,
    bool CanReplaceExisting)
{
    public static ProviderPlaylistMutationSupport None { get; } = new(false, false);
}

/// <summary>
/// A provider-neutral mutation intent reserved for host-controlled playlist materialization.
/// It is not an SDK v1 extension hook.
/// </summary>
public sealed record ProviderPlaylistMutationRequest
{
    public ProviderPlaylistMutationRequest(
        string providerId,
        string name,
        IEnumerable<ProviderExternalResourceId> orderedTrackIds,
        ProviderPlaylistConflictBehavior conflictBehavior,
        ProviderExternalResourceId? existingPlaylistId = null,
        string? expectedRevision = null,
        string? description = null,
        ProviderArtworkReference? artwork = null)
    {
        if (!Enum.IsDefined(conflictBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictBehavior));
        }

        providerId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));

        if (existingPlaylistId != null &&
            (existingPlaylistId.ResourceKind != ProviderResourceKind.Playlist ||
             !existingPlaylistId.ProviderId.Equals(providerId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Existing playlist IDs must identify playlists.", nameof(existingPlaylistId));
        }

        var tracks = ProviderContractValidation.Copy(orderedTrackIds);
        if (tracks.Any(item => item == null || item.ResourceKind != ProviderResourceKind.Track))
        {
            throw new ArgumentException("Playlist mutations require track resource IDs.", nameof(orderedTrackIds));
        }

        if (tracks.Any(item => !item.ProviderId.Equals(providerId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Playlist mutations cannot send IDs to a different provider.",
                nameof(orderedTrackIds));
        }

        ProviderId = providerId;
        Name = ProviderContractValidation.RequiredText(name, nameof(name), 500);
        OrderedTrackIds = tracks;
        ConflictBehavior = conflictBehavior;
        ExistingPlaylistId = existingPlaylistId;
        ExpectedRevision = ProviderContractValidation.OptionalText(
            expectedRevision,
            nameof(expectedRevision),
            300);
        Description = ProviderContractValidation.OptionalContent(description, nameof(description), 4000);
        Artwork = artwork;
    }

    public string ProviderId { get; }

    public string Name { get; }

    public IReadOnlyList<ProviderExternalResourceId> OrderedTrackIds { get; }

    public ProviderPlaylistConflictBehavior ConflictBehavior { get; }

    public ProviderExternalResourceId? ExistingPlaylistId { get; }

    public string? ExpectedRevision { get; }

    public string? Description { get; }

    public ProviderArtworkReference? Artwork { get; }
}

public sealed record ProviderPlaylistMutationReceipt
{
    public ProviderPlaylistMutationReceipt(
        ProviderExternalResourceId playlistId,
        string? revision,
        int trackCount,
        bool applied,
        IEnumerable<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(playlistId);
        playlistId.RequireOwner(playlistId.ProviderId, ProviderResourceKind.Playlist);
        if (trackCount < 0) throw new ArgumentOutOfRangeException(nameof(trackCount));
        var safeWarnings = (warnings ?? [])
            .Select(item => ProviderContractValidation.RequiredText(item, nameof(warnings), 200))
            .ToArray();
        if (safeWarnings.Length > 20)
            throw new ArgumentException("Playlist mutation receipts support at most 20 warnings.", nameof(warnings));

        PlaylistId = playlistId;
        Revision = ProviderContractValidation.OptionalText(revision, nameof(revision), 300);
        TrackCount = trackCount;
        Applied = applied;
        Warnings = Array.AsReadOnly(safeWarnings);
    }

    public ProviderExternalResourceId PlaylistId { get; }
    public string? Revision { get; }
    public int TrackCount { get; }
    public bool Applied { get; }
    public IReadOnlyList<string> Warnings { get; }
}

public interface IProviderPlaylistCapability : IProviderCapability
{
    ProviderPlaylistMutationSupport MutationSupport => ProviderPlaylistMutationSupport.None;

    Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(
        ProviderExecutionContext context,
        ProviderUserPlaylistsRequest request);

    Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(
        ProviderExecutionContext context,
        ProviderPlaylistTracksRequest request);

    Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(
        ProviderExecutionContext context,
        ProviderPlaylistSearchRequest request);

    Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
        ProviderExecutionContext context,
        ProviderPlaylistArtworkRequest request) => Task.FromResult(
            ProviderOutcome<ProviderPlaylistArtwork>.Failure(
                new ProviderError(ProviderErrorKind.CapabilityUnavailable)));

    Task<ProviderOutcome<ProviderPlaylistMutationReceipt>> MutatePlaylistAsync(
        ProviderExecutionContext context,
        ProviderPlaylistMutationRequest request) => Task.FromResult(
            ProviderOutcome<ProviderPlaylistMutationReceipt>.Failure(
                new ProviderError(ProviderErrorKind.CapabilityUnavailable)));
}
