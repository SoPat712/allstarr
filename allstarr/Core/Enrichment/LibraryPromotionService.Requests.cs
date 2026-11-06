using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record PromotionStartResult(PromotionRunRecord? Run, PromotionRequestRecord? Request);

public sealed partial class LibraryPromotionService
{
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private static readonly string[] RequestStates = ["pending", "approved", "declined", "cancelled", "added", "failed"];

    public async Task<bool> CanAddDirectlyAsync(ProtocolExecutionContext context, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await PromotionProfiles.RequireUserAsync(db, context, token);
        var household = (await (profiles ?? new PromotionProfiles(factory)).GetMineAsync(context, token)).HouseholdDefault;
        return user.IsAdmin || user.IsTrustedListener && household.AddWithoutApproval == "admins-and-trusted-listeners";
    }

    public async Task<PromotionRunRecord> StartAsync(ProtocolExecutionContext context, PromoteSongRequest request,
        CancellationToken token = default)
    {
        if (!await CanAddDirectlyAsync(context, token)) throw new UnauthorizedAccessException("This listener must request approval.");
        var result = await StartOrRequestAsync(context, request, token);
        return result.Run ?? throw new InvalidOperationException("This album has an open request; follow its status.");
    }

    public async Task<PromotionStartResult> StartOrRequestAsync(ProtocolExecutionContext context, PromoteSongRequest request,
        CancellationToken token = default, bool requestApproval = false)
    {
        if (request.RecordingId == Guid.Empty || string.IsNullOrWhiteSpace(request.BackendLibraryId) ||
            string.IsNullOrWhiteSpace(request.RequestKey) || request.RequestKey.Length > 100)
            throw new ArgumentException("A recording, library and request key are required.");
        await requestGate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await PromotionProfiles.RequireUserAsync(db, context, token);
            var previous = await db.Set<PromotionRunRecord>().AsNoTracking().SingleOrDefaultAsync(item =>
                item.OwnerUserId == user.Id && item.RequestKey == request.RequestKey, token);
            if (previous != null)
            {
                if (previous.RequestJson != JsonSerializer.Serialize(request))
                    throw new InvalidOperationException("This request key already belongs to a different promotion.");
                return new(await GetAsync(context, previous.Id, token), null);
            }
            if (!(await libraryAccess.ResolveAsync(context, token)).Allows(request.BackendLibraryId)) throw new UnauthorizedAccessException();
            var profile = (await (profiles ?? new PromotionProfiles(factory)).GetMineAsync(context, token)).Values;
            var unit = Unit(request, profile);
            var songs = await RequestSongsAsync(db, context, request, unit, token);
            var identity = RequestIdentity(context, request, unit, songs);
            var direct = !requestApproval && await CanAddDirectlyAsync(context, token);
            await SynchronizeRequestsAsync(db, user.Id, token);
            var open = await db.PromotionRequests.Where(item => item.RequesterUserId == user.Id &&
                (item.State == "pending" || item.State == "approved")).OrderBy(item => item.CreatedAt).ToArrayAsync(token);
            var existing = open.FirstOrDefault(item => item.IdentityKey == identity || Covers(item, request, unit, songs));
            if (existing != null)
            {
                if (direct && existing.State == "approved" && existing.RunId is { } runId)
                {
                    var run = await db.Set<PromotionRunRecord>().AsNoTracking().SingleAsync(item => item.Id == runId, token);
                    return new(run.OwnerUserId == user.Id || user.IsAdmin ? await GetAsync(context, run.Id, token) : SharedRunReceipt(run), existing);
                }
                return new(null, existing);
            }
            var covering = await CoveringRunAsync(db, context, request, unit, songs, token);
            if (direct && (covering == null || covering.OwnerUserId == user.Id || user.IsAdmin))
                return new(covering ?? await StartRunAsync(context, request, token, songs), null);

            var first = songs[0];
            var pending = new PromotionRequestRecord
            {
                Id = Guid.CreateVersion7(),
                RequesterUserId = user.Id,
                RecordingId = request.RecordingId,
                IdentityKey = identity,
                ReleaseIdentity = ReleaseIdentity(first),
                Title = unit == "song" ? first.Title : first.Album,
                Artist = first.AlbumArtist ?? first.Artist,
                Year = first.Year,
                SongCount = songs.Count,
                CoverReference = first.CoverArtUrl,
                Unit = unit,
                CreatedAt = DateTimeOffset.UtcNow,
                RequestJson = JsonSerializer.Serialize(request with { Unit = unit }),
                SongsJson = JsonSerializer.Serialize(songs)
            };
            if (direct && covering != null)
            {
                pending.RunId = covering.Id;
                pending.State = "approved";
            }
            db.PromotionRequests.Add(pending);
            await db.SaveChangesAsync(token);
            if (direct && covering != null)
            {
                await SynchronizeRequestAsync(db, pending, token);
                await SaveSynchronizedRequestsAsync(db, token);
            }
            return new(direct && covering != null ? SharedRunReceipt(covering) : null, pending);
        }
        finally { requestGate.Release(); }
    }

    private static PromotionRunRecord SharedRunReceipt(PromotionRunRecord run) => new()
    {
        Id = run.Id,
        JobId = run.JobId,
        Step = run.Step,
        Outcome = run.Outcome,
        Reason = "This album is already being added to the library."
    };

    public async Task<IReadOnlyList<PromotionRequestRecord>> ListRequestsAsync(ProtocolExecutionContext context, string? state,
        CancellationToken token = default)
    {
        if (state != null && !RequestStates.Contains(state, StringComparer.Ordinal)) throw new ArgumentException("Choose a request state.");
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await PromotionProfiles.RequireUserAsync(db, context, token);
        await SynchronizeRequestsAsync(db, user.IsAdmin ? null : user.Id, token);
        return await db.PromotionRequests.AsNoTracking().Where(item => (user.IsAdmin || item.RequesterUserId == user.Id) &&
            (state == null || item.State == state)).OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).ToArrayAsync(token);
    }

    public async Task<PromotionRequestRecord> GetRequestAsync(ProtocolExecutionContext context, Guid id, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var request = await OwnedRequestAsync(db, context, id, token);
        await SynchronizeRequestAsync(db, request, token);
        await SaveSynchronizedRequestsAsync(db, token);
        return await db.PromotionRequests.AsNoTracking().SingleAsync(item => item.Id == id, token);
    }

    public async Task<PromotionRequestRecord> ApproveRequestAsync(ProtocolExecutionContext context, Guid id, CancellationToken token = default)
    {
        await requestGate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var administrator = await PromotionProfiles.RequireUserAsync(db, context, token, administrator: true);
            var pending = await OwnedRequestAsync(db, context, id, token);
            if (pending.State is "approved" or "added") return await GetRequestAsync(context, id, token);
            if (pending.State != "pending") throw new InvalidOperationException("Only pending requests can be approved.");
            var access = await libraryAccess.ResolveUserAsync(pending.RequesterUserId, token);
            var ownerContext = access.Context ?? throw new UnauthorizedAccessException("The requester's library access is unavailable.");
            await PromotionProfiles.RequireUserAsync(db, ownerContext, token);
            var start = JsonSerializer.Deserialize<PromoteSongRequest>(pending.RequestJson)!;
            if (!access.Access.Allows(start.BackendLibraryId)) throw new UnauthorizedAccessException();
            var songs = JsonSerializer.Deserialize<List<Song>>(pending.SongsJson)!;
            var tracks = songs.Select(song => new PromotionTrack { RecordingId = song.CanonicalRecordingId!.Value, Song = song }).ToArray();
            var present = true;
            foreach (var track in tracks)
                present &= await AlreadyInLibraryAsync(db, ownerContext, start.BackendLibraryId, track, token);
            if (present)
            {
                DecideRequest(db, pending, "approved", administrator.Id, null);
                CompleteRequest(db, pending, "added");
                await db.SaveChangesAsync(token);
                return pending;
            }
            var covering = await CoveringRunAsync(db, ownerContext, start, pending.Unit, songs, token);
            if (covering != null)
            {
                pending.RunId = covering.Id;
                DecideRequest(db, pending, "approved", administrator.Id, null);
                await db.SaveChangesAsync(token);
                return await GetRequestAsync(context, id, token);
            }
            await StartRunAsync(ownerContext, start with { RequestKey = $"request:{pending.Id:N}" }, token, songs, pending.Id, administrator.Id);
            return await GetRequestAsync(context, id, token);
        }
        finally { requestGate.Release(); }
    }

    public Task<PromotionRequestRecord> DeclineRequestAsync(ProtocolExecutionContext context, Guid id, string? note,
        CancellationToken token = default) => DecidePendingRequestAsync(context, id, "declined", note, token);

    public Task<PromotionRequestRecord> CancelRequestAsync(ProtocolExecutionContext context, Guid id,
        CancellationToken token = default) => DecidePendingRequestAsync(context, id, "cancelled", null, token);

    private async Task<PromotionRequestRecord> DecidePendingRequestAsync(ProtocolExecutionContext context, Guid id, string state,
        string? note, CancellationToken token)
    {
        if (note?.Trim().Length > 500) throw new ArgumentException("Use a note of at most 500 characters.");
        await requestGate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await PromotionProfiles.RequireUserAsync(db, context, token, administrator: state == "declined");
            var request = await OwnedRequestAsync(db, context, id, token);
            if (request.State != "pending") throw new InvalidOperationException("Only pending requests can be declined or cancelled.");
            DecideRequest(db, request, state, user.Id, string.IsNullOrWhiteSpace(note) ? null : SafeOperationalText.Sanitize(note.Trim(), 500));
            await db.SaveChangesAsync(token);
            return request;
        }
        finally { requestGate.Release(); }
    }

    private static async Task<PromotionRequestRecord> OwnedRequestAsync(AllstarrDbContext db, ProtocolExecutionContext context,
        Guid id, CancellationToken token)
    {
        var user = await PromotionProfiles.RequireUserAsync(db, context, token);
        var request = await db.PromotionRequests.SingleOrDefaultAsync(item => item.Id == id, token) ?? throw new KeyNotFoundException();
        if (request.RequesterUserId != user.Id && !user.IsAdmin) throw new UnauthorizedAccessException();
        return request;
    }

    private async Task<IReadOnlyList<Song>> RequestSongsAsync(AllstarrDbContext db, ProtocolExecutionContext context,
        PromoteSongRequest request, string unit, CancellationToken token)
    {
        if (promotionCatalog != null)
        {
            var songs = await promotionCatalog.ResolveAsync(context, request.RecordingId, unit, token);
            if (songs.Count is 0 or > 256 || songs.Any(song => song.CanonicalRecordingId is null || song.CanonicalRecordingId == Guid.Empty))
                throw new InvalidOperationException("A bounded release tracklist with recording identities is required.");
            return songs.DistinctBy(song => song.CanonicalRecordingId).ToArray();
        }
        var recording = await db.CanonicalRecordings.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.RecordingId, token)
            ?? throw new KeyNotFoundException();
        return [new() { CanonicalRecordingId = recording.Id, Title = recording.Title, Album = recording.Title }];
    }

    private static string? ReleaseIdentity(Song song)
    {
        var mbid = song.ProviderIds.GetValueOrDefault("musicbrainz_releaseid") ?? song.ProviderIds.GetValueOrDefault("musicbrainz_albumid");
        return Guid.TryParse(mbid, out var parsed) ? $"release:{parsed:D}" :
            !string.IsNullOrWhiteSpace(song.AlbumId) && !string.IsNullOrWhiteSpace(song.ExternalProvider)
                ? $"{song.ExternalProvider}:{song.AlbumId}" : null;
    }

    private static string RequestIdentity(ProtocolExecutionContext context, PromoteSongRequest request, string unit, IReadOnlyList<Song> songs)
    {
        var identity = unit == "song" ? request.RecordingId.ToString("N") : ReleaseIdentity(songs[0]) ??
            string.Join(',', songs.Select(song => song.CanonicalRecordingId!.Value).Order().Select(id => id.ToString("N")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { context.BackendInstanceId, request.BackendLibraryId, unit, identity }))));
    }

    private static bool Covers(PromotionRequestRecord candidate, PromoteSongRequest request, string unit, IReadOnlyList<Song> songs)
    {
        var start = JsonSerializer.Deserialize<PromoteSongRequest>(candidate.RequestJson)!;
        if (start.BackendLibraryId != request.BackendLibraryId || candidate.Unit == "song" && unit == "album") return false;
        return CoversSongs(candidate.Unit, JsonSerializer.Deserialize<List<Song>>(candidate.SongsJson)!, unit, songs);
    }

    private static bool CoversSongs(string candidateUnit, IReadOnlyList<Song> candidate, string unit, IReadOnlyList<Song> songs)
    {
        if (candidateUnit == "song" && unit == "album") return false;
        if (candidateUnit == "album" && ReleaseIdentity(songs[0]) is { } release && candidate.Count >= songs.Count &&
            candidate.Any(song => ReleaseIdentity(song) == release)) return true;
        return songs.All(song => candidate.Any(known => known.CanonicalRecordingId == song.CanonicalRecordingId ||
            !string.IsNullOrWhiteSpace(song.Isrc) && string.Equals(known.Isrc?.Replace("-", "", StringComparison.Ordinal),
                song.Isrc.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase) ||
            song.ProviderIds.GetValueOrDefault("musicbrainz_recordingid") is { } mbid &&
                known.ProviderIds.GetValueOrDefault("musicbrainz_recordingid") == mbid));
    }

    private static async Task<PromotionRunRecord?> CoveringRunAsync(AllstarrDbContext db, ProtocolExecutionContext context,
        PromoteSongRequest request, string unit, IReadOnlyList<Song> songs, CancellationToken token)
    {
        var active = await db.Set<PromotionRunRecord>().AsNoTracking().Where(item =>
            (item.Outcome == "Queued" || item.Outcome == "Running" || item.Outcome == "Needs review") &&
            db.Users.Any(user => user.Id == item.OwnerUserId && user.BackendInstanceId == context.BackendInstanceId))
            .OrderBy(item => item.CreatedAt).ToArrayAsync(token);
        foreach (var run in active)
        {
            var start = JsonSerializer.Deserialize<PromoteSongRequest>(run.RequestJson)!;
            if (start.BackendLibraryId != request.BackendLibraryId || start.Unit == "song" && unit == "album") continue;
            var album = ReadAlbum(run);
            var knownSongs = album?.Tracks.Select(track => track.Song).ToArray() ?? [new Song { CanonicalRecordingId = start.RecordingId }];
            if (!CoversSongs(start.Unit ?? album?.Profile.DefaultUnit ?? "song", knownSongs, unit, songs)) continue;
            var job = await db.Jobs.AsNoTracking().SingleAsync(item => item.Id == run.JobId, token);
            if (job.State is DurableJobState.Failed or DurableJobState.Cancelled || job.CancellationRequestedAt != null) continue;
            HydrateTracks(run);
            return run;
        }
        return null;
    }

    private static async Task LinkApprovedRequestAsync(AllstarrDbContext db, Guid id, PromotionRunRecord run, Guid approver, CancellationToken token)
    {
        var request = await db.PromotionRequests.SingleAsync(item => item.Id == id && item.RequesterUserId == run.OwnerUserId, token);
        if (request.State != "pending") throw new InvalidOperationException("The request is no longer pending.");
        request.RunId = run.Id;
        DecideRequest(db, request, "approved", approver, null);
    }

    private static void DecideRequest(AllstarrDbContext db, PromotionRequestRecord request, string state, Guid actor, string? note)
    {
        request.State = state;
        request.DecidedByUserId = actor;
        request.DecidedAt = DateTimeOffset.UtcNow;
        request.DeclineNote = note;
        request.Revision++;
        if (state is "approved" or "declined")
            RequestActivity(db, request, state, state == "approved" ? $"Your request for {request.Title} was approved." :
                $"Your request for {request.Title} was declined." + (note == null ? "" : $" {note}"));
    }

    private static void CompleteRequest(AllstarrDbContext db, PromotionRequestRecord request, string state)
    {
        request.State = state;
        request.Revision++;
        RequestActivity(db, request, state, state == "added" ? $"{request.Title} was added to your library." :
            $"{request.Title} could not be added to your library. Ask an administrator to review it.");
    }

    private static void RequestActivity(AllstarrDbContext db, PromotionRequestRecord request, string action, string message) => db.AuditEvents.Add(new()
    {
        Id = Guid.CreateVersion7(),
        ActorUserId = request.RequesterUserId,
        Category = "library-promotion",
        Action = $"request-{action}",
        Outcome = action == "failed" ? "failed" : "success",
        SubjectType = "promotion-request",
        SubjectId = request.Id.ToString("N"),
        DetailsJson = JsonSerializer.Serialize(new
        {
            message,
            requestId = request.Id,
            runId = request.RunId,
            decidedByUserId = request.DecidedByUserId,
            note = request.DeclineNote
        }),
        CreatedAt = DateTimeOffset.UtcNow
    });

    private async Task SynchronizeRequestsForRunAsync(Guid runId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var requests = await db.PromotionRequests.Where(item => item.RunId == runId && item.State == "approved").ToArrayAsync(token);
        foreach (var request in requests) await SynchronizeRequestAsync(db, request, token);
        await SaveSynchronizedRequestsAsync(db, token);
    }

    private static async Task SynchronizeRequestsAsync(AllstarrDbContext db, Guid? owner, CancellationToken token)
    {
        var requests = await db.PromotionRequests.Where(item => (owner == null || item.RequesterUserId == owner) && item.State == "approved").ToArrayAsync(token);
        foreach (var request in requests) await SynchronizeRequestAsync(db, request, token);
        await SaveSynchronizedRequestsAsync(db, token);
    }

    private static async Task SaveSynchronizedRequestsAsync(AllstarrDbContext db, CancellationToken token)
    {
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
    }

    private static async Task SynchronizeRequestAsync(AllstarrDbContext db, PromotionRequestRecord request, CancellationToken token)
    {
        if (request.State != "approved" || request.RunId is not { } runId) return;
        var run = await db.Set<PromotionRunRecord>().AsNoTracking().SingleAsync(item => item.Id == runId, token);
        var job = await db.Jobs.AsNoTracking().SingleAsync(item => item.Id == run.JobId, token);
        if (run.Outcome == "Placed")
        {
            var album = ReadAlbum(run);
            var added = album == null || album.Tracks.Any(track => track.Outcome == "Placed" || track.Steps.Any(step => step.Name == "Dedupe"));
            CompleteRequest(db, request, added ? "added" : "failed");
        }
        else if (run.Outcome is "Failed" or "Cancelled" || job.State is DurableJobState.Failed or DurableJobState.Cancelled)
            CompleteRequest(db, request, "failed");
    }
}
