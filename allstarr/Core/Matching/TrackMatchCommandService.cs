using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record TrackMatchActor(Guid UserId, bool IsAdministrator);

public sealed record ManualAuthorityRevision(Guid Id, long Revision);

public sealed record ResolveTrackMatchCommand(
    string TargetType,
    Guid? LibraryTrackId = null,
    string? BackendItemId = null,
    string? ExternalProvider = null,
    string? ExternalId = null,
    string? Reason = null,
    string AuthorityScope = "personal",
    ManualAuthorityRevision? ExpectedAuthority = null);

public sealed record AutomatedSourceMatchResult(
    string ProviderId,
    string ExternalId,
    TrackMatchReviewState State,
    string? LocalBackendItemId,
    string? Title,
    string? Artist,
    string? Album,
    long? DurationMilliseconds,
    string? Isrc,
    double Confidence);

public sealed record DurableProviderRoute(string ProviderId, string ExternalId, bool IsManual = false);

public sealed record TrackMatchDetailData(
    IReadOnlyList<ProviderTrackIdentityRecord> ProviderIdentities,
    IReadOnlyList<LibraryTrackRecord> LocalTracks,
    IReadOnlyList<ExternalMetadataSnapshotRecord> Snapshots,
    IReadOnlyList<TrackMatchRecord> Decisions,
    IReadOnlyList<ManualTrackOverrideRecord> Overrides,
    IReadOnlyList<ProviderDownloadArtifactEntity> Artifacts);

public sealed record TrackMatchReviewData(
    IReadOnlyList<ExternalMetadataSnapshotRecord> Snapshots,
    IReadOnlyList<TrackMatchRecord> LatestDecisions,
    IReadOnlyList<ManualTrackOverrideRecord> ActiveOverrides,
    IReadOnlyList<LibraryTrackRecord> LibraryTracks,
    IReadOnlyList<ProviderTrackIdentityRecord> ProviderIdentities);

public sealed record TrackMatchActivityData(
    IReadOnlyList<TrackMatchRecord> Decisions,
    IReadOnlyList<ExternalMetadataSnapshotRecord> Snapshots,
    IReadOnlyList<ProviderTrackIdentityRecord> ProviderIdentities,
    IReadOnlyList<LibraryTrackRecord> LibraryTracks);

public sealed record TrackMatchResolutionData(
    IReadOnlyList<ExternalMetadataSnapshotRecord> Snapshots,
    IReadOnlyList<ProviderTrackIdentityRecord> ProviderIdentities,
    IReadOnlyList<ManualTrackOverrideRecord> ActiveOverrides,
    IReadOnlyList<TrackMatchRecord> LatestDecisions);

public sealed record SourceTrackSeed(
    string ProviderId,
    string ExternalId,
    string Title,
    string Artist,
    string? Album,
    long? DurationMilliseconds,
    string? Isrc,
    string? ArtworkReference,
    string ProviderRevision);

public enum TrackMatchCommandFailure
{
    None,
    Invalid,
    NotFound,
    Forbidden,
    Conflict
}

public sealed record TrackMatchCommandResult(
    bool Succeeded,
    TrackMatchCommandFailure Failure = TrackMatchCommandFailure.None,
    string? Error = null,
    Guid? ExternalSnapshotId = null)
{
    public static TrackMatchCommandResult Success(Guid snapshotId) => new(true, ExternalSnapshotId: snapshotId);
    public static TrackMatchCommandResult Fail(TrackMatchCommandFailure failure, string error) => new(false, failure, error);
}

public sealed record TrackRematchCommandResult(
    bool Succeeded,
    TrackMatchCommandFailure Failure = TrackMatchCommandFailure.None,
    string? Error = null,
    string? State = null,
    double Confidence = 0,
    int CandidateCount = 0,
    int DecisionVersion = 0);

public enum ManualTrackAuthorityKind
{
    LocalMatch,
    ProviderMatch,
    Rejection
}

public sealed record ManualTrackAuthorityCommandResult(
    bool Succeeded,
    TrackMatchCommandFailure Failure = TrackMatchCommandFailure.None,
    string? Error = null,
    Guid? ExternalSnapshotId = null,
    Guid? ReleasedProviderIdentityId = null);

public interface ITrackMatchRepository
{
    bool SupportsExternalMatching => false;

    Task<int> EnsureSourceSnapshotsAsync(
        IReadOnlyCollection<SourceTrackSeed> sourceTracks,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomatedSourceMatchResult>> MatchSourceTracksAsync(
        IReadOnlyCollection<SourceTrackSeed> sourceTracks,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<TrackMatchDetailData> GetDetailAsync(
        TrackMatchActor actor,
        string providerId,
        string externalId,
        string? backendItemId = null,
        CancellationToken cancellationToken = default);

    Task<TrackMatchReviewData> GetReviewDataAsync(
        TrackMatchActor actor,
        string? backendLibraryId = null,
        string? search = null,
        Guid? externalSnapshotId = null,
        int scanLimit = 5000,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryTrackRecord>> SearchLocalTracksAsync(
        TrackMatchActor actor,
        string query,
        string? backendLibraryId = null,
        int limit = 20,
        ExternalTrackMatchSnapshot? source = null,
        CancellationToken cancellationToken = default);

    Task<TrackMatchActivityData> GetActivityDataAsync(
        TrackMatchActor actor,
        DateTimeOffset? before = null,
        Guid? beforeId = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<TrackMatchResolutionData> GetResolutionDataAsync(
        TrackMatchActor actor,
        Guid ownerUserId,
        IReadOnlyCollection<Guid> externalSnapshotIds,
        CancellationToken cancellationToken = default);

    Task<ExternalMetadataSnapshotRecord> CaptureSnapshotAsync(
        ProtocolExecutionContext context,
        ExternalSnapshotInput input,
        CancellationToken cancellationToken = default);

    Task<TrackMatchRecord> RecordDecisionAsync(
        ProtocolExecutionContext context,
        MatchDecisionInput input,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrackMatchRecord>> RecordDecisionsAsync(
        ProtocolExecutionContext context,
        IReadOnlyCollection<MatchDecisionInput> inputs,
        CancellationToken cancellationToken = default);

    Task<ManualTrackOverrideRecord> SetOverrideAsync(
        ProtocolExecutionContext context,
        ManualOverrideInput input,
        CancellationToken cancellationToken = default);

    Task RevokeOverrideAsync(
        ProtocolExecutionContext context,
        Guid overrideId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<ManualTrackOverrideRecord?> GetActiveOverrideAsync(
        ProtocolExecutionContext context,
        Guid externalSnapshotId,
        CancellationToken cancellationToken = default);

    Task<ExternalMetadataSnapshotRecord?> FindSnapshotAsync(
        Guid externalSnapshotId,
        CancellationToken cancellationToken = default);

    Task<ManualTrackOverrideRecord?> FindOverrideAsync(
        Guid overrideId,
        CancellationToken cancellationToken = default);

    Task<TrackRematchCommandResult> RematchSnapshotAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<TrackRematchCommandResult> RematchSnapshotAsync(
        ProtocolExecutionContext context,
        Guid externalSnapshotId,
        string correlationId,
        string policyVersion,
        CancellationToken cancellationToken = default);

    Task<ManualTrackAuthorityCommandResult> ClearManualAuthorityAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        ManualTrackAuthorityKind kind,
        Guid authorityId,
        long expectedRevision,
        string correlationId,
        CancellationToken cancellationToken = default,
        string authorityScope = "personal");

    Task<TrackRematchCommandResult> RematchManualAuthorityAsync(
        ProtocolExecutionContext context,
        Guid externalSnapshotId,
        ManualTrackAuthorityKind kind,
        Guid authorityId,
        long expectedRevision,
        string correlationId,
        CancellationToken cancellationToken = default,
        string authorityScope = "personal");

    Task<TrackMatchCommandResult> ClearSpotifyAsync(
        TrackMatchActor actor,
        string spotifyId,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<TrackMatchCommandResult> ResolveSpotifyAsync(
        TrackMatchActor actor,
        string spotifyId,
        ResolveTrackMatchCommand command,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<TrackMatchCommandResult> ResolveSnapshotAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        ResolveTrackMatchCommand command,
        string correlationId,
        CancellationToken cancellationToken = default);
}

public sealed partial class TrackMatchCommandService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    TrackMatchDecisionEngine decisionEngine,
    ProviderAccountResolver accountResolver,
    IPlatformClock clock,
    IBackendLibraryAccessResolver libraryAccess,
    TrackMatchPlayableSearch? playableSearch = null,
    IEffectiveProviderPolicyResolver? effectivePolicies = null,
    IProtocolProviderGateway? providerGateway = null) : ITrackMatchRepository
{
    private const int ConcurrentWriteRetries = 3;
    private readonly ConcurrentDictionary<
        (Guid UserId, bool IsAdministrator, Guid SnapshotId),
        Lazy<Task<TrackRematchCommandResult>>> _rematches = [];

    public bool SupportsExternalMatching => playableSearch != null;

    private async Task<IQueryable<LibraryTrackRecord>> AccessibleTracksAsync(
        AllstarrDbContext db, TrackMatchActor actor, CancellationToken cancellationToken)
    {
        var access = await libraryAccess.ResolveUserAsync(actor.UserId, cancellationToken);
        return LibraryTrackAccess.Query(db, access);
    }

    private async Task<TrackMatchDecisionEngine> DecisionEngineAsync(
        CancellationToken cancellationToken) => effectivePolicies == null
        ? decisionEngine
        : decisionEngine.WithLocalPriorityWindow(
            (await effectivePolicies.ResolveAsync(cancellationToken)).LocalPreferenceWindow);
}
