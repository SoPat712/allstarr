using System.Text.Json;
using allstarr.Core.Enrichment;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

public sealed record AddClientLibraryItem(string ItemId, string Unit);
public sealed record ClientLibraryAddition(Guid Id, string ItemId, string Unit, string State, string Reason);
public sealed record ClientLibraryAdditionCapabilities(bool CanAddDirectly, bool RequiresApproval, string DefaultUnit);

public sealed class ClientLibraryAdditions(IDbContextFactory<AllstarrDbContext> factory, LibraryPromotionService promotions,
    PromotionProfiles profiles, PromotionRoots roots, ITrackIdentityService identities, IBackendLibraryAccessResolver access,
    IProtocolProviderGateway providers)
{
    public async Task<ClientLibraryAdditionCapabilities> CapabilitiesAsync(ProtocolExecutionContext context, CancellationToken token = default)
    {
        var direct = await promotions.CanAddDirectlyAsync(context, token);
        return new(direct, !direct, (await profiles.GetMineAsync(context, token)).Values.DefaultUnit);
    }

    public async Task<ClientLibraryAddition> AddAsync(ProtocolExecutionContext context, AddClientLibraryItem request,
        CancellationToken token = default, string? requestKey = null)
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
                return From(current, run);
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
            new(recording.Id, Guid.Empty, root.Id, root.BackendLibraryId, requestKey ?? $"client:{Guid.CreateVersion7():N}") { Unit = request.Unit }, token);
        return result.Request is { } pending ? From(pending, result.Run) : From(result.Run!);
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
        var jobIds = runs.Select(run => run.JobId).ToArray();
        var jobs = await db.Jobs.AsNoTracking().Where(item => jobIds.Contains(item.Id)).ToArrayAsync(token);
        foreach (var run in runs)
            if (run.Outcome is "Queued" or "Running" && jobs.Single(item => item.Id == run.JobId).State is DurableJobState.Failed or DurableJobState.Cancelled)
                run.Outcome = jobs.Single(item => item.Id == run.JobId).State == DurableJobState.Cancelled ? "Cancelled" : "Failed";
        return requests.Select(item => (item.CreatedAt, Value: From(item, runs.SingleOrDefault(run => run.Id == item.RunId))))
            .Concat(runs.Where(item => item.OwnerUserId == owner && !runIds.Contains(item.Id)).Select(item => (item.CreatedAt, Value: From(item))))
            .OrderByDescending(item => item.CreatedAt).Select(item => item.Value).ToArray();
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
            request.Unit ?? LibraryPromotionService.ReadAlbum(run)?.Profile.DefaultUnit ?? "song", RunState(run), Reason(RunState(run)));
    }

    internal static ClientLibraryAddition From(PromotionRequestRecord request, PromotionRunRecord? run = null)
    {
        var state = request.State switch
        {
            "pending" => "waitingForApproval", "declined" => "declined", "cancelled" => "cancelled",
            "added" => "done", "failed" => run?.Outcome == "Cancelled" ? "cancelled" : "needsLook",
            _ => run == null ? "adding" : RunState(run)
        };
        return new(request.Id, $"ext-allstarr-song-{request.RecordingId:D}", request.Unit, state,
            request.State == "declined" && request.DeclineNote is { Length: > 0 } note ? note : Reason(state));
    }

    private static string RunState(PromotionRunRecord run) => run.Outcome switch
    {
        "Placed" => "done", "Cancelled" => "cancelled", "Needs review" or "Failed" => "needsLook", _ => "adding"
    };

    private static string Reason(string state) => state switch
    {
        "waitingForApproval" => "Waiting for household approval.", "needsLook" => "Ask an administrator to review this addition.",
        "done" => "Added to the library.", "declined" => "The library request was declined.",
        "cancelled" => "Cancelled; any files already added stay in the library.", _ => "Adding to the library."
    };
}
