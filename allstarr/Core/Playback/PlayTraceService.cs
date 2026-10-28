using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public sealed class PlayTraceService(IDbContextFactory<AllstarrDbContext> factory, TimeProvider clock, ProblemReportService reports)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PlayTraceDetail>> ListAsync(AdminAuthSession session, int limit, CancellationToken token, Guid? before = null)
    {
        await reports.RequireSessionAsync(session, token);
        await using var db = await factory.CreateDbContextAsync(token);
        var query = Visible(db, session);
        if (before.HasValue)
        {
            var previous = await query.SingleOrDefaultAsync(item => item.Id == before.Value, token) ?? throw ProblemReportException.Missing();
            query = query.Where(item => item.CreatedAt < previous.CreatedAt || item.CreatedAt == previous.CreatedAt && item.Id.CompareTo(previous.Id) < 0);
        }
        var rows = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(token);
        return rows.Select(Read).OfType<PlayTraceDetail>().ToArray();
    }

    public async Task<PlayTraceDetail> DetailAsync(AdminAuthSession session, Guid id, CancellationToken token)
    {
        await reports.RequireSessionAsync(session, token);
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await Visible(db, session).SingleOrDefaultAsync(item => item.Id == id, token);
        return row == null ? throw ProblemReportException.Missing() : Read(row) ?? throw ProblemReportException.Missing();
    }

    public async Task<object> DiagnosticsAsync(AdminAuthSession session, Guid id, CancellationToken token)
    {
        var trace = await DetailAsync(session, id, token);
        return new
        {
            schemaVersion = 1,
            trace.Id,
            trace.StartedAt,
            trace.Outcome,
            request = new { trace.Request.Protocol, trace.Request.ItemId, presented = trace.Request.Presented with { Artwork = null } },
            resolution = new
            {
                trace.Resolution.CanonicalRecordingId,
                trace.Resolution.State,
                trace.Resolution.Score,
                trace.Resolution.AuthorityScope,
                trace.Resolution.Provider,
                trace.Resolution.Route,
                trace.Resolution.Cached,
                trace.Resolution.Quality
            },
            trace.Received,
            trace.Delivery,
            trace.Differences,
            trace.Timing
        };
    }

    private IQueryable<AuditEventRecord> Visible(AllstarrDbContext db, AdminAuthSession session)
    {
        var cutoff = clock.GetUtcNow() - PlaybackStartTimingStore.Retention;
        return db.AuditEvents.AsNoTracking().Where(item => item.Category == PlaybackStartTimingStore.Category &&
            item.SubjectType == PlaybackStartTimingStore.Category && item.SubjectId == "recent" && item.CreatedAt >= cutoff &&
            item.ActorUserId != null && (session.IsAdministrator || item.ActorUserId == session.AllstarrUserId));
    }

    private static PlayTraceDetail? Read(AuditEventRecord row)
    {
        if (row.DetailsJson.Length > 131072) return null;
        try
        {
            var sample = JsonSerializer.Deserialize<PlaybackStartSample>(row.DetailsJson);
            if (sample?.Trace is not { } trace || trace.OwnerUserId != row.ActorUserId) return null;
            return new(row.Id, row.CreatedAt, sample.Outcome, trace.Request, trace.Resolution, trace.Received, trace.Delivery,
                PlayTraceDifferences.Compare(trace.Request.Presented, trace.Received),
                new(sample.ElapsedMs, sample.HeadersMs, sample.FirstByteMs, sample.Phases, sample.OmittedPhases), trace.OwnerUserId);
        }
        catch (JsonException) { return null; }
    }

    internal async Task<string?> AccountLabelAsync(Guid accountId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return PlayTraceText.Clean(await db.ProviderAccounts.AsNoTracking().Where(item => item.Id == accountId)
            .Select(item => item.DisplayName).SingleOrDefaultAsync(token));
    }

    internal async Task<ManualTrackOverrideRecord?> AuthorityAsync(AdminAuthSession session, Guid snapshotId, string scope, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(item => item.Id == snapshotId, token)
            ?? throw ProblemReportException.Missing();
        var owner = scope == "personal" ? session.AllstarrUserId : null;
        return await ManualTrackOverrides.ForSource(db, snapshot).AsNoTracking().SingleOrDefaultAsync(item => item.OwnerUserId == owner && item.RevokedAt == null, token);
    }

    internal async Task CaptureAsync(PlaybackStartTimings timing, ProtocolExecutionContext context, string provider, string externalId, bool resolutionOnly = false)
    {
        if (context.Principal == null) return;
        provider = ProviderRuntimeStatusKey.CreateAccountFree(provider, "metadata").Provider;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        var token = timeout.Token;
        await using var db = await factory.CreateDbContextAsync(token);
        var user = context.Principal.UserId;
        var backend = context.Protocol.ToString().ToLowerInvariant();
        var snapshots = db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == user &&
            item.Protocol == backend && item.BackendInstanceId == context.BackendInstanceId &&
            item.BackendPrincipalId == context.VerifiedBackendPrincipalId && (item.ResourceKind == "track" || item.ResourceKind == "playback-metadata"));
        ExternalMetadataSnapshotRecord? snapshot;
        CanonicalRecordingRecord? recording = null;
        if (provider == "allstarr")
        {
            var publicId = context.RequestedItemId ?? $"ext-allstarr-song-{externalId}";
            recording = await db.CanonicalRecordings.AsNoTracking().SingleOrDefaultAsync(item => item.PublicId == publicId, token);
            snapshot = recording == null ? null : await snapshots.Where(item =>
                db.TrackMatches.Any(match => match.OwnerUserId == user && match.ExternalSnapshotId == item.Id && match.CanonicalRecordingId == recording.Id) ||
                db.ProviderTrackIdentities.Any(identity => identity.Id == item.ProviderTrackIdentityId && identity.CanonicalRecordingId == recording.Id))
                .OrderByDescending(item => item.ResourceKind == "track").ThenByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
        }
        else
        {
            var hash = CanonicalCatalogKeys.Hash(externalId);
            snapshot = await snapshots.Where(item => item.ProviderId == provider && item.ExternalIdHash == hash)
                .OrderByDescending(item => item.ResourceKind == "track").ThenByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
        }
        if (snapshot == null)
        {
            if (recording != null)
            {
                if (!resolutionOnly) timing.Present(new Song
                {
                    Title = recording.Title,
                    Duration = recording.DurationMilliseconds is > 0 ? (int)(recording.DurationMilliseconds / 1000) : null,
                    ExplicitContentLyrics = recording.IsExplicit == null ? null : recording.IsExplicit.Value ? 1 : 0
                });
                timing.Resolve(new(CanonicalRecordingId: recording.Id));
            }
            return;
        }
        var decision = await db.TrackMatches.AsNoTracking().Where(item => item.OwnerUserId == user && item.ExternalSnapshotId == snapshot.Id)
            .OrderByDescending(item => item.DecisionVersion).FirstOrDefaultAsync(token);
        var identityRecording = snapshot.ProviderTrackIdentityId.HasValue
            ? await db.ProviderTrackIdentities.AsNoTracking().Where(item => item.Id == snapshot.ProviderTrackIdentityId)
                .Select(item => (Guid?)item.CanonicalRecordingId).SingleOrDefaultAsync(token) : null;
        var layers = await ManualTrackOverrides.ReadAsync(db, snapshot, user, token);
        var authority = layers.Effective;
        if (!resolutionOnly) timing.Present(Metadata(snapshot.PayloadJson));
        timing.Resolve(new(recording?.Id ?? decision?.CanonicalRecordingId ?? identityRecording, snapshot.Id, decision?.Id,
            authority == null ? decision?.State.ToString().ToLowerInvariant() : authority.Decision == ManualOverrideDecision.Pin ? "pinned" : "rejected",
            decision != null && double.IsFinite(decision.Confidence) ? Math.Clamp(decision.Confidence, 0, 1) : null,
            layers.EffectiveScope, AuthorityId: authority?.Id, AuthorityRevision: authority?.Revision,
            AuthorityKind: authority == null ? null : authority.Decision == ManualOverrideDecision.Reject ? "rejection" :
                authority.TargetProviderId == null ? "local" : "provider"));
        timing.Artwork($"/api/admin/track-matches/{snapshot.Id:D}/artwork");
    }

    private static Song Metadata(string json)
    {
        if (json.Length > 131072) return new();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new();
            JsonElement? Value(params string[] names) => root.EnumerateObject().Where(property => names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                .Select(property => (JsonElement?)property.Value).FirstOrDefault();
            string? Text(params string[] names) => Value(names) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
            var milliseconds = Value("durationMilliseconds");
            var seconds = Value("duration", "durationSeconds");
            var explicitContent = Value("explicitContentLyrics");
            return new()
            {
                Title = Text("title", "name") ?? "",
                Artist = TrackSnapshotMetadata.Artist(root) ?? "",
                Album = Text("album", "albumTitle") ?? "",
                Duration = milliseconds is { ValueKind: JsonValueKind.Number } ms && ms.TryGetInt64(out var duration) && duration is > 0 and < 86400000 ? (int)(duration / 1000) :
                    seconds is { ValueKind: JsonValueKind.Number } sec && sec.TryGetInt32(out var length) && length is > 0 and < 86400 ? length : null,
                CoverArtUrl = Text("coverArtUrl", "artworkUrl"),
                ExplicitContentLyrics = TrackSnapshotMetadata.IsExplicit(root) switch
                {
                    true => 1,
                    false => 0,
                    _ => explicitContent is { ValueKind: JsonValueKind.Number } content && content.TryGetInt32(out var advice) ? advice : null
                }
            };
        }
        catch (JsonException) { return new(); }
    }
}
