using allstarr.Models.Domain;

namespace allstarr.Core.Enrichment;

public sealed class PromotionTrack
{
    public Guid RecordingId { get; set; }
    public Song Song { get; set; } = new();
    public string Step { get; set; } = "Acquire";
    public string Outcome { get; set; } = "Queued";
    public string Reason { get; set; } = "Waiting for the best authorized source.";
    public int SourceAttempt { get; set; }
    public Guid? AcquisitionJobId { get; set; }
    public List<string> RejectedProviders { get; set; } = [];
    public Guid? ArtifactId { get; set; }
    public string? Provider { get; set; }
    public string? Extension { get; set; }
    public string? WorkingFileName { get; set; }
    public string? ContentSha256 { get; set; }
    public long Length { get; set; }
    public long? DurationMilliseconds { get; set; }
    public bool Lossy { get; set; }
    public allstarr.Core.Playback.AudioQualityCheck? QualityCheck { get; set; }
    public List<PromotionSourceAttempt> SourceAttempts { get; set; } = [];
    public Dictionary<string, string>? PendingTags { get; set; }
    public PromotionTrackStep? PendingMetadataStep { get; set; }
    public Guid? ManagedFileId { get; set; }
    public Guid? RefreshJobId { get; set; }
    public string? DestinationPath { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovalReason { get; set; }
    public List<PromotionTrackStep> Steps { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PromotionNextSource? NextSource { get; set; }
}

public sealed record PromotionTrackStep(string Name, string Outcome, string Reason);
public sealed record PromotionSourceAttempt(string? Provider, Guid? ArtifactId, string Outcome, string Reason);
internal sealed record PromotionAlbumPlan(PromotionProfile Profile, List<PromotionTrack> Tracks)
{
    public int Version { get; init; } = 2;
    public Guid? RefreshJobId { get; set; }
    public bool ReplayGainComplete { get; set; }
    public Dictionary<Guid, Dictionary<string, string>>? ReplayGainTags { get; set; }
}
