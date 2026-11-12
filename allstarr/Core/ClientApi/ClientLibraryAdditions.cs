using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Enrichment;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

public sealed record AddClientLibraryItem(string ItemId, string Unit);
public sealed record ClientLibraryAddition(Guid Id, string ItemId, string Unit, string State, string Reason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LibraryItemId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedItemId { get; init; }
}
public sealed record ClientLibraryAdditionCapabilities(bool CanAddDirectly, bool RequiresApproval, string DefaultUnit);

public sealed class ClientLibraryAdditions(IDbContextFactory<AllstarrDbContext> factory, LibraryPromotionService promotions,
    PromotionProfiles profiles, PromotionRoots roots, ITrackIdentityService identities, IBackendLibraryAccessResolver access,
    IProtocolProviderGateway providers)
{
    private static readonly SemaphoreSlim additionGate = new(1, 1);

    public async Task<(ClientLibraryAddition Addition, bool Replayed)> AddIdempotentAsync(ProtocolExecutionContext context,
        AddClientLibraryItem request, string? key, CancellationToken token = default)
    {
        if (key == null) return (await AddAsync(context, request, token), false);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 300) throw new ArgumentException("Choose a valid request key.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        await additionGate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var owner = (await PromotionProfiles.RequireUserAsync(db, context, token)).Id;
            var receipts = await db.AuditEvents.AsNoTracking().Where(item => item.ActorUserId == owner &&
                item.Action == "library-addition.accepted" && item.CorrelationId == hash).ToArrayAsync(token);
            var previous = receipts.Where(item => item.CreatedAt > DateTimeOffset.UtcNow.AddHours(-24))
                .OrderByDescending(item => item.CreatedAt).FirstOrDefault();
            if (previous != null)
            {
                if (JsonSerializer.Deserialize<AddClientLibraryItem>(previous.DetailsJson) != request)
                    throw new PromotionException(PromotionFailure.PromotionChanged);
                var id = Guid.Parse(previous.SubjectId!);
                var pending = await db.PromotionRequests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.RequesterUserId == owner, token);
                if (pending != null) pending = await promotions.GetRequestAsync(context, id, token);
                var run = pending == null ? await promotions.GetAsync(context, id, token) : pending.RunId is { } runId
                    ? await db.Set<PromotionRunRecord>().AsNoTracking().SingleAsync(item => item.Id == runId, token) : null;
                var value = pending == null ? From(run!) : From(pending, run);
                return ((await ResolveLibraryItemsAsync(context, [(value, ReadLibrary(pending?.RequestJson ?? run!.RequestJson))], token)).Single(), true);
            }
            var receipt = new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Category = "library-promotion",
                Action = "library-addition.accepted",
                Outcome = "succeeded",
                CorrelationId = hash,
                SubjectType = "library-addition",
                DetailsJson = JsonSerializer.Serialize(request)
            };
            return (await AddAsync(context, request, token, clientReceipt: receipt), false);
        }
        finally { additionGate.Release(); }
    }

    public async Task<ClientLibraryAdditionCapabilities> CapabilitiesAsync(ProtocolExecutionContext context, CancellationToken token = default)
    {
        var direct = await promotions.CanAddDirectlyAsync(context, token);
        // Apps read this at startup, so it skips working out which folder adds would go to.
        return new(direct, !direct, (await profiles.GetMineAsync(context, token, effectiveRoot: false)).Values.DefaultUnit);
    }

    public async Task<ClientLibraryAddition> AddAsync(ProtocolExecutionContext context, AddClientLibraryItem request,
        CancellationToken token = default, string? requestKey = null, AuditEventRecord? clientReceipt = null)
    {
        if (string.IsNullOrWhiteSpace(request.ItemId) || request.ItemId.Length > 500 || request.Unit is not ("song" or "album"))
            throw new ArgumentException("Choose a song or album.");
        if (requestKey != null)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var owner = (await PromotionProfiles.RequireUserAsync(db, context, token)).Id;
            var previous = (await db.PromotionRequests.AsNoTracking().Where(item => item.RequesterUserId == owner && item.RequestJson.Contains(requestKey))
                .ToArrayAsync(token)).SingleOrDefault(item => JsonSerializer.Deserialize<PromoteSongRequest>(item.RequestJson)!.RequestKey == requestKey);
            if (previous != null)
            {
                var current = await promotions.GetRequestAsync(context, previous.Id, token);
                var run = current.RunId is { } id ? await db.Set<PromotionRunRecord>().AsNoTracking().SingleAsync(item => item.Id == id, token) : null;
                return (await ResolveLibraryItemsAsync(context, [(From(current, run), ReadLibrary(current.RequestJson))], token)).Single();
            }
        }
        var itemId = request.ItemId;
        var parsed = LocalLibraryService.ParseExternalResource(itemId);
        if (parsed.isExternal && parsed.type != "song")
        {
            if (parsed.type != "album" || request.Unit != "album") throw new ArgumentException("Choose a song or album.");
            var album = await providers.GetAlbumAsync(context, parsed.provider!, parsed.externalId!) ?? throw new KeyNotFoundException();
            itemId = album.Songs.FirstOrDefault()?.Id ?? throw new KeyNotFoundException();
        }
        var recording = await identities.ResolveRecordingAsync(context.RequireActor(), itemId, token) ?? throw new KeyNotFoundException();
        var profile = (await profiles.GetMineAsync(context, token)).Values;
        var root = await roots.RequireAsync(profile.LibraryRootId ?? Guid.Empty, token);
        if (string.IsNullOrWhiteSpace(root.BackendLibraryId) || !(await access.ResolveAsync(context, token)).Allows(root.BackendLibraryId))
            throw new UnauthorizedAccessException();
        var result = await promotions.StartOrRequestAsync(context,
            new(recording.Id, Guid.Empty, root.Id, root.BackendLibraryId, requestKey ?? $"client:{Guid.CreateVersion7():N}")
            { Unit = request.Unit, RequestedItemId = request.ItemId }, token, clientReceipt: clientReceipt);
        var value = result.Request is { } pending ? From(pending, result.Run) : From(result.Run!);
        return (await ResolveLibraryItemsAsync(context, [(value, root.BackendLibraryId)], token)).Single();
    }

    public async Task<ClientLibraryAddition[]> ListAsync(ProtocolExecutionContext context, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var owner = (await PromotionProfiles.RequireUserAsync(db, context, token)).Id;
        var requests = await db.PromotionRequests.AsNoTracking().Where(item => item.RequesterUserId == owner).ToArrayAsync(token);
        foreach (var request in requests) await promotions.GetRequestAsync(context, request.Id, token);
        requests = await db.PromotionRequests.AsNoTracking().Where(item => item.RequesterUserId == owner).ToArrayAsync(token);
        var runIds = requests.Where(item => item.RunId != null).Select(item => item.RunId!.Value).ToArray();
        var runs = await db.Set<PromotionRunRecord>().AsNoTracking().Where(item => item.OwnerUserId == owner || runIds.Contains(item.Id)).ToArrayAsync(token);
        var representedRunIds = requests.Where(item => item.RunId != null && runs.Any(run => run.Id == item.RunId &&
                From(run).RequestedItemId == From(item).RequestedItemId)).Select(item => item.RunId!.Value).ToArray();
        var jobIds = runs.Select(run => run.JobId).ToArray();
        var jobs = await db.Jobs.AsNoTracking().Where(item => jobIds.Contains(item.Id)).ToArrayAsync(token);
        foreach (var run in runs)
            if (run.Outcome is "Queued" or "Running" && jobs.Single(item => item.Id == run.JobId).State is DurableJobState.Failed or DurableJobState.Cancelled)
                run.Outcome = jobs.Single(item => item.Id == run.JobId).State == DurableJobState.Cancelled ? "Cancelled" : "Failed";
        var values = requests.Select(item => (item.CreatedAt, Value: From(item, runs.SingleOrDefault(run => run.Id == item.RunId)), Library: ReadLibrary(item.RequestJson)))
            .Concat(runs.Where(item => item.OwnerUserId == owner && !representedRunIds.Contains(item.Id)).Select(item => (item.CreatedAt, Value: From(item), Library: ReadLibrary(item.RequestJson))))
            .OrderByDescending(item => item.CreatedAt).Select(item => (item.Value, item.Library)).ToArray();
        return await ResolveLibraryItemsAsync(context, values, token);
    }

    private async Task<ClientLibraryAddition[]> ResolveLibraryItemsAsync(ProtocolExecutionContext context,
        (ClientLibraryAddition Value, string Library)[] values, CancellationToken token)
    {
        if (values.All(item => item.Value.State != "done")) return values.Select(item => item.Value).ToArray();
        await using var db = await factory.CreateDbContextAsync(token);
        return await ResolveLibraryItemsAsync(db, new(context, await access.ResolveAsync(context, token)), values, token);
    }

    internal static string ReadLibrary(string json) => JsonSerializer.Deserialize<PromoteSongRequest>(json)!.BackendLibraryId;

    internal static async Task<ClientLibraryAddition[]> ResolveLibraryItemsAsync(AllstarrDbContext db,
        BackendLibraryAccessContext access, (ClientLibraryAddition Value, string Library)[] values, CancellationToken token)
    {
        var recordingIds = values.Where(item => item.Value.State == "done")
            .Select(item => Guid.Parse(item.Value.ItemId["ext-allstarr-song-".Length..])).Distinct().ToArray();
        if (recordingIds.Length == 0 || access.Context == null) return values.Select(item => item.Value).ToArray();
        var owner = access.Context?.Principal?.UserId;
        var linked = await db.AuditEvents.AsNoTracking().Where(item => item.ActorUserId == owner &&
            item.Action == "promotion.library-linked").OrderByDescending(item => item.CreatedAt).ToArrayAsync(token);
        var matches = linked.Select(item => (RecordingId: JsonSerializer.Deserialize<Dictionary<string, Guid>>(item.DetailsJson)!["RecordingId"],
            TrackId: Guid.Parse(item.SubjectId!))).Where(item => recordingIds.Contains(item.RecordingId)).ToArray();
        var trackIds = matches.Select(item => item.TrackId).ToArray();
        var tracks = await LibraryTrackAccess.Query(db, access).Where(item => item.CanonicalRecordingId != null &&
            recordingIds.Contains(item.CanonicalRecordingId.Value) || trackIds.Contains(item.Id)).OrderBy(item => item.BackendItemId).ToArrayAsync(token);
        return values.Select(item => item.Value.State != "done" ? item.Value : item.Value with
        {
            LibraryItemId = matches.Where(match => item.Value.ItemId == $"ext-allstarr-song-{match.RecordingId:D}")
                .Select(match => tracks.FirstOrDefault(track => track.Id == match.TrackId && track.BackendLibraryId == item.Library))
                .FirstOrDefault(track => track != null)?.BackendItemId ?? tracks.FirstOrDefault(track => track.BackendLibraryId == item.Library &&
                item.Value.ItemId == $"ext-allstarr-song-{track.CanonicalRecordingId:D}")?.BackendItemId
        }).ToArray();
    }

    public async Task<ClientLibraryAddition> CancelAsync(ProtocolExecutionContext context, Guid id, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var owner = (await PromotionProfiles.RequireUserAsync(db, context, token)).Id;
        var request = await db.PromotionRequests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.RequesterUserId == owner, token);
        if (request != null)
        {
            if (request.State == "pending") return From(await promotions.CancelRequestAsync(context, id, token));
            if (request.State != "approved" || request.RunId is not { } runId ||
                !await db.Set<PromotionRunRecord>().AnyAsync(item => item.Id == runId && item.OwnerUserId == owner, token))
                throw new PromotionException(PromotionFailure.PromotionChanged);
            var cancelled = await promotions.CancelAsync(context, runId, token);
            return From(await promotions.GetRequestAsync(context, id, token), cancelled);
        }
        if (!await db.Set<PromotionRunRecord>().AnyAsync(item => item.Id == id && item.OwnerUserId == owner, token)) throw new KeyNotFoundException();
        return From(await promotions.CancelAsync(context, id, token));
    }

    internal static ClientLibraryAddition From(PromotionRunRecord run)
    {
        var request = JsonSerializer.Deserialize<PromoteSongRequest>(run.RequestJson)!;
        return new(run.Id, $"ext-allstarr-song-{request.RecordingId:D}",
            request.Unit ?? LibraryPromotionService.ReadAlbum(run)?.Profile.DefaultUnit ?? "song", RunState(run), Reason(RunState(run)))
        { RequestedItemId = request.RequestedItemId };
    }

    internal static ClientLibraryAddition From(PromotionRequestRecord request, PromotionRunRecord? run = null)
    {
        var state = request.State switch
        {
            "pending" => "waitingForApproval",
            "declined" => "declined",
            "cancelled" => "cancelled",
            "added" => "done",
            "failed" => run?.Outcome == "Cancelled" ? "cancelled" : "needsLook",
            _ => run == null ? "adding" : RunState(run)
        };
        return new(request.Id, $"ext-allstarr-song-{request.RecordingId:D}", request.Unit, state,
            request.State == "declined" && request.DeclineNote is { Length: > 0 } note ? note : Reason(state))
        { RequestedItemId = JsonSerializer.Deserialize<PromoteSongRequest>(request.RequestJson)!.RequestedItemId };
    }

    private static string RunState(PromotionRunRecord run) => run.Outcome switch
    {
        "Placed" => "done",
        "Cancelled" => "cancelled",
        "Needs review" or "Failed" => "needsLook",
        _ => "adding"
    };

    private static string Reason(string state) => state switch
    {
        "waitingForApproval" => "Waiting for household approval.",
        "needsLook" => "Ask an administrator to review this addition.",
        "done" => "Added to the library.",
        "declined" => "The library request was declined.",
        "cancelled" => "Cancelled; any files already added stay in the library.",
        _ => "Adding to the library."
    };
}
