using System.Text.Json.Serialization;

namespace allstarr.Core.Operations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProblemReportPlaybackSelector([property: JsonPropertyName("subjectUserId")] Guid SubjectUserId, [property: JsonPropertyName("deviceId")] string DeviceId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProblemReportSelector([property: JsonPropertyName("itemId")] string? ItemId = null, [property: JsonPropertyName("externalSnapshotId")] Guid? ExternalSnapshotId = null,
    [property: JsonPropertyName("playback")] ProblemReportPlaybackSelector? Playback = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateProblemReportRequest([property: JsonPropertyName("requestId")] Guid RequestId, [property: JsonPropertyName("kind")] string Kind, [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("selector")] ProblemReportSelector? Selector = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProblemReportStatusRequest([property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("expectedRevision")] long ExpectedRevision);

public sealed record ProblemReportReceipt([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt, [property: JsonPropertyName("replayed")] bool Replayed);
public sealed record ProblemReportSong([property: JsonPropertyName("itemId")] string? ItemId, [property: JsonPropertyName("externalSnapshotId")] Guid? ExternalSnapshotId, [property: JsonPropertyName("canonicalRecordingId")] Guid? CanonicalRecordingId,
    [property: JsonPropertyName("libraryTrackId")] Guid? LibraryTrackId, [property: JsonPropertyName("title")] string? Title, [property: JsonPropertyName("artist")] string? Artist, [property: JsonPropertyName("album")] string? Album, [property: JsonPropertyName("durationMilliseconds")] long? DurationMilliseconds,
    [property: JsonPropertyName("catalogProviderId")] string? CatalogProviderId);
public sealed record ProblemReportMatch([property: JsonPropertyName("decisionId")] Guid? DecisionId, [property: JsonPropertyName("state")] string? State, [property: JsonPropertyName("decisionVersion")] int? DecisionVersion,
    [property: JsonPropertyName("confidence")] double? Confidence, [property: JsonPropertyName("authorityScope")] string AuthorityScope, [property: JsonPropertyName("reasonCodes")] IReadOnlyList<string> ReasonCodes);
public sealed record ProblemReportPlayback([property: JsonPropertyName("providerId")] string? ProviderId, [property: JsonPropertyName("sourceConfirmed")] bool SourceConfirmed, [property: JsonPropertyName("providerAccountId")] Guid? ProviderAccountId,
    [property: JsonPropertyName("accountRevision")] long? AccountRevision, [property: JsonPropertyName("openedAt")] DateTimeOffset? OpenedAt, [property: JsonPropertyName("observedAt")] DateTimeOffset? ObservedAt, [property: JsonPropertyName("cached")] bool? Cached,
    [property: JsonPropertyName("seekRung")] string? SeekRung, [property: JsonPropertyName("selectionReason")] string? SelectionReason, [property: JsonPropertyName("positionTicks")] long? PositionTicks, [property: JsonPropertyName("clientName")] string? ClientName)
{
    [JsonPropertyName("rejectionReason")] public string? RejectionReason { get; init; }
}
public sealed record ProblemReportCheck([property: JsonPropertyName("providerId")] string ProviderId, [property: JsonPropertyName("capability")] string Capability, [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("checkedAt")] DateTimeOffset CheckedAt, [property: JsonPropertyName("reasonCode")] string? ReasonCode);
public sealed record ProblemReportContext([property: JsonPropertyName("schemaVersion")] int SchemaVersion, [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("subjectUserId")] Guid? SubjectUserId, [property: JsonPropertyName("subjectName")] string? SubjectName, [property: JsonPropertyName("song")] ProblemReportSong? Song, [property: JsonPropertyName("match")] ProblemReportMatch? Match,
    [property: JsonPropertyName("playback")] ProblemReportPlayback? Playback, [property: JsonPropertyName("checks")] IReadOnlyList<ProblemReportCheck> Checks)
{
    [JsonPropertyName("playbackRequestId")] public Guid? PlaybackRequestId { get; init; }
}
public sealed record ProblemReportSummary([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("kind")] string Kind, [property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("notePreview")] string NotePreview,
    [property: JsonPropertyName("reporterName")] string ReporterName, [property: JsonPropertyName("subjectName")] string? SubjectName, [property: JsonPropertyName("songTitle")] string? SongTitle, [property: JsonPropertyName("artist")] string? Artist, [property: JsonPropertyName("providerId")] string? ProviderId,
    [property: JsonPropertyName("clientName")] string? ClientName, [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt, [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt, [property: JsonPropertyName("revision")] long Revision);
public sealed record ProblemReportDetail([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("kind")] string Kind, [property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("notePreview")] string NotePreview,
    [property: JsonPropertyName("reporterName")] string ReporterName, [property: JsonPropertyName("subjectName")] string? SubjectName, [property: JsonPropertyName("songTitle")] string? SongTitle, [property: JsonPropertyName("artist")] string? Artist, [property: JsonPropertyName("providerId")] string? ProviderId,
    [property: JsonPropertyName("clientName")] string? ClientName, [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt, [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt, [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("note")] string Note, [property: JsonPropertyName("context")] ProblemReportContext Context);
public sealed record ProblemReportPage([property: JsonPropertyName("items")] IReadOnlyList<ProblemReportSummary> Items, [property: JsonPropertyName("nextCursor")] string? NextCursor);
public sealed record ListenerContext([property: JsonPropertyName("backend")] string Backend, [property: JsonPropertyName("username")] string Username, [property: JsonPropertyName("musicClientUrl")] string? MusicClientUrl, [property: JsonPropertyName("addressSource")] string AddressSource);

public sealed class ProblemReportException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    internal static ProblemReportException Invalid() => new(400, "report_invalid", "The problem report is invalid.");
    internal static ProblemReportException Missing() => new(404, "report_context_unavailable", "This report context is unavailable. Refresh the page and try again.");
    internal static ProblemReportException Unauthorized() => new(401, "authentication_required", "Sign in again to continue.");
    internal static ProblemReportException Changed() => new(409, "report_changed", "This report changed. Refresh it before saving.");
}
