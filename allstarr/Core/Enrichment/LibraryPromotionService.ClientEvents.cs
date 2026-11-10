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
        events.Publish(run.OwnerUserId, ClientEvent.LibraryAddition(ClientLibraryAdditions.From(run)));
        await using var db = await factory.CreateDbContextAsync(token);
        foreach (var request in await db.PromotionRequests.AsNoTracking().Where(item => item.RunId == run.Id).ToArrayAsync(token))
            events.Publish(request.RequesterUserId, ClientEvent.LibraryRequest(ClientLibraryAdditions.From(request, run)));
    }

    private async Task PublishRequestAsync(PromotionRequestRecord request, CancellationToken token)
    {
        if (events == null) return;
        await using var db = await factory.CreateDbContextAsync(token);
        var run = request.RunId is { } runId ? await db.Set<PromotionRunRecord>().AsNoTracking().SingleAsync(item => item.Id == runId, token) : null;
        events.Publish(request.RequesterUserId, ClientEvent.LibraryRequest(ClientLibraryAdditions.From(request, run)));
    }
}
