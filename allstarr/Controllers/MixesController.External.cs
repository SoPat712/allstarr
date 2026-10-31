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
            if (!(bool)(await settings.GetAsync(PersonalMixService.PublishKey, token)).Value) throw new UnauthorizedAccessException();
            await using var db = await factory.CreateDbContextAsync(token);
            var account = await db.ProviderAccounts.AsNoTracking().Where(item => item.OwnerUserId == user.Id &&
                item.ProviderId == source && item.Enabled).OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
                .FirstOrDefaultAsync(token) ?? throw new ArgumentException("Connect your personal source account first.");
            var links = HttpContext.RequestServices.GetService<PlaylistLinksController>() ??
                ActivatorUtilities.CreateInstance<PlaylistLinksController>(HttpContext.RequestServices);
            links.ControllerContext = ControllerContext;
            var created = await links.CreateExternalMixAsync(new(account.Id, source!, selection,
                user.BackendType, user.BackendInstanceId, "virtual", "reconcile"), token);
            if (created is not CreatedAtActionResult response) return created;
            var value = JsonSerializer.SerializeToElement(response.Value);
            var id = value.GetProperty("id").GetGuid();
            var initialJobId = value.GetProperty("initialJobId").GetGuid();
            await mixes.SetInAppsAsync(user.Id, id, true, token);
            var link = await db.PlaylistLinks.AsNoTracking().SingleAsync(item => item.Id == id && item.OwnerUserId == user.Id, token);
            return Created($"/api/admin/mixes/{id}", Card(PersonalMixService.PendingExternalMix(link),
                (bool)(await settings.GetAsync(PersonalMixService.PublishKey, token)).Value, initialJobId));
        }, token);
        return result is OkObjectResult { Value: IActionResult response } ? response : result;
    }
}

public sealed record ExternalMixRequest(string Source, string Kind, string? Period = null);
