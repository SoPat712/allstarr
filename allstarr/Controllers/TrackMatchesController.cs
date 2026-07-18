using System.Text.Json;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

/// <summary>
/// Provider-neutral review projection over durable Phase 4 match decisions.
/// Legacy Spotify mapping endpoints remain compatibility-only and are not consulted here.
/// </summary>
[ApiController]
[Route("api/admin/track-matches")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class TrackMatchesController(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    AdminAuthSessionService? sessionService = null) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? libraryScopeId = null,
        [FromQuery] string? state = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (page < 1 || pageSize is < 1 or > 200) return BadRequest(new { error = "Page and pageSize are outside the supported range" });
        if (!string.IsNullOrWhiteSpace(state) && !Enum.TryParse<TrackMatchState>(state, true, out _))
            return BadRequest(new { error = "State is not a valid match state" });

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = session!.TenantId!.Value;
        var userId = session.AllstarrUserId!.Value;
        var snapshotsQuery = db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.TenantId == tenantId);
        if (!session.IsAdministrator) snapshotsQuery = snapshotsQuery.Where(item => item.OwnerUserId == userId);
        if (!string.IsNullOrWhiteSpace(libraryScopeId)) snapshotsQuery = snapshotsQuery.Where(item => item.LibraryScopeId == libraryScopeId.Trim());
        var snapshots = await snapshotsQuery.OrderByDescending(item => item.RetrievedAt).Take(5000).ToListAsync(cancellationToken);
        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        var decisions = (await db.TrackMatches.AsNoTracking().Where(item => item.TenantId == tenantId && snapshotIds.Contains(item.ExternalSnapshotId))
                .OrderByDescending(item => item.DecisionVersion).ToListAsync(cancellationToken))
            .GroupBy(item => item.ExternalSnapshotId).ToDictionary(group => group.Key, group => group.First());
        var overrides = (await db.ManualTrackOverrides.AsNoTracking().Where(item => item.TenantId == tenantId && snapshotIds.Contains(item.ExternalSnapshotId) && item.RevokedAt == null)
                .ToListAsync(cancellationToken)).ToDictionary(item => item.ExternalSnapshotId);
        var libraryIds = decisions.Values.Where(item => item.LibraryTrackId.HasValue).Select(item => item.LibraryTrackId!.Value)
            .Concat(overrides.Values.Where(item => item.LibraryTrackId.HasValue).Select(item => item.LibraryTrackId!.Value)).Distinct().ToArray();
        var library = await db.LibraryTracks.AsNoTracking().Where(item => item.TenantId == tenantId && libraryIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var canonicalIds = decisions.Values.Where(item => item.CanonicalRecordingId.HasValue).Select(item => item.CanonicalRecordingId!.Value)
            .Concat(library.Values.Where(item => item.CanonicalRecordingId.HasValue).Select(item => item.CanonicalRecordingId!.Value)).Distinct().ToArray();
        var identities = (await db.ProviderTrackIdentities.AsNoTracking().Where(item => item.TenantId == tenantId && canonicalIds.Contains(item.CanonicalRecordingId))
                .OrderBy(item => item.ProviderId).ThenBy(item => item.ExternalId).ToListAsync(cancellationToken))
            .GroupBy(item => item.CanonicalRecordingId).ToDictionary(group => group.Key, group => group.ToArray());

        var rows = snapshots.Select(snapshot => Row(snapshot, decisions.GetValueOrDefault(snapshot.Id),
                overrides.GetValueOrDefault(snapshot.Id), library, identities))
            .Where(row => string.IsNullOrWhiteSpace(state) || row.State.ToString().Equals(state, StringComparison.OrdinalIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(search) || row.SearchText.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var total = rows.Length;
        var items = rows.Skip((page - 1) * pageSize).Take(pageSize).Select(row => row.Value).ToArray();
        return Ok(new
        {
            matches = items,
            stats = new
            {
                total,
                accepted = rows.Count(item => item.State is TrackMatchState.Accepted or TrackMatchState.Pinned),
                unresolved = rows.Count(item => item.State == TrackMatchState.Unresolved),
                review = rows.Count(item => item.State is TrackMatchState.Suggested or TrackMatchState.Ambiguous)
            },
            pagination = new { page, pageSize, total, totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)) }
        }
        );
    }

    private static MatchRow Row(ExternalMetadataSnapshotRecord snapshot, TrackMatchRecord? decision,
        ManualTrackOverrideRecord? manual, IReadOnlyDictionary<Guid, LibraryTrackRecord> library,
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord[]> identities)
    {
        var state = manual?.Decision == ManualOverrideDecision.Pin ? TrackMatchState.Pinned :
            manual?.Decision == ManualOverrideDecision.Reject ? TrackMatchState.Rejected : decision?.State ?? TrackMatchState.Unresolved;
        var trackId = manual?.LibraryTrackId ?? decision?.LibraryTrackId;
        library.TryGetValue(trackId ?? Guid.Empty, out var track);
        var canonicalId = decision?.CanonicalRecordingId ?? track?.CanonicalRecordingId;
        var providerIdentities = canonicalId.HasValue && identities.TryGetValue(canonicalId.Value, out var values)
            ? values.Select(item => new { item.ProviderId, item.ExternalId, scope = item.Scope.ToString(), verification = item.Verification.ToString() }).ToArray()
            : [];
        var metadata = Metadata(snapshot.PayloadJson);
        var value = new
        {
            externalSnapshotId = snapshot.Id,
            snapshot.ProviderId,
            snapshot.ProviderAccountId,
            snapshot.LibraryScopeId,
            state = state.ToString().ToLowerInvariant(),
            decision?.Confidence,
            decision?.Threshold,
            decision?.DecisionVersion,
            canonicalRecordingId = canonicalId,
            libraryTrackId = trackId,
            overrideId = manual?.Id,
            overrideRevision = manual?.Revision,
            title = metadata.Title,
            artist = metadata.Artist,
            album = metadata.Album,
            localTrack = track == null ? null : new { track.Id, track.BackendItemId, track.Title, track.Artist, track.Album },
            providerIdentities,
            candidates = ParseCandidates(decision?.CandidateResultsJson),
            reasons = ParseArray(decision?.ReasonsJson),
            warnings = ParseArray(decision?.WarningsJson),
            decidedAt = decision?.DecidedAt,
            reviewedAt = manual?.CreatedAt
        };
        return new(state, $"{metadata.Title} {metadata.Artist} {metadata.Album} {snapshot.ProviderId} {track?.Title} {track?.Artist}", value);
    }

    private static (string? Title, string? Artist, string? Album) Metadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var title = Text(root, "title") ?? Text(root, "Title") ?? Text(root, "name") ?? Text(root, "Name");
            var artist = Text(root, "artist") ?? Text(root, "Artist");
            if (artist == null && (root.TryGetProperty("artists", out var artists) || root.TryGetProperty("Artists", out artists)) && artists.ValueKind == JsonValueKind.Array)
                artist = string.Join(", ", artists.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : Text(item, "name")).Where(item => item != null));
            return (title, artist, Text(root, "album") ?? Text(root, "Album") ?? Text(root, "albumTitle") ?? Text(root, "AlbumTitle"));
        }
        catch (JsonException) { return (null, null, null); }
    }

    private static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string[] ParseArray(string? json)
    { try { return JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? []; } catch (JsonException) { return []; } }

    private static object[] ParseCandidates(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "[]");
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray().Select(item => new
            {
                libraryTrackId = Text(item, "libraryTrackId") ?? Text(item, "LibraryTrackId"),
                backendItemId = Text(item, "backendItemId") ?? Text(item, "BackendItemId"),
                confidence = Number(item, "confidence") ?? Number(item, "Confidence")
            }).Where(item => item.libraryTrackId != null).Cast<object>().ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static double? Number(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;

    private bool TrySession(out AdminAuthSession? session, out IActionResult? error)
    {
        session = null; error = null;
        HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value);
        var found = value as AdminAuthSession;
        if (found == null && sessionService?.TryGetValidSession(Request, out var cookieSession) == true)
        {
            found = cookieSession;
            HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = cookieSession;
        }
        if (found == null) { error = Unauthorized(new { error = "Authentication required" }); return false; }
        if (!found.TenantId.HasValue || !found.AllstarrUserId.HasValue)
        { error = StatusCode(403, new { error = "The backend identity is not linked to an Allstarr user" }); return false; }
        session = found; return true;
    }

    private sealed record MatchRow(TrackMatchState State, string SearchText, object Value);
}
