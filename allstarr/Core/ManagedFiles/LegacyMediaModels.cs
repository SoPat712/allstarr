namespace allstarr.Core.ManagedFiles;

public sealed record LegacyMediaFileSnapshot(Guid RootId, string RootPath, string Path, long Length,
    string DeviceId, string FileId, long LastWriteUtcTicks, bool Kept,
    string? ProviderId, string? ExternalId, KeptRecordingMetadata Metadata);

public sealed record LegacyMediaPlan(IReadOnlyList<LegacyMediaFileSnapshot> Files, IReadOnlyList<string> Notices)
{
    public static LegacyMediaPlan Empty { get; } = new([], []);
}

public sealed record LegacyMediaPreview(int Files, int Identified, int NeedsIdentification, IReadOnlyList<string> Notices);

public sealed class LegacyMediaAdoptionRecord
{
    public Guid Id { get; set; }
    public Guid ImportId { get; set; }
    public Guid ManagedFileId { get; set; }
    public Guid? CanonicalRecordingId { get; set; }
    public string? ProviderId { get; set; }
    public string? ExternalId { get; set; }
    public bool Kept { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
