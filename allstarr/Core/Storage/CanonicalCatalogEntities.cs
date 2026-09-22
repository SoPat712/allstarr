namespace allstarr.Core.Storage;

public enum CanonicalCatalogEntityKind
{
    Artist,
    ReleaseGroup,
    Release,
    ReleaseTrack,
    Recording
}

public sealed class CanonicalArtistRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SortName { get; set; } = string.Empty;
    public string? Disambiguation { get; set; }
    public string? MusicBrainzArtistId { get; set; }
    public bool IsProvisional { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class CanonicalReleaseGroupRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? PrimaryType { get; set; }
    public string SecondaryTypesJson { get; set; } = "[]";
    public string? FirstReleaseDate { get; set; }
    public string? MusicBrainzReleaseGroupId { get; set; }
    public bool IsProvisional { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class CanonicalReleaseRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CanonicalReleaseGroupId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Disambiguation { get; set; }
    public string? Status { get; set; }
    public string? CountryCode { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Barcode { get; set; }
    public string? MusicBrainzReleaseId { get; set; }
    public bool IsProvisional { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class CanonicalReleaseTrackRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CanonicalReleaseId { get; set; }
    public Guid CanonicalRecordingId { get; set; }
    public int MediumPosition { get; set; }
    public int TrackPosition { get; set; }
    public string Title { get; set; } = string.Empty;
    public long? DurationMilliseconds { get; set; }
    public string? MusicBrainzTrackId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class CanonicalRecordingArtistRecord
{
    public Guid TenantId { get; set; }
    public Guid CanonicalRecordingId { get; set; }
    public Guid CanonicalArtistId { get; set; }
    public int Position { get; set; }
    public string? CreditName { get; set; }
    public string JoinPhrase { get; set; } = string.Empty;
}

public sealed class CanonicalReleaseGroupArtistRecord
{
    public Guid TenantId { get; set; }
    public Guid CanonicalReleaseGroupId { get; set; }
    public Guid CanonicalArtistId { get; set; }
    public int Position { get; set; }
    public string? CreditName { get; set; }
    public string JoinPhrase { get; set; } = string.Empty;
}

public sealed class CanonicalCatalogAliasRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public CanonicalCatalogEntityKind EntityKind { get; set; }
    public Guid CanonicalEntityId { get; set; }
    public string Namespace { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string ExternalIdHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class CatalogFactRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public CanonicalCatalogEntityKind EntityKind { get; set; }
    public Guid CanonicalEntityId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "null";
    public string SourceId { get; set; } = string.Empty;
    public string? SourceRevision { get; set; }
    public string PayloadSha256 { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset? RefreshAfter { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
}
