using allstarr.Core.ClientApi;
using allstarr.Core.ClientEvents;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed partial class LibraryPromotionService
{
    private async Task PublishRunAsync(PromotionRunRecord run, CancellationToken token)
    {
        if (events == null) return;
        await using var db = await factory.CreateDbContextAsync(token);
        var value = await ProjectClientAdditionAsync(db, run.OwnerUserId, ClientLibraryAdditions.From(run), run.RequestJson, token);
        events.Publish(run.OwnerUserId, ClientEvent.LibraryAddition(value));
        foreach (var request in await db.PromotionRequests.AsNoTracking().Where(item => item.RunId == run.Id).ToArrayAsync(token))
            events.Publish(request.RequesterUserId, ClientEvent.LibraryRequest(await ProjectClientAdditionAsync(db,
                request.RequesterUserId, ClientLibraryAdditions.From(request, run), request.RequestJson, token)));
    }

    private async Task PublishRequestAsync(PromotionRequestRecord request, CancellationToken token)
    {
        if (events == null) return;
        await using var db = await factory.CreateDbContextAsync(token);
        var run = request.RunId is { } runId ? await db.Set<PromotionRunRecord>().AsNoTracking().SingleAsync(item => item.Id == runId, token) : null;
        events.Publish(request.RequesterUserId, ClientEvent.LibraryRequest(await ProjectClientAdditionAsync(db,
            request.RequesterUserId, ClientLibraryAdditions.From(request, run), request.RequestJson, token)));
    }

    private async Task<ClientLibraryAddition> ProjectClientAdditionAsync(AllstarrDbContext db, Guid owner,
        ClientLibraryAddition value, string requestJson, CancellationToken token) => value.State != "done" ? value :
        (await ClientLibraryAdditions.ResolveLibraryItemsAsync(db, await libraryAccess.ResolveUserAsync(owner, token),
            [(value, ClientLibraryAdditions.ReadLibrary(requestJson))], token)).Single();

    internal async Task PublishLinkedAdditionsAsync(Guid owner, Guid recordingId, CancellationToken token)
    {
        if (events == null) return;
        await using var db = await factory.CreateDbContextAsync(token);
        var runs = await db.Set<PromotionRunRecord>().AsNoTracking().Where(item => item.OwnerUserId == owner &&
            item.Outcome == "Placed" && item.RequestJson.Contains(recordingId.ToString())).ToArrayAsync(token);
        foreach (var run in runs.Where(item => System.Text.Json.JsonSerializer.Deserialize<PromoteSongRequest>(item.RequestJson)!.RecordingId == recordingId))
            await PublishRunAsync(run, token);
    }
}
