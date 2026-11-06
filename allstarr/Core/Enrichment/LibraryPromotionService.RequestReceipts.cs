using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed partial class LibraryPromotionService
{
    public async Task<IReadOnlyList<PromotionRequestReceipt>> ListRequestReceiptsAsync(ProtocolExecutionContext context,
        string? state, CancellationToken token = default) =>
        await ProjectRequestReceiptsAsync(await ListRequestsAsync(context, state, token), token);

    internal async Task<PromotionRequestReceipt> RequestReceiptAsync(PromotionRequestRecord request,
        CancellationToken token = default) => (await ProjectRequestReceiptsAsync([request], token)).Single();

    private async Task<IReadOnlyList<PromotionRequestReceipt>> ProjectRequestReceiptsAsync(
        IReadOnlyList<PromotionRequestRecord> requests, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var owners = requests.Select(item => item.RequesterUserId).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(item => owners.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.DisplayName, token);
        return requests.Select(item => new PromotionRequestReceipt(item.Id, item.RequesterUserId, item.RecordingId,
            item.ReleaseIdentity, item.Title, item.Artist, item.Year, item.SongCount, item.CoverReference,
            item.Unit, item.State, item.DecidedByUserId, item.DecidedAt, item.DeclineNote, item.RunId,
            item.CreatedAt, new(item.RequesterUserId, users.GetValueOrDefault(item.RequesterUserId) ?? ""))).ToArray();
    }

    public async Task<PromotionPreview> PreviewRequestAsync(ProtocolExecutionContext context, Guid id,
        CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await PromotionProfiles.RequireUserAsync(db, context, token, administrator: true);
        var pending = await OwnedRequestAsync(db, context, id, token);
        var access = await libraryAccess.ResolveUserAsync(pending.RequesterUserId, token);
        var requester = access.Context ?? throw new UnauthorizedAccessException();
        await PromotionProfiles.RequireUserAsync(db, requester, token);
        var request = JsonSerializer.Deserialize<PromoteSongRequest>(pending.RequestJson)!;
        if (!access.Access.Allows(request.BackendLibraryId)) throw new UnauthorizedAccessException();
        var songs = JsonSerializer.Deserialize<List<Song>>(pending.SongsJson)!;
        var preview = promotionCatalog == null ? await PreviewAsync(requester, request, token) :
            await PreviewAlbumAsync(requester, request, token, songs);
        return preview with { CanAddDirectly = await CanAddDirectlyAsync(requester, token) };
    }
}
