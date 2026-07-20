using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using allstarr.Services.Jellyfin;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    [HttpGet("Items/Filters", Order = 1)]
    [HttpGet("Items/Filters2", Order = 1)]
    [HttpGet("Genres", Order = 1)]
    [HttpGet("Items/Latest", Order = 1)]
    public Task<IActionResult> GetConstrainedMusicBrowse() =>
        ProxyConstrainedMusicQueryAsync("IncludeItemTypes", JellyfinMusicEndpointPolicy.DefaultMusicItemTypes);

    [HttpGet("Items/Suggestions", Order = 1)]
    public Task<IActionResult> GetConstrainedMusicSuggestions() =>
        ProxyConstrainedMusicQueryAsync("Type", JellyfinMusicEndpointPolicy.DefaultMusicItemTypes);

    [HttpGet("UserItems/Resume", Order = 1)]
    public Task<IActionResult> GetConstrainedMusicResume() =>
        ProxyConstrainedMusicQueryAsync("IncludeItemTypes", JellyfinMusicEndpointPolicy.DefaultMusicItemTypes,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MediaTypes"] = "Audio" });

    [HttpGet("Library/MediaFolders", Order = 1)]
    [HttpGet("UserViews", Order = 1)]
    public async Task<IActionResult> GetMusicLibraryViews()
    {
        var endpoint = BuildCurrentEndpoint();
        var (body, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
        if (body == null) return HandleProxyResponse(body, statusCode);

        using (body)
        {
            var root = JsonNode.Parse(body.RootElement.GetRawText()) as JsonObject;
            if (root?["Items"] is JsonArray items)
            {
                var musicLibraryId = await _proxyService.GetMusicLibraryIdForFilteringAsync();
                var filtered = items
                    .Where(item => item is JsonObject candidate &&
                                   (string.Equals(candidate["CollectionType"]?.GetValue<string>(), "music", StringComparison.OrdinalIgnoreCase) ||
                                    (!string.IsNullOrWhiteSpace(musicLibraryId) &&
                                     string.Equals(candidate["Id"]?.GetValue<string>(), musicLibraryId, StringComparison.Ordinal))))
                    .Select(item => item!.DeepClone())
                    .ToArray();
                items.Clear();
                foreach (var item in filtered) items.Add(item);
                root["TotalRecordCount"] = filtered.Length;
            }

            return Content(root?.ToJsonString() ?? "{}", "application/json");
        }
    }

    [HttpGet("Items/Root", Order = 1)]
    public async Task<IActionResult> GetMusicLibraryRoot()
    {
        var musicLibraryId = await _proxyService.GetMusicLibraryIdForFilteringAsync();
        if (string.IsNullOrWhiteSpace(musicLibraryId))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "A Jellyfin music library could not be identified."
            });
        }

        var (body, statusCode) = await _proxyService.GetJsonAsync(
            $"Items/{Uri.EscapeDataString(musicLibraryId)}",
            null,
            Request.Headers);
        return HandleProxyResponse(body, statusCode);
    }

    [HttpGet("Items/Counts", Order = 1)]
    public async Task<IActionResult> GetMusicItemCounts()
    {
        var (body, statusCode) = await _proxyService.GetJsonAsync(BuildCurrentEndpoint(), null, Request.Headers);
        if (body == null) return HandleProxyResponse(body, statusCode);

        using (body)
        {
            var root = JsonNode.Parse(body.RootElement.GetRawText()) as JsonObject ?? new JsonObject();
            foreach (var key in new[]
                     {
                         "MovieCount", "SeriesCount", "EpisodeCount", "MusicVideoCount",
                         "TrailerCount", "ProgramCount", "BookCount", "BoxSetCount"
                     })
            {
                root[key] = 0;
            }

            root["ItemCount"] = Number(root, "SongCount") + Number(root, "AlbumCount") + Number(root, "ArtistCount");
            return Content(root.ToJsonString(), "application/json");
        }
    }

    [HttpGet("Items/{itemId}/PlaybackInfo", Order = 1)]
    [HttpPost("Items/{itemId}/PlaybackInfo", Order = 1)]
    public async Task<IActionResult> GetMusicPlaybackInfo(string itemId)
    {
        var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(itemId);
        if (!isExternal)
        {
            var upstream = BuildCurrentEndpoint();
            return await ProxyPlaybackInfoAsync(upstream);
        }

        if (!string.Equals(type, "song", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(externalId))
        {
            return NotFound(new { error = "The external item is not a playable track." });
        }

        var song = await GetProviderSongAsync(provider, externalId, HttpContext.RequestAborted);
        if (song == null) return NotFound(new { error = "Track metadata was not found." });

        // PlaybackInfo must describe the same synthesized item that the client
        // requested. Provider metadata adapters may return a canonical/alternate
        // identifier; using it here would build stream URLs for a different item.
        song.Id = itemId;
        song.ExternalProvider = provider;
        song.ExternalId = externalId;

        var item = _responseBuilder.ConvertSongToJellyfinItem(song);
        item.TryGetValue("MediaSources", out var mediaSources);
        return new JsonResult(new
        {
            MediaSources = mediaSources ?? Array.Empty<object>(),
            PlaySessionId = Guid.NewGuid().ToString("N"),
            ErrorCode = (string?)null
        });
    }

    private async Task<IActionResult> ProxyPlaybackInfoAsync(string endpoint)
    {
        if (HttpMethods.IsPost(Request.Method))
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync(HttpContext.RequestAborted);
            var (result, statusCode) = await _proxyService.PostJsonAsync(endpoint, body, Request.Headers);
            return HandleProxyResponse(result, statusCode);
        }

        var (getResult, getStatus) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
        return HandleProxyResponse(getResult, getStatus);
    }

    private async Task<IActionResult> ProxyConstrainedMusicQueryAsync(
        string parameterName,
        string defaults,
        IReadOnlyDictionary<string, string>? additionalDefaults = null)
    {
        var query = QueryHelpers.ParseQuery(Request.QueryString.Value ?? string.Empty)
            .ToDictionary(entry => entry.Key, entry => entry.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        if (!query.TryGetValue(parameterName, out var types) || string.IsNullOrWhiteSpace(types))
        {
            query[parameterName] = defaults;
        }
        if (additionalDefaults != null)
        {
            foreach (var (key, value) in additionalDefaults)
            {
                if (!query.TryGetValue(key, out var current) || string.IsNullOrWhiteSpace(current)) query[key] = value;
            }
        }

        var path = Request.Path.Value?.TrimStart('/') ?? string.Empty;
        var endpoint = query.Count == 0
            ? path
            : path + QueryString.Create(query.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)));
        var (body, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
        return HandleProxyResponse(body, statusCode);
    }

    private async Task<IActionResult> ProxyMusicItemsResponseAsync(string endpoint)
    {
        var (body, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
        if (body == null) return HandleProxyResponse(body, statusCode);

        using (body)
        {
            var root = JsonNode.Parse(body.RootElement.GetRawText()) as JsonObject;
            if (root?["Items"] is not JsonArray items)
            {
                return new ContentResult
                {
                    Content = root?.ToJsonString() ?? "{}",
                    ContentType = "application/json",
                    StatusCode = statusCode
                };
            }

            var filtered = items
                .Where(item => item is JsonObject candidate &&
                               JellyfinMusicEndpointPolicy.IsMusicItemType(candidate["Type"]?.GetValue<string>()))
                .Select(item => item!.DeepClone())
                .ToArray();
            items.Clear();
            foreach (var item in filtered) items.Add(item);
            root["TotalRecordCount"] = filtered.Length;
            return new ContentResult
            {
                Content = root.ToJsonString(),
                ContentType = "application/json",
                StatusCode = statusCode
            };
        }
    }

    private string BuildCurrentEndpoint() =>
        (Request.Path.Value?.TrimStart('/') ?? string.Empty) + Request.QueryString.Value;

    private static int Number(JsonObject root, string name) =>
        root[name]?.GetValue<int>() ?? 0;
}
