namespace allstarr.Core.Enrichment;

public sealed record PromotionAlbumSummary(string Title, string Artist, int? Year, int SongCount, string? CoverReference);
public sealed record PromotionCopyPreview(string Provider, string? Codec, int? BitDepth, int? SampleRate, string Verdict, string Reason);
public sealed record PromotionProfileStep(string Key, string Title, string Description, bool Enabled);
public sealed record PromotionNextSource(string Provider, string? Codec, int? BitDepth, int? SampleRate);
public sealed record PromotionRequester(Guid Id, string DisplayName);
public sealed record PromotionListCounts(int Active, int NeedsLook, int Added, int All, int AddedToday);
public sealed record PromotionTrackCounts(int Total, int Added, int NeedsLook);
public sealed record PromotionListPage(IReadOnlyList<PromotionReceipt> Items, string? NextCursor, PromotionListCounts Counts);
public sealed record PromotionKeptItem(string Path, Guid RecordingId, string? BackendLibraryId, bool InLibrary);
public sealed record PromotionKeptPage(IReadOnlyList<PromotionKeptItem> Items, string? BackendLibraryId,
    IReadOnlyList<string> BackendLibraryIds, Guid? RootId, int AlreadyInLibrary)
{
    public PromotionSetup? Setup { get; init; }
}

public sealed record PromotionReceipt(Guid Id, Guid JobId, string Step, string Outcome, string Reason,
    Guid? ManagedFileId, Guid? ApprovedByUserId, string? ApprovalReason, IReadOnlyList<PromotionTrack> Tracks,
    string Title, string Artist, string? CoverReference, PromotionAlbumSummary Album, string Unit,
    int Total, int Added, int NeedsLook, string State, bool Active, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, PromotionRequester Requester, Guid RecordingId, string BackendLibraryId, Guid? RootId)
{
    public PromotionTrackCounts Counts => new(Total, Added, NeedsLook);
    public IReadOnlyList<PromotionProfileStep> ProfileSteps { get; init; } = [];
}

public sealed record PromotionRequestReceipt(Guid Id, Guid RequesterUserId, Guid RecordingId, string? ReleaseIdentity,
    string Title, string Artist, int? Year, int SongCount, string? CoverReference, string Unit, string State,
    Guid? DecidedByUserId, DateTimeOffset? DecidedAt, string? DeclineNote, Guid? RunId, DateTimeOffset CreatedAt,
    PromotionRequester Requester);
