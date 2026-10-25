using System.Globalization;
using System.Text.Json.Nodes;
using allstarr.Core.Favorites;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Filters;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[TypeFilter(typeof(JellyfinHistoryQueryFilter))]
public partial class JellyfinController
{
    [HttpPost("UserPlayedItems/{itemId}")]
    [HttpDelete("UserPlayedItems/{itemId}")]
    [HttpPost("Users/{userId}/PlayedItems/{itemId}")]
    [HttpDelete("Users/{userId}/PlayedItems/{itemId}")]
    public async Task<IActionResult> SetProviderPlayed(string itemId, string? userId = null)
    {
        if (!itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
            return await ProxyRequest(Request.Path.Value!.TrimStart('/'));
        var context = HttpContext.RequireProtocolExecutionContext();
        if (new[] { userId, Request.Query["UserId"].ToString() }.Any(value =>
                !string.IsNullOrWhiteSpace(value) && value != context.VerifiedBackendPrincipalId))
            return StatusCode(StatusCodes.Status403Forbidden);
        var playedAt = DateTimeOffset.UtcNow;
        var date = Request.Query["DatePlayed"].ToString();
        if (date.Length > 0 && !DateTimeOffset.TryParseExact(date, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out playedAt) &&
            !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out playedAt))
            return BadRequest(new { error = "DatePlayed is invalid" });
        var state = HttpContext.RequestServices.GetRequiredService<ProviderUserDataStore>();
        if (!await state.SetPlayedAsync(context, itemId, HttpMethods.IsPost(Request.Method), playedAt, HttpContext.RequestAborted))
            return StatusCode(StatusCodes.Status403Forbidden);
        var saved = await state.ReadForRequestAsync(HttpContext, [itemId]);
        var favorites = HttpContext.RequestServices.GetService<FavoriteViewReader>();
        IReadOnlyList<FavoriteViewItem> favoriteView = favorites == null ? [] : await favorites.ReadForRequestAsync(HttpContext);
        var favorite = favoriteView.Select(item => item.ItemId).Contains(itemId, FavoriteViewReader.ItemComparer(favoriteView));
        var data = new JsonObject { ["ItemId"] = itemId, ["Key"] = itemId, ["IsFavorite"] = favorite };
        MusicUserDataProjection.Jellyfin(data, saved.GetValueOrDefault(itemId) ?? new());
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(data);
    }
}
