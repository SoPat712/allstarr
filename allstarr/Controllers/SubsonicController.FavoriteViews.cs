using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Protocols.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    [HttpGet("rest/getStarred")]
    [HttpPost("rest/getStarred")]
    [HttpGet("rest/getStarred.view")]
    [HttpPost("rest/getStarred.view")]
    public Task<IActionResult> GetStarred() => ReadStarredAsync("starred");

    [HttpGet("rest/getStarred2")]
    [HttpPost("rest/getStarred2")]
    [HttpGet("rest/getStarred2.view")]
    [HttpPost("rest/getStarred2.view")]
    public Task<IActionResult> GetStarred2() => ReadStarredAsync("starred2");

    private async Task<IActionResult> ReadStarredAsync(string container)
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        IReadOnlyList<FavoriteViewItem> saved = [];
        if (_favoriteViews != null && CurrentProtocolContext.CanRunUserScopedWork &&
            !parameters.HasNonEmptyValue("musicFolderId"))
        {
            try
            {
                saved = (await _favoriteViews.ReadAsync(CurrentProtocolContext, HttpContext.RequestAborted))
                    .Where(item => item.Resource.ResourceKind != ProviderResourceKind.Playlist).ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { _logger.LogWarning("Saved favorites are unavailable ({ExceptionType})", exception.GetType().Name); }
        }
        IHeaderDictionary headers = Request.Headers;
        if (saved.Count != 0)
            headers = new HeaderDictionary(Request.Headers.Where(header =>
                    !header.Key.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("If-Modified-Since", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase));
        try
        {
            var native = await _proxyService.RelayRawAsync(Request.Path.Value!.TrimStart('/'), parameters,
                HttpContext.RequestAborted, headers);
            var merged = SubsonicFavoriteProtocolAdapter.Merge(native, saved, container, format, _responseBuilder);
            if (!ReferenceEquals(native, merged)) Response.Headers.CacheControl = "no-store";
            return _relayProtocolAdapter.CreateResult(merged, $"application/{format}");
        }
        catch (HttpRequestException)
        { return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server"); }
    }
}
