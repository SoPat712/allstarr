using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Services.Jellyfin;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

public sealed class CleanOnlyProtocolFilter(CleanOnlyProtocolContent content, IProtocolProviderGateway providers,
    JellyfinResponseBuilder? jellyfin = null, SubsonicResponseBuilder? subsonic = null) : IAsyncActionFilter, IAsyncResultFilter
{
    private readonly JellyfinResponseBuilder _jellyfin = jellyfin ?? new();
    private readonly SubsonicResponseBuilder _subsonic = subsonic ?? new();
    private readonly Dictionary<string, allstarr.Models.Domain.Song?> _counterparts = new(StringComparer.Ordinal);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (http.GetProtocolExecutionContext() is { } protocol && await content.IsActiveAsync(protocol))
        {
            if (IsMedia(http.Request.Path))
            {
                if (http.Request.Path.Value?.Contains("getTranscodeStream", StringComparison.OrdinalIgnoreCase) == true)
                {
                    context.Result = Refused(http, protocol.Protocol);
                    return;
                }
                var parameter = http.Request.Path.Value?.Contains("getTranscodeDecision", StringComparison.OrdinalIgnoreCase) == true ? "mediaId" : "id";
                var id = (context.ActionArguments.TryGetValue("itemId", out var itemId) ? itemId?.ToString() : null) ??
                    (context.ActionArguments.TryGetValue("id", out var argumentId) ? argumentId?.ToString() : null) ?? Parameters(http).GetValueOrDefault(parameter);
                if (string.IsNullOrWhiteSpace(id) || !await content.AllowsAsync(http, id))
                {
                    context.Result = Refused(http, protocol.Protocol);
                    return;
                }
            }
            else
            {
                http.Request.Headers.Remove("If-None-Match");
                http.Request.Headers.Remove("If-Modified-Since");
            }
        }
        await next();
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (http.GetProtocolExecutionContext() is not { } protocol || IsMedia(http.Request.Path) ||
            http.Request.Path.Value?.Contains("/Images/", StringComparison.OrdinalIgnoreCase) == true ||
            http.Request.Path.Value?.Contains("getCoverArt", StringComparison.OrdinalIgnoreCase) == true ||
            !await content.IsActiveAsync(protocol))
        { await next(); return; }
        var response = http.Response;
        var destination = response.Body;
        await using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await next();
            var bytes = buffer.ToArray();
            if (response.StatusCode is >= 200 and < 300 && bytes.Length > 0)
            {
                var text = Encoding.UTF8.GetString(bytes);
                if (response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var root = JsonNode.Parse(text);
                    if (root != null)
                    {
                        if (await ProjectJsonAsync(root, protocol, http))
                        {
                            response.StatusCode = StatusCodes.Status404NotFound;
                            bytes = Encoding.UTF8.GetBytes("{}");
                        }
                        else bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
                    }
                }
                else if (response.ContentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var root = XDocument.Parse(text);
                    await ProjectXmlAsync(root, protocol, http);
                    bytes = Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting));
                }
            }
            response.Headers.CacheControl = "private, no-store";
            response.Headers.Remove("ETag"); response.Headers.Remove("Last-Modified");
            response.ContentLength = bytes.Length;
            response.Body = destination;
            if (!HttpMethods.IsHead(http.Request.Method)) await destination.WriteAsync(bytes, http.RequestAborted);
        }
        finally { response.Body = destination; }
    }

    private async Task<bool> ProjectJsonAsync(JsonNode node, ProtocolExecutionContext protocol, HttpContext http, string? key = null)
    {
        if (node is JsonArray array)
        {
            for (var index = array.Count - 1; index >= 0; index--)
            {
                if (array[index] is not { } child) continue;
                if (child is JsonObject row && IsSong(row, protocol.Protocol, key) &&
                    CleanOnlyProtocolContent.Rating(row, protocol.Protocol) is not (0 or 3))
                {
                    var replacement = await CounterpartAsync(CleanOnlyProtocolContent.Song(row, protocol.Protocol), protocol, http);
                    if (replacement == null) array.RemoveAt(index);
                    else
                    {
                        var rendered = Render(replacement, protocol.Protocol);
                        rendered[protocol.Protocol == ProtocolKind.Jellyfin ? "OriginalItemId" : "originalItemId"] =
                            row[protocol.Protocol == ProtocolKind.Jellyfin ? "Id" : "id"]?.DeepClone();
                        array[index] = rendered;
                    }
                }
                else if (await ProjectJsonAsync(child, protocol, http, key)) array.RemoveAt(index);
            }
        }
        else if (node is JsonObject item)
        {
            if (IsSong(item, protocol.Protocol, key) && CleanOnlyProtocolContent.Rating(item, protocol.Protocol) is not (0 or 3)) return true;

            foreach (var pair in item.ToArray())
            {
                if (pair.Value == null) continue;
                var before = (pair.Value as JsonArray)?.Count;
                if (await ProjectJsonAsync(pair.Value, protocol, http, pair.Key)) item.Remove(pair.Key);
                if (before is { } count && pair.Value is JsonArray rows && count != rows.Count)
                {
                    foreach (var countKey in new[] { "TotalRecordCount", "songCount", "childCount" })
                        if (item[countKey] is JsonValue value && value.TryGetValue<int>(out var total))
                            item[countKey] = Math.Max(0, total - count + rows.Count);
                }
            }
            var album = protocol.Protocol == ProtocolKind.Jellyfin
                ? CleanOnlyProtocolContent.Text(item, "Type") == "MusicAlbum"
                : key == "album" && item["id"] != null;
            if (album && CleanOnlyProtocolContent.Rating(item, protocol.Protocol) is not (0 or 3) &&
                (item[protocol.Protocol == ProtocolKind.Jellyfin ? "Children" : "song"] is not JsonArray songs || songs.Count == 0)) return true;
        }
        return false;
    }

    private async Task ProjectXmlAsync(XDocument document, ProtocolExecutionContext protocol, HttpContext http)
    {
        foreach (var row in document.Descendants().Where(element => element.Name.LocalName is "song" or "entry" ||
            element.Name.LocalName == "child" && element.Attribute("isDir")?.Value != "true").ToArray())
        {
            if (row.Attribute("explicitStatus")?.Value == "clean") continue;
            var source = new allstarr.Models.Domain.Song
            {
                Id = row.Attribute("id")?.Value ?? "",
                Title = row.Attribute("title")?.Value ?? "",
                Artist = row.Attribute("artist")?.Value ?? "",
                Album = row.Attribute("album")?.Value ?? "",
                Duration = int.TryParse(row.Attribute("duration")?.Value, out var duration) ? duration : null,
                ExplicitContentLyrics = row.Attribute("explicitStatus")?.Value == "explicit" ? 1 : null
            };
            var replacement = await CounterpartAsync(source, protocol, http);
            if (replacement != null)
            {
                var rendered = _subsonic.ConvertSongToXml(replacement, row.Name.Namespace);
                rendered.Name = row.Name;
                rendered.SetAttributeValue("originalItemId", source.Id);
                row.ReplaceWith(rendered);
            }
            else
            {
                var parent = row.Parent;
                row.Remove();
                if (parent?.Attribute("songCount") is { } count && int.TryParse(count.Value, out var total)) count.Value = Math.Max(0, total - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        foreach (var album in document.Descendants().Where(element => element.Name.LocalName == "album" && element.Attribute("id") != null).ToArray())
            if (album.Attribute("explicitStatus")?.Value != "clean" && !album.Elements().Any(element => element.Name.LocalName == "song")) album.Remove();
    }

    private async Task<allstarr.Models.Domain.Song?> CounterpartAsync(allstarr.Models.Domain.Song source,
        ProtocolExecutionContext protocol, HttpContext http)
    {
        if (source.ExplicitContentLyrics != 1 ||
            string.IsNullOrWhiteSpace(source.Artist) || string.IsNullOrWhiteSpace(source.Album) || source.Duration is not > 0) return null;
        var key = $"{source.Id}|{source.Title}|{source.Artist}|{source.Album}|{source.Duration}";
        try
        {
            if (!await content.IsPlaylistAsync(http)) return null;
            if (_counterparts.TryGetValue(key, out var known)) return known;
            var result = await providers.SearchAsync(protocol, $"{source.Title} {source.Artist}", 20, 0, 0);
            var replacement = result.Songs.FirstOrDefault(candidate => CleanRecordingPolicy.IsCounterpart(source, candidate));
            _counterparts[key] = replacement;
            return replacement;
        }
        catch (Exception) when (!protocol.CancellationToken.IsCancellationRequested)
        {
            _counterparts[key] = null;
            return null;
        }
    }

    private JsonNode Render(allstarr.Models.Domain.Song song, ProtocolKind protocol) => protocol == ProtocolKind.Jellyfin
        ? JsonSerializer.SerializeToNode(_jellyfin.ConvertSongToJellyfinItem(song))!
        : JsonSerializer.SerializeToNode(_subsonic.ConvertSongToJson(song))!;

    private static bool IsSong(JsonObject row, ProtocolKind protocol, string? key) => protocol == ProtocolKind.Jellyfin
        ? CleanOnlyProtocolContent.Text(row, "Type") == "Audio" || CleanOnlyProtocolContent.Text(row, "ItemType") == "Audio"
        : key is "song" or "entry" || key == "child" && row["isDir"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) != true;

    private static bool IsMedia(PathString path)
    {
        var value = path.Value ?? "";
        var endpoint = value.TrimEnd('/').Split('/').Last().Replace(".view", "", StringComparison.OrdinalIgnoreCase);
        return value.StartsWith("/Audio/", StringComparison.OrdinalIgnoreCase) || endpoint.Equals("stream", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Equals("download", StringComparison.OrdinalIgnoreCase) || endpoint.Equals("getTranscodeStream", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Equals("getTranscodeDecision", StringComparison.OrdinalIgnoreCase) || endpoint.Equals("File", StringComparison.OrdinalIgnoreCase) || endpoint.Contains("lyrics", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Equals("PlaybackInfo", StringComparison.OrdinalIgnoreCase);
    }

    private static SubsonicRequestParameters Parameters(HttpContext http) =>
        http.Items.TryGetValue(SubsonicAuthFilter.RequestParametersItemKey, out var value) && value is SubsonicRequestParameters parameters
            ? parameters : SubsonicRequestParameters.FromDictionary(http.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()));

    private IActionResult Refused(HttpContext http, ProtocolKind protocol)
    {
        if (protocol == ProtocolKind.Jellyfin) return new ObjectResult(new { error = CleanOnlyProtocolContent.UnavailableMessage }) { StatusCode = 403 };
        var result = _subsonic.CreateError(Parameters(http).GetValueOrDefault("f", "xml"), 50, CleanOnlyProtocolContent.UnavailableMessage);
        if (result is JsonResult json) json.StatusCode = 403;
        if (result is ContentResult text) text.StatusCode = 403;
        return result;
    }
}
