using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Providers.ListenBrainz;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

public sealed partial class MixesController
{
    [HttpPost("external")]
    public async Task<IActionResult> External([FromBody] ExternalMixRequest request, CancellationToken token)
    {
        var result = await ExecuteAsync(async user =>
        {
            var source = request.Source?.Trim().ToLowerInvariant();
            var kind = request.Kind?.Trim().ToLowerInvariant();
            string selection;
            if (source == "listenbrainz" && request.Period == null && !string.IsNullOrWhiteSpace(kind) &&
                ListenBrainzPlaylistCapabilityAdapter.TryNormalize("createdfor:" + kind, out var normalized)) selection = normalized;
            else if (source == "lastfm" && kind is "loved" or "similar" && request.Period == null) selection = kind;
            else if (source == "lastfm" && kind == "top" &&
                (request.Period ?? "1month") is "7day" or "1month" or "3month" or "12month" or "overall")
                selection = "top:" + (request.Period ?? "1month") + ":50";
            else throw new ArgumentException("Unknown external mix selection.");
            await using var db = await factory.CreateDbContextAsync(token);
            var account = await db.ProviderAccounts.AsNoTracking().Where(item => item.OwnerUserId == user.Id &&
                item.ProviderId == source && item.Enabled).OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
                .FirstOrDefaultAsync(token) ?? throw new ArgumentException("Connect your personal source account first.");
            var id = await db.PlaylistLinks.AsNoTracking().Where(item => item.OwnerUserId == user.Id &&
                item.ProviderAccountId == account.Id && item.SourceProviderId == source && item.SourcePlaylistId == selection &&
                item.TargetProtocol == user.BackendType && item.TargetBackendInstanceId == user.BackendInstanceId &&
                item.ImportMode == PlaylistImportMode.Linked).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(token);
            if (id == null)
            {
                var links = HttpContext.RequestServices.GetService<PlaylistLinksController>() ??
                    ActivatorUtilities.CreateInstance<PlaylistLinksController>(HttpContext.RequestServices);
                links.ControllerContext = ControllerContext;
                var created = await links.CreateExternalMixAsync(new(account.Id, source!, selection,
                    user.BackendType, user.BackendInstanceId, "virtual", "reconcile", FollowNewest: source == "listenbrainz"), token);
                if (created is not CreatedAtActionResult response) return created;
                id = JsonSerializer.SerializeToElement(response.Value).GetProperty("id").GetGuid();
            }
            await mixes.SetInAppsAsync(user.Id, id.Value, true, token);
            var mix = (await mixes.ListAsync(user.Id, token: token)).Single(item => item.Id == id);
            return Created($"/api/admin/mixes/{id}", Card(mix, (bool)(await settings.GetAsync(PersonalMixService.PublishKey, token)).Value));
        }, token);
        return result is OkObjectResult { Value: IActionResult response } ? response : result;
    }
}

public sealed record ExternalMixRequest(string Source, string Kind, string? Period = null);
